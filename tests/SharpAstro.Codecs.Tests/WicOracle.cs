using System.Diagnostics;
using System.Text;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Probes a JXR file through Windows Imaging Component (WIC) — what
/// Windows Photo, Microsoft Photos, and the Win32 file-preview pipeline
/// actually use to decode JXR. Stricter than the spec and (notably)
/// stricter than jxrlib's <c>JxrDecApp</c>, so this oracle catches
/// codestream issues that pass JxrDecApp but still produce a "this
/// file can't be opened" toast.
/// </summary>
/// <remarks>
/// <para>Shells out to PowerShell because PresentationCore (which hosts
/// <c>System.Windows.Media.Imaging.BitmapDecoder</c>) is a .NET Framework
/// surface that's awkward to load into a .NET 10 xunit host process —
/// PowerShell already has it in-process. Windows-only by definition.</para>
///
/// <para>Returns <see cref="WicResult"/> with the frames count + first
/// frame's dimensions / pixel format when WIC accepts the file, plus the
/// raw error message when it doesn't. <c>Frames == 0</c> is what Windows
/// Photo treats as "no valid image".</para>
/// </remarks>
public static class WicOracle
{
    public sealed record WicResult(
        bool Available,
        int Frames,
        int Width,
        int Height,
        string? PixelFormat,
        string? Error,
        string RawOutput)
    {
        /// <summary>Total bytes sampled by CopyPixels (top ~64 rows). 0 means CopyPixels wasn't run.</summary>
        public int Sampled { get; init; }
        /// <summary>Non-zero bytes in the sampled region. 0 with Sampled > 0 means full decode produced all-zero pixels — a silent decode failure that Frames=1 alone wouldn't catch.</summary>
        public int NonZero { get; init; }
        /// <summary>Hex of the first 64 sampled bytes — for value-level comparison with a reference encoder.</summary>
        public string? PixelHex { get; init; }

        public bool IsValidImage => Available && Frames > 0 && Error is null;
        public bool HasNonZeroPixels => Sampled > 0 && NonZero > 0;
    }

    public static WicResult Probe(string jxrPath)
    {
        if (!OperatingSystem.IsWindows())
            return new WicResult(false, 0, 0, 0, null, "WIC oracle is Windows-only", "");

        // CopyPixels into a managed buffer — Frames=1 alone is misleading
        // because WIC instantiates a frame even when the codestream is
        // malformed enough that the actual pixel decode produces garbage.
        // We additionally check that the decoded data isn't all-zero
        // (catches the BD16F sign-magnitude bug Task #11b: header looks
        // valid, frame instantiates, but every pixel decodes to 0).
        //
        // Also write the first 64 sampled bytes as hex (PXHEX=) so callers can
        // compare against a reference encoder's output to spot value-level
        // differences in WIC's decoded view of the codestream.
        var script = $@"
Add-Type -AssemblyName PresentationCore -ErrorAction Stop
try {{
    $d = [System.Windows.Media.Imaging.BitmapDecoder]::Create(
        (New-Object Uri '{jxrPath.Replace("'", "''")}'),
        [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
        [System.Windows.Media.Imaging.BitmapCacheOption]::None)
    Write-Output ""FRAMES=$($d.Frames.Count)""
    if ($d.Frames.Count -gt 0) {{
        $f = $d.Frames[0]
        Write-Output ""W=$($f.PixelWidth)""
        Write-Output ""H=$($f.PixelHeight)""
        Write-Output ""FMT=$($f.Format)""
        # CopyPixels to detect all-zero (silent decode failure).
        try {{
            $bpp = $f.Format.BitsPerPixel
            $stride = [int]([Math]::Ceiling($f.PixelWidth * $bpp / 8.0))
            # Sample first ~64 rows to avoid materialising huge buffers.
            $sampleRows = [Math]::Min($f.PixelHeight, 64)
            $bufSize = $stride * $sampleRows
            $buf = New-Object byte[] $bufSize
            $rect = New-Object System.Windows.Int32Rect 0, 0, $f.PixelWidth, $sampleRows
            $f.CopyPixels($rect, $buf, $stride, 0)
            $nonZero = 0
            for ($i = 0; $i -lt $buf.Length; $i++) {{ if ($buf[$i] -ne 0) {{ $nonZero++ }} }}
            Write-Output ""SAMPLED=$($buf.Length)""
            Write-Output ""NONZERO=$nonZero""
            $hexLen = [Math]::Min(64, $buf.Length)
            $hex = ($buf[0..($hexLen - 1)] | ForEach-Object {{ '{{0:X2}}' -f $_ }}) -join ''
            Write-Output ""PXHEX=$hex""
        }} catch {{
            Write-Output ""COPY_ERR=$($_.Exception.GetType().Name): $($_.Exception.Message)""
        }}
    }}
}} catch {{
    Write-Output ""ERR=$($_.Exception.GetType().Name): $($_.Exception.Message)""
    if ($_.Exception.InnerException) {{
        Write-Output ""INNER=$($_.Exception.InnerException.GetType().Name): $($_.Exception.InnerException.Message)""
    }}
}}";

        // Run via temp script file — stdin-piped scripts have flaky stdout
        // capture on some PowerShell versions.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wic_probe_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script);
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            File.Delete(scriptPath);
            return new WicResult(false, 0, 0, 0, null, "powershell.exe failed to start", "");
        }

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        File.Delete(scriptPath);

        // Parse FRAMES=N / W=N / H=N / FMT=name / ERR=message / SAMPLED=N / NONZERO=N lines.
        var frames = 0;
        int width = 0, height = 0;
        int sampled = 0, nonZero = 0;
        string? format = null;
        string? error = null;
        string? pixelHex = null;
        var raw = new StringBuilder();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            raw.AppendLine(trimmed);
            if (trimmed.StartsWith("FRAMES=")) int.TryParse(trimmed[7..], out frames);
            else if (trimmed.StartsWith("W=")) int.TryParse(trimmed[2..], out width);
            else if (trimmed.StartsWith("H=")) int.TryParse(trimmed[2..], out height);
            else if (trimmed.StartsWith("FMT=")) format = trimmed[4..];
            else if (trimmed.StartsWith("SAMPLED=")) int.TryParse(trimmed[8..], out sampled);
            else if (trimmed.StartsWith("NONZERO=")) int.TryParse(trimmed[8..], out nonZero);
            else if (trimmed.StartsWith("PXHEX=")) pixelHex = trimmed[6..];
            else if (trimmed.StartsWith("ERR=")) error = trimmed[4..];
            else if (trimmed.StartsWith("INNER=")) error = (error ?? "") + " // " + trimmed[6..];
        }
        if (!string.IsNullOrWhiteSpace(stderr)) raw.Append("STDERR: ").AppendLine(stderr);

        return new WicResult(Available: true, frames, width, height, format, error, raw.ToString())
        {
            Sampled = sampled,
            NonZero = nonZero,
            PixelHex = pixelHex,
        };
    }
}
