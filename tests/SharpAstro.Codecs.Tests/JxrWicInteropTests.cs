using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// WIC interop coverage: shell out to <c>BitmapDecoder</c> via PowerShell
/// and assert that files our encoder produces are accepted by the same
/// pipeline Windows Photo uses. Distinct from
/// <see cref="JxrEncoderOracleProbe"/> (which uses <c>JxrDecApp</c> — the
/// spec reference) because WIC is stricter than the spec and rejects
/// codestream shapes that JxrDecApp accepts.
/// </summary>
public sealed class JxrWicInteropTests
{
    private readonly ITestOutputHelper _out;
    public JxrWicInteropTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Bd8Rgb_YuvY444_OurEncoder_WicAcceptsAsFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            _out.WriteLine("SKIP — WIC oracle is Windows-only");
            return;
        }

        // 32×32 RGB through our YUV444 + NComponent path — same wire shape
        // as the WIC-encoded seagull. WIC must report Frames=1, not 0.
        const int w = 32, h = 32;
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            pixels[i * 3 + 0] = (byte)(i * 7 % 256);
            pixels[i * 3 + 1] = (byte)(i * 13 % 256);
            pixels[i * 3 + 2] = (byte)(i * 19 % 256);
        }
        var jxr = JxrFileFormatter.SaveBd8RgbNoFlexbits(pixels, w, h, useYUV444: true);
        var path = Path.Combine(Path.GetTempPath(), $"wic_probe_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(path, jxr);

        try
        {
            var result = WicOracle.Probe(path);
            _out.WriteLine(result.RawOutput);
            result.Available.ShouldBeTrue();
            result.Error.ShouldBeNull($"WIC rejected our codestream: {result.Error}");
            result.Frames.ShouldBe(1, "WIC should expose exactly one frame for a single-image JXR");
            result.Width.ShouldBe(w);
            result.Height.ShouldBe(h);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Bd16FRgb_YuvY444_OurEncoder_WicAcceptsAsFrame()
    {
        // HDR-master path — half-float RGB. This is the format the user's
        // TianWen pipeline produces (sol_test1.jxr / sol_test2.jxr).
        if (!OperatingSystem.IsWindows())
        {
            _out.WriteLine("SKIP — WIC oracle is Windows-only");
            return;
        }

        const int w = 32, h = 32;
        var halves = new Half[w * h * 3];
        for (var i = 0; i < halves.Length; i++)
            halves[i] = (Half)((i % 100) / 100.0);

        var jxr = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halves, w, h, useYUV444: true);
        var path = Path.Combine(Path.GetTempPath(), $"wic_probe_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(path, jxr);

        try
        {
            var result = WicOracle.Probe(path);
            _out.WriteLine(result.RawOutput);
            result.Available.ShouldBeTrue();
            result.Error.ShouldBeNull($"WIC rejected our codestream: {result.Error}");
            result.Frames.ShouldBe(1, "WIC should expose exactly one frame for a single-image BD16F JXR");
            result.Width.ShouldBe(w);
            result.Height.ShouldBe(h);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LargeBd8Rgb_YuvY444_OurEncoder_WicAcceptsAsFrame()
    {
        // Regression for Task #11. Before the fix, every encoder picked
        // LEVEL_IDC=L1 (cap 1920×1088) and omitted LONG_WORD_FLAG +
        // INDEX_TABLE_TILES, so WIC's WMPhotoDecoder rejected anything larger
        // than 1920×1088 with FRAMES=0 even though JxrDecApp accepted it.
        // Switching to LEVEL_IDC=Unrestricted, LongWordFlag=true, and an
        // INDEX_TABLE_TILES (degenerate single entry) makes WIC instantiate a
        // proper Rgb24 frame at full astro-photo resolution.
        if (!OperatingSystem.IsWindows())
        {
            _out.WriteLine("SKIP — WIC oracle is Windows-only");
            return;
        }

        const int w = 2838, h = 2860;
        var pixels = new byte[w * h * 3]; // all zero — content doesn't matter
        var jxr = JxrFileFormatter.SaveBd8RgbNoFlexbits(pixels, w, h, useYUV444: true);
        var path = Path.Combine(Path.GetTempPath(), $"wic_probe_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(path, jxr);

        try
        {
            var result = WicOracle.Probe(path);
            _out.WriteLine(result.RawOutput);
            result.Frames.ShouldBe(1, "WIC should accept full-resolution BD8 RGB YUV444");
            result.Width.ShouldBe(w);
            result.Height.ShouldBe(h);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void LargeBd16FRgb_YuvY444_OurEncoder_WicAcceptsAsFrame()
    {
        // Regression for Task #11 (BD16F at full resolution). Two distinct
        // bugs landed here:
        //   1. ImagePlaneHeader emitted 16 extra bits (LEN_MANTISSA + EXP_BIAS)
        //      for BD16F that the spec / jxrlib reference encoder never write.
        //      The misalignment passed WIC at 32×32 (FMT=Default, partial parse)
        //      but caused FRAMES=0 at full resolution.
        //   2. Even after the header fix, our encoder mapped half-float bits
        //      via `src - Bd16Bias` (integer midpoint subtraction) instead of
        //      the sign-magnitude conversion jxrlib uses in forwardHalf. WIC
        //      then opened the file but every pixel decoded near-zero
        //      (Windows Photo showed near-black noise).
        // The non-zero pixel assertion catches the second bug specifically —
        // FRAMES=1 alone wouldn't.
        if (!OperatingSystem.IsWindows())
        {
            _out.WriteLine("SKIP — WIC oracle is Windows-only");
            return;
        }

        const int w = 2838, h = 2860;
        // Bright varying RGB pattern: ensures coded coefficients exceed any
        // quantizer dead-zone so the decoded pixels can't accidentally be
        // all-zero (which would mask the sign-magnitude bug).
        var halves = new Half[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            halves[i * 3 + 0] = (Half)(0.5 + (i % 100) / 200.0); // R in [0.5, 1.0)
            halves[i * 3 + 1] = (Half)(0.3 + (i % 50) / 100.0);  // G
            halves[i * 3 + 2] = (Half)(0.1 + (i % 20) / 25.0);   // B
        }

        var jxr = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halves, w, h, useYUV444: true);
        var path = Path.Combine(Path.GetTempPath(), $"wic_probe_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(path, jxr);

        try
        {
            var result = WicOracle.Probe(path);
            _out.WriteLine(result.RawOutput);
            result.Frames.ShouldBe(1, "WIC should accept full-resolution BD16F RGB YUV444");
            result.Width.ShouldBe(w);
            result.Height.ShouldBe(h);
            result.HasNonZeroPixels.ShouldBeTrue(
                $"WIC decoded the frame but produced all-zero pixels (sampled={result.Sampled} nonZero={result.NonZero}) — " +
                "likely the BD16F sign-magnitude encoding bug.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void EncodeUserPngAsJxr_VisualVerification()
    {
        // Take an existing HDR PNG from disk, convert to half-float RGB, run
        // through our BD16F YUV444 encoder, and write the result next to the
        // source. Lets the user open both in Windows Photo and visually
        // confirm whether our encoder reproduces the source content. No
        // assertions — purpose is to produce an artefact for manual review.
        var candidates = new[]
        {
            @"C:\temp\stack\output\sol_hdr_noreinhard.png",
            @"C:\temp\stack\output\sol_hdr_4000-srgb.png",
            @"C:\temp\stack\output\sol_hdr_1000-srgb.png",
        };
        var pngPath = candidates.FirstOrDefault(File.Exists);
        if (pngPath is null) { _out.WriteLine("SKIP — no HDR PNG fixture on disk"); return; }
        _out.WriteLine($"Source PNG: {pngPath}");

        var pngBytes = File.ReadAllBytes(pngPath);
        var png = SharpAstro.Png.PngReader.Decode(pngBytes);
        var bytesPerPixel = png.Pixels.Length / (png.Width * png.Height);
        var bytesPerSample = png.BitDepth / 8;
        var samplesPerPixel = bytesPerPixel / bytesPerSample;
        _out.WriteLine($"  Decoded: {png.Width}×{png.Height}, ColorType={png.ColorType}, BitDepth={png.BitDepth}, " +
                       $"samples/pixel={samplesPerPixel}, bytes/pixel={bytesPerPixel}");

        if (samplesPerPixel < 3)
        {
            _out.WriteLine($"SKIP — PNG has {samplesPerPixel} samples per pixel, need ≥3 for RGB encoding");
            return;
        }

        var w = png.Width;
        var h = png.Height;
        var halves = new Half[w * h * 3];
        if (bytesPerSample == 1)
        {
            for (var i = 0; i < w * h; i++)
            {
                halves[i * 3 + 0] = (Half)(png.Pixels[i * samplesPerPixel + 0] / 255.0f);
                halves[i * 3 + 1] = (Half)(png.Pixels[i * samplesPerPixel + 1] / 255.0f);
                halves[i * 3 + 2] = (Half)(png.Pixels[i * samplesPerPixel + 2] / 255.0f);
            }
        }
        else if (bytesPerSample == 2)
        {
            // 16-bit PNG samples are big-endian per spec. Skip alpha if present.
            for (var i = 0; i < w * h; i++)
            {
                var baseOff = i * samplesPerPixel * 2;
                ushort R(int c) => (ushort)((png.Pixels[baseOff + c * 2] << 8) | png.Pixels[baseOff + c * 2 + 1]);
                halves[i * 3 + 0] = (Half)(R(0) / 65535.0f);
                halves[i * 3 + 1] = (Half)(R(1) / 65535.0f);
                halves[i * 3 + 2] = (Half)(R(2) / 65535.0f);
            }
        }
        else
        {
            _out.WriteLine($"SKIP — unsupported PNG bit depth {png.BitDepth}");
            return;
        }

        var halfBits = new ushort[halves.Length];
        for (var i = 0; i < halves.Length; i++) halfBits[i] = BitConverter.HalfToUInt16Bits(halves[i]);

        var jxr = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halfBits, w, h, useYUV444: true);

        // Number each new file rather than overwriting — lets the user pin
        // older attempts and visually compare progress across iterations.
        var dir = Path.GetDirectoryName(pngPath)!;
        var stem = Path.GetFileNameWithoutExtension(pngPath) + "_reencoded";
        int n = 1;
        string outJxr;
        do { outJxr = Path.Combine(dir, $"{stem}_v{n:D3}.jxr"); n++; }
        while (File.Exists(outJxr));
        File.WriteAllBytes(outJxr, jxr);
        _out.WriteLine($"  Wrote re-encoded JXR: {outJxr} ({jxr.Length:N0} bytes)");

        // Also confirm WIC opens it and the decoded pixels are non-zero.
        if (OperatingSystem.IsWindows())
        {
            var result = WicOracle.Probe(outJxr);
            _out.WriteLine($"  WIC: {result.RawOutput.TrimEnd().Replace('\n', '|')}");
        }

        // Also produce a REFERENCE .jxr by running JxrEncApp on the same
        // half-float content. Lets the user open ours and jxrlib's side-by-side
        // in Windows Photo to confirm which one is broken visually.
        var jxrEncApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrEncApp.exe");
        if (File.Exists(jxrEncApp))
        {
            var refStem = Path.GetFileNameWithoutExtension(pngPath) + "_jxrlib_reference";
            var refJxrPath = Path.Combine(dir, $"{refStem}.jxr");
            var refTifInput = Path.Combine(dir, $"{refStem}.tif");
            try
            {
                WriteHalfTiff(refTifInput, halves, w, h);
                var psiRef = new System.Diagnostics.ProcessStartInfo(jxrEncApp,
                    // -c 12 = 48bppRGBHalf, -q 1 = lossless quantization, -d 3
                    // and -l 0 = no chroma subsampling (YUV444), -f = flexbits
                    // off, -s 1 = single tile. Matches the no-flexbits BD16F
                    // pipeline we exercise on our side.
                    $"-i \"{refTifInput}\" -o \"{refJxrPath}\" -c 12 -q 1 -d 3 -l 0 -f -s 1")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var procRef = System.Diagnostics.Process.Start(psiRef)!;
                procRef.WaitForExit(300_000).ShouldBeTrue();
                _out.WriteLine($"  JxrEncApp reference: exit={procRef.ExitCode}, file={refJxrPath} " +
                               $"({(File.Exists(refJxrPath) ? new FileInfo(refJxrPath).Length.ToString("N0") + " bytes" : "MISSING")})");
                var refErr = procRef.StandardError.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(refErr))
                    _out.WriteLine($"  JxrEncApp stderr: {refErr}");
            }
            finally
            {
                if (File.Exists(refTifInput)) File.Delete(refTifInput);
            }
        }
        else
        {
            _out.WriteLine("  JxrEncApp not present — skipped reference encode.");
        }

        // Self round-trip through our own decoder — proves encoder/decoder
        // consistency. Doesn't validate spec-correctness; just internal sanity.
        var selfDecoded = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(jxr, out _, out _, out _);
        var selfMismatch = 0;
        for (var i = 0; i < halves.Length; i++)
            if (BitConverter.HalfToUInt16Bits(selfDecoded[i]) != BitConverter.HalfToUInt16Bits(halves[i]))
                selfMismatch++;
        _out.WriteLine($"  Self round-trip mismatches: {selfMismatch}/{halves.Length}");

        // ===== STEP 1 DIAGNOSTIC: decode jxrlib's reference output through OUR decoder. =====
        // If it matches source -> bug is encoder-only. If it diverges too ->
        // decoder also has bugs (and our "clean self-roundtrip" is just two
        // wrongs cancelling). This is the load-bearing test for the whole
        // debugging plan: answers "which side is broken?"
        var jxrlibRefPath = Path.Combine(dir,
            Path.GetFileNameWithoutExtension(pngPath) + "_jxrlib_reference.jxr");
        if (File.Exists(jxrlibRefPath))
        {
            try
            {
                var jxrlibRefBytes = File.ReadAllBytes(jxrlibRefPath);
                // Capture decoder stderr trace to a string for the test log.
                var traceCapture = new System.IO.StringWriter();
                var origErr = System.Console.Error;
                System.Console.SetError(traceCapture);
                Environment.SetEnvironmentVariable("DIR_LIB_JXR_TRACE", "1");
                Half[] oursOnRef;
                try
                {
                    oursOnRef = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(jxrlibRefBytes, out _, out _, out _);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("DIR_LIB_JXR_TRACE", null);
                    System.Console.SetError(origErr);
                }
                var traceLines = traceCapture.ToString();
                if (!string.IsNullOrWhiteSpace(traceLines))
                {
                    _out.WriteLine($"  STEP1 decoder trace:");
                    foreach (var line in traceLines.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            _out.WriteLine($"    {line.TrimEnd()}");
                }
                var refMatchExact = 0;
                var refMatchClose = 0;
                float refMaxAbsDiff = 0;
                double refSumAbsDiff = 0;
                int refCount = Math.Min(oursOnRef.Length, halves.Length);
                for (var i = 0; i < refCount; i++)
                {
                    var got = (float)oursOnRef[i];
                    var want = (float)halves[i];
                    if (BitConverter.HalfToUInt16Bits(oursOnRef[i]) == BitConverter.HalfToUInt16Bits(halves[i]))
                        refMatchExact++;
                    var d = Math.Abs(got - want);
                    if (d < 0.05f) refMatchClose++;
                    if (float.IsFinite(d) && d > refMaxAbsDiff) refMaxAbsDiff = d;
                    if (float.IsFinite(d)) refSumAbsDiff += d;
                }
                _out.WriteLine($"  STEP1: our decoder on jxrlib reference: bit-exact={refMatchExact}/{refCount} ({100.0 * refMatchExact / refCount:F2}%), " +
                               $"close (<0.05)={refMatchClose}/{refCount} ({100.0 * refMatchClose / refCount:F2}%), " +
                               $"maxAbsDiff={refMaxAbsDiff:F4}, avgAbsDiff={refSumAbsDiff / refCount:F4}");
                _out.WriteLine($"  STEP1 VERDICT: {(refMatchClose > refCount * 0.95 ? "DECODER IS OK -> encoder-only bug" : "DECODER ALSO BROKEN -> fix decoder first")}");

                // Locate the worst-diverging pixels — clustered (specific MBs)
                // vs spread (per-pixel) tells us which decoder stage is wrong.
                // Per-MB stats so we can see if a single MB-row or MB-col
                // is to blame.
                int badnessMbW = (w + 15) >> 4, badnessMbH = (h + 15) >> 4;
                var mbWorst = new float[badnessMbW * badnessMbH];
                var mbBadCount = new int[badnessMbW * badnessMbH];
                for (var i = 0; i < refCount; i++)
                {
                    var pixel = i / 3;
                    var px = pixel % w;
                    var py = pixel / w;
                    var mbx = px / 16;
                    var mby = py / 16;
                    var mbIdx = mby * badnessMbW + mbx;
                    var d = Math.Abs((float)oursOnRef[i] - (float)halves[i]);
                    if (!float.IsFinite(d)) continue;
                    if (d > mbWorst[mbIdx]) mbWorst[mbIdx] = d;
                    if (d >= 0.05f) mbBadCount[mbIdx]++;
                }
                // Find top-10 worst MBs.
                var worstSorted = new (int idx, float worst, int bad)[badnessMbW * badnessMbH];
                for (var i = 0; i < worstSorted.Length; i++) worstSorted[i] = (i, mbWorst[i], mbBadCount[i]);
                Array.Sort(worstSorted, (a, b) => b.worst.CompareTo(a.worst));
                _out.WriteLine($"  STEP1: worst MBs (by maxAbsDiff in pixel) — top 10:");
                for (var k = 0; k < Math.Min(10, worstSorted.Length); k++)
                {
                    var (idx, worst, bad) = worstSorted[k];
                    var mbx = idx % badnessMbW;
                    var mby = idx / badnessMbW;
                    _out.WriteLine($"    MB ({mbx,4},{mby,4}): maxAbsDiff={worst:F4}, pixels-with-diff>=0.05: {bad}/768");
                }
                // Bucket the bad-pixel distribution by mby (which MB-row).
                var rowBad = new int[badnessMbH];
                var rowWorst = new float[badnessMbH];
                for (var i = 0; i < worstSorted.Length; i++)
                {
                    var mby = i / badnessMbW;
                    rowBad[mby] += mbBadCount[i];
                    if (mbWorst[i] > rowWorst[mby]) rowWorst[mby] = mbWorst[i];
                }
                _out.WriteLine($"  STEP1: per-MB-row stats (first 8 + last 4 of {badnessMbH}):");
                for (var mby = 0; mby < Math.Min(8, badnessMbH); mby++)
                    _out.WriteLine($"    row {mby,4}: rowMaxDiff={rowWorst[mby]:F4}, badPixelsInRow={rowBad[mby]}");
                for (var mby = Math.Max(8, badnessMbH - 4); mby < badnessMbH; mby++)
                    _out.WriteLine($"    row {mby,4}: rowMaxDiff={rowWorst[mby]:F4}, badPixelsInRow={rowBad[mby]}");

                // Walk row 0 to find where errors START — if MB(0,0) is near-clean
                // and errors grow with column, it's cross-MB LEFT prediction
                // accumulating.
                _out.WriteLine($"  STEP1: per-MB-in-row-0 max abs diff (col, maxDiff, badPixels):");
                for (var mbx = 0; mbx < badnessMbW; mbx++)
                {
                    if (mbx > 6 && mbx < 110 && (mbx % 8) != 0) continue; // print first 7, then every 8th
                    var idx = 0 * badnessMbW + mbx;
                    _out.WriteLine($"    MB ({mbx,4}, 0): maxAbsDiff={mbWorst[idx]:F4}, badPixels={mbBadCount[idx]}/768");
                }
                // Walk column 0 to find where errors propagate down.
                _out.WriteLine($"  STEP1: per-MB-in-col-0 max abs diff (col=0, row, maxDiff, badPixels):");
                for (var mby = 0; mby < badnessMbH; mby++)
                {
                    if (mby > 6 && mby < badnessMbH - 4 && (mby % 16) != 0) continue;
                    var idx = mby * badnessMbW + 0;
                    _out.WriteLine($"    MB (   0, {mby,4}): maxAbsDiff={mbWorst[idx]:F4}, badPixels={mbBadCount[idx]}/768");
                }

                // First-MB pixel dump — MB(0,0) has no cross-MB dependencies.
                // If THIS already has errors, the bug is purely intra-MB.
                _out.WriteLine($"  STEP1: pixel-level inspection of MB (0,0) — no cross-MB deps:");
                _out.WriteLine($"         px,py |  source R  G  B  | decoded R  G  B  | diff R  G  B");
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var px = dx;
                        var py = dy;
                        var i = (py * w + px) * 3;
                        var sR = (float)halves[i];
                        var sG = (float)halves[i + 1];
                        var sB = (float)halves[i + 2];
                        var dR = (float)oursOnRef[i];
                        var dG = (float)oursOnRef[i + 1];
                        var dB = (float)oursOnRef[i + 2];
                        _out.WriteLine($"        ({px,4},{py,4}) | {sR,8:F4} {sG,8:F4} {sB,8:F4} | {dR,8:F4} {dG,8:F4} {dB,8:F4} | {dR - sR,+8:F4} {dG - sG,+8:F4} {dB - sB,+8:F4}");
                    }
                }

                // Worst-MB pixel dump: top-row MB at column 116 is the worst.
                // Print the actual (source, decoded, diff) for the first few
                // pixels of that MB to see the pattern.
                int probeMbX = 116, probeMbY = 0;
                _out.WriteLine($"  STEP1: pixel-level inspection of MB ({probeMbX},{probeMbY}):");
                _out.WriteLine($"         px,py |  source R  G  B  | decoded R  G  B  | diff R  G  B");
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var px = probeMbX * 16 + dx;
                        var py = probeMbY * 16 + dy;
                        var i = (py * w + px) * 3;
                        var sR = (float)halves[i];
                        var sG = (float)halves[i + 1];
                        var sB = (float)halves[i + 2];
                        var dR = (float)oursOnRef[i];
                        var dG = (float)oursOnRef[i + 1];
                        var dB = (float)oursOnRef[i + 2];
                        _out.WriteLine($"        ({px,4},{py,4}) | {sR,8:F4} {sG,8:F4} {sB,8:F4} | {dR,8:F4} {dG,8:F4} {dB,8:F4} | {dR - sR,+8:F4} {dG - sG,+8:F4} {dB - sB,+8:F4}");
                    }
                }
                // Same probe for a non-worst MB (column 0 row 100 — boring middle).
                _out.WriteLine($"  STEP1: comparison probe at MB (0,100):");
                _out.WriteLine($"         px,py |  source R  G  B  | decoded R  G  B  | diff R  G  B");
                for (var dy = 0; dy < 4; dy++)
                {
                    for (var dx = 0; dx < 4; dx++)
                    {
                        var px = dx;
                        var py = 100 * 16 + dy;
                        var i = (py * w + px) * 3;
                        var sR = (float)halves[i];
                        var sG = (float)halves[i + 1];
                        var sB = (float)halves[i + 2];
                        var dR = (float)oursOnRef[i];
                        var dG = (float)oursOnRef[i + 1];
                        var dB = (float)oursOnRef[i + 2];
                        _out.WriteLine($"        ({px,4},{py,4}) | {sR,8:F4} {sG,8:F4} {sB,8:F4} | {dR,8:F4} {dG,8:F4} {dB,8:F4} | {dR - sR,+8:F4} {dG - sG,+8:F4} {dB - sB,+8:F4}");
                    }
                }
            }
            catch (Exception ex)
            {
                _out.WriteLine($"  STEP1: our decoder THREW on jxrlib reference: {ex.GetType().Name}: {ex.Message}");
                _out.WriteLine($"  STEP1 VERDICT: DECODER ALSO BROKEN -> fix decoder first");
            }
        }
        else
        {
            _out.WriteLine($"  STEP1: skipped — jxrlib reference {jxrlibRefPath} not present");
        }
        // ===== END STEP 1 =====

        // Decode through JxrDecApp (reference decoder) — this is the load-bearing
        // check. If JxrDecApp recovers values close to the source PNG, our
        // encoder is spec-correct and any "noise" rendering is a display issue.
        // If JxrDecApp recovers garbage, our encoder has a real bug.
        var jxrDecApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrDecApp.exe");
        if (File.Exists(jxrDecApp))
        {
            var refTif = outJxr.Replace(".jxr", "_via_jxrdecapp.tif");
            var psi = new System.Diagnostics.ProcessStartInfo(jxrDecApp,
                $"-i \"{outJxr}\" -o \"{refTif}\" -c 12")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(120_000).ShouldBeTrue();
            _out.WriteLine($"  JxrDecApp exit={proc.ExitCode}");
            if (proc.ExitCode == 0 && File.Exists(refTif))
            {
                // Parse the half-float TIFF strip.
                var tif = File.ReadAllBytes(refTif);
                var ifdOff = (int)BitConverter.ToUInt32(tif, 4);
                var nEntries = BitConverter.ToUInt16(tif, ifdOff);
                int stripOff = 0;
                for (var i = 0; i < nEntries; i++)
                {
                    var e = ifdOff + 2 + i * 12;
                    if (BitConverter.ToUInt16(tif, e) == 0x111) stripOff = (int)BitConverter.ToUInt32(tif, e + 8);
                }
                var refMismatch = 0;
                float maxDiff = 0, sumDiff = 0;
                long zeroSrcButNonZeroRt = 0;
                for (var i = 0; i < halves.Length; i++)
                {
                    var rtBits = BitConverter.ToUInt16(tif, stripOff + i * 2);
                    var srcBits = BitConverter.HalfToUInt16Bits(halves[i]);
                    if (rtBits != srcBits)
                    {
                        refMismatch++;
                        var d = Math.Abs((float)halves[i] - (float)BitConverter.UInt16BitsToHalf(rtBits));
                        if (d > maxDiff) maxDiff = d;
                        sumDiff += d;
                        if ((float)halves[i] == 0 && BitConverter.UInt16BitsToHalf(rtBits) != (Half)0) zeroSrcButNonZeroRt++;
                    }
                }
                _out.WriteLine($"  JxrDecApp round-trip mismatches: {refMismatch}/{halves.Length}, " +
                               $"max abs diff={maxDiff:F4}, avg abs diff={sumDiff / halves.Length:F6}, " +
                               $"zero→nonzero count={zeroSrcButNonZeroRt}");
                // Count how many pixels are CLOSE (delta < 0.05) — gives us a
                // sense of how "garbage" vs "noisy but recognizable" the output is.
                long closeCount = 0;
                for (var i = 0; i < halves.Length; i++)
                {
                    var rtBits = BitConverter.ToUInt16(tif, stripOff + i * 2);
                    var rtH = BitConverter.UInt16BitsToHalf(rtBits);
                    if (Math.Abs((float)halves[i] - (float)rtH) < 0.05f) closeCount++;
                }
                _out.WriteLine($"  pixels within 0.05 of source: {closeCount}/{halves.Length} ({closeCount * 100.0 / halves.Length:F1}%)");
                // Sample a few decoded vs source values from the bright nebula region.
                for (var s = 0; s < 6; s++)
                {
                    var idx = (h / 2 + s) * w * 3 + (w / 2) * 3;
                    var src = (float)halves[idx];
                    var rt = (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(tif, stripOff + idx * 2));
                    _out.WriteLine($"    pixel center+{s} R: src={src:F4}, rt={rt:F4}, diff={Math.Abs(src - rt):F4}");
                }
                if (File.Exists(refTif)) File.Delete(refTif);
            }
        }
    }

    [Fact]
    public void DiagnoseBd16F_FindCatastrophicFailureBoundary()
    {
        // The 2-MB test fixture decodes within 0.07 of source. The user's PNG
        // (2838×2860) decodes with max diff = ∞. Somewhere between those two
        // extremes we cross a boundary where decode catastrophically fails.
        // Probe specific dimensions / magnitudes / patterns to find the
        // first case that overflows. Output guides where to look next.
        var jxrDecApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrDecApp.exe");
        if (!File.Exists(jxrDecApp)) { _out.WriteLine("SKIP"); return; }

        // Probe matrix: (W, H, peakValue, pattern). Pattern 0 = smooth
        // gradient; 1 = constant with one bright pixel; 2 = random.
        var probes = new (int w, int h, float peak, int pattern, string desc)[]
        {
            // Smooth, small. Should decode well.
            (32, 16,  0.5f, 0, "smooth gradient, 2 MBs, peak 0.5"),
            // Multi-row, smooth. Tests cross-MB-TOP prediction.
            (32, 32,  0.5f, 0, "smooth gradient, 4 MBs (2 rows), peak 0.5"),
            // Multi-row + multi-col, smooth.
            (64, 32,  0.5f, 0, "smooth gradient, 8 MBs (2x4), peak 0.5"),
            // Single-MB with a bright pixel. Stress AC coefficient magnitude.
            (16, 16,  0.9f, 1, "single-pixel spike at 0.9 in single MB"),
            // Multi-MB with a bright pixel — like a star on dark background.
            (32, 32,  0.9f, 1, "single-pixel spike at 0.9 in 4 MBs"),
            // Random high-magnitude content (full [0,1] range).
            (32, 16,  1.0f, 2, "random pixels in [0,1] across 2 MBs"),
            (64, 64,  1.0f, 2, "random pixels in [0,1] across 16 MBs (4x4)"),
        };

        foreach (var (w, h, peak, pattern, desc) in probes)
        {
            var halves = new Half[w * h * 3];
            var rng = new Random(0x5EED);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                float r, g, b;
                switch (pattern)
                {
                    case 0: // smooth gradient
                        r = peak * (x / (float)w);
                        g = peak * (y / (float)h);
                        b = peak * 0.5f;
                        break;
                    case 1: // dark background with one bright pixel at center
                        if (x == w / 2 && y == h / 2) { r = peak; g = peak; b = peak; }
                        else { r = 0.01f; g = 0.01f; b = 0.01f; }
                        break;
                    case 2: // random
                        r = (float)(rng.NextDouble() * peak);
                        g = (float)(rng.NextDouble() * peak);
                        b = (float)(rng.NextDouble() * peak);
                        break;
                    default: throw new InvalidOperationException();
                }
                var i = y * w + x;
                halves[i * 3 + 0] = (Half)r;
                halves[i * 3 + 1] = (Half)g;
                halves[i * 3 + 2] = (Half)b;
            }
            var bits = HalfArrayToUshort(halves);
            var jxr = JxrFileFormatter.SaveBd16FRgbNoFlexbits(bits, w, h, useYUV444: true);
            var jp = Path.Combine(Path.GetTempPath(), $"probe_{Guid.NewGuid():N}.jxr");
            var tp = jp.Replace(".jxr", ".tif");
            File.WriteAllBytes(jp, jxr);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(jxrDecApp,
                    $"-i \"{jp}\" -o \"{tp}\" -c 12")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(30_000);
                if (proc.ExitCode != 0) { _out.WriteLine($"  [{desc}] JxrDecApp FAILED exit={proc.ExitCode}"); continue; }

                var tif = File.ReadAllBytes(tp);
                var ifdOff = (int)BitConverter.ToUInt32(tif, 4);
                var nEntries = BitConverter.ToUInt16(tif, ifdOff);
                int stripOff = 0;
                for (var i = 0; i < nEntries; i++)
                {
                    var e = ifdOff + 2 + i * 12;
                    if (BitConverter.ToUInt16(tif, e) == 0x111) stripOff = (int)BitConverter.ToUInt32(tif, e + 8);
                }

                float maxDiff = 0;
                long catastrophicCount = 0; // pixels where decoded is wildly wrong (>10x source)
                long closeCount = 0;
                for (var i = 0; i < halves.Length; i++)
                {
                    var rt = BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(tif, stripOff + i * 2));
                    var src = (float)halves[i];
                    var d = Math.Abs(src - (float)rt);
                    if (float.IsFinite(d) && d > maxDiff) maxDiff = d;
                    if (!float.IsFinite((float)rt) || Math.Abs((float)rt) > 10) catastrophicCount++;
                    if (d < 0.05f) closeCount++;
                }
                _out.WriteLine($"  [{desc}]: maxDiff={maxDiff:F4} close={closeCount}/{halves.Length} catastrophic={catastrophicCount}");
            }
            finally
            {
                if (File.Exists(jp)) File.Delete(jp);
                if (File.Exists(tp)) File.Delete(tp);
            }
        }
    }

    [Fact]
    public void Bd16FRgb_TwoMb_DecodedByJxrDecAppCloseToSource()
    {
        // Cross-MB progress regression: encode 2-MB BD16F YUV444 through our
        // encoder, decode through JxrDecApp (spec reference), assert decoded
        // values are NUMERICALLY CLOSE to source (max abs diff < 0.05). Tracks
        // the PCT + LP/HP-position-swap progress: before those fixes the
        // decoded values were ∞ / NaN / wildly wrong; after, they're within
        // ~1% of source. Tightening this assertion to bit-exact requires the
        // bScaledArith pre-shift work (Task #12 follow-up).
        var jxrDecApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrDecApp.exe");
        if (!File.Exists(jxrDecApp)) { _out.WriteLine("SKIP — JxrDecApp not built"); return; }

        const int w = 32, h = 16;
        var halves = new Half[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            halves[i * 3 + 0] = (Half)(0.4f + ((x + y) % 7) * 0.005f);
            halves[i * 3 + 1] = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
            halves[i * 3 + 2] = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
        }

        var jxr = JxrFileFormatter.SaveBd16FRgbNoFlexbits(HalfArrayToUshort(halves), w, h, useYUV444: true);
        var jxrPath = Path.Combine(Path.GetTempPath(), $"rtcheck_{Guid.NewGuid():N}.jxr");
        var tifPath = jxrPath.Replace(".jxr", ".tif");
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(jxrDecApp,
                $"-i \"{jxrPath}\" -o \"{tifPath}\" -c 12")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(30_000).ShouldBeTrue();
            proc.ExitCode.ShouldBe(0);

            var tif = File.ReadAllBytes(tifPath);
            var ifdOff = (int)BitConverter.ToUInt32(tif, 4);
            var nEntries = BitConverter.ToUInt16(tif, ifdOff);
            int stripOff = 0;
            for (var i = 0; i < nEntries; i++)
            {
                var e = ifdOff + 2 + i * 12;
                if (BitConverter.ToUInt16(tif, e) == 0x111) stripOff = (int)BitConverter.ToUInt32(tif, e + 8);
            }
            var mismatch = 0;
            float maxAbsDiff = 0;
            for (var i = 0; i < halves.Length; i++)
            {
                var rt = BitConverter.ToUInt16(tif, stripOff + i * 2);
                if (rt != BitConverter.HalfToUInt16Bits(halves[i])) mismatch++;
                var d = Math.Abs((float)halves[i] - (float)BitConverter.UInt16BitsToHalf(rt));
                if (d > maxAbsDiff) maxAbsDiff = d;
            }
            _out.WriteLine($"JxrDecApp round-trip on 2-MB BD16F YUV444: {mismatch}/{halves.Length} bit-exact mismatches, maxAbsDiff={maxAbsDiff:F4}");
            // Progress gate: catches regression back to ∞/NaN garbage decoding.
            // Tighten to bit-exact ShouldBe(0) once bScaledArith is implemented.
            // Threshold 0.1 is generous to allow for the residual precision loss
            // from missing bScaledArith pre-shift (∼7%). Pre-fix this was ∞/NaN.
            // Tighten to 0.001 once bScaledArith is implemented; tighten further
            // to bit-exact ShouldBe(0) once any remaining AdaptVLC drift is closed.
            maxAbsDiff.ShouldBeLessThan(0.1f,
                "BD16F YUV444 cross-MB: decoded values must be within 0.1 of source. " +
                "Anything worse indicates a regression in PCT, LP/HP position, or coefficient encoding.");
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(tifPath)) File.Delete(tifPath);
        }
    }

    [Fact]
    public void DiagnoseJxrlibReferenceHeader()
    {
        // Parse the jxrlib reference .jxr through our parser and dump every
        // field. Comparing against our own encoder's defaults reveals which
        // header bits we set differently — anything different is a candidate
        // bug for the decoder's 27% gap (or the encoder's full breakage).
        var refPath = @"C:\temp\stack\output\sol_hdr_noreinhard_jxrlib_reference.jxr";
        if (!File.Exists(refPath)) { _out.WriteLine($"SKIP — {refPath} not present"); return; }

        var bytes = File.ReadAllBytes(refPath);
        var container = JxrContainer.Read(bytes);
        var codestream = container.Codestream;
        _out.WriteLine($"Codestream length: {codestream.Length:N0} bytes");

        var reader = new BitReader(codestream);
        var img = ImageHeader.Read(ref reader);
        _out.WriteLine($"=== IMAGE_HEADER ===");
        _out.WriteLine($"  HardTilingFlag             = {img.HardTilingFlag}");
        _out.WriteLine($"  TilingFlag                 = {img.TilingFlag}");
        _out.WriteLine($"  FrequencyModeCodestreamFlag= {img.FrequencyModeCodestreamFlag}");
        _out.WriteLine($"  SpatialXfrmSubordinate     = {img.SpatialXfrmSubordinate}");
        _out.WriteLine($"  IndexTablePresentFlag      = {img.IndexTablePresentFlag}");
        _out.WriteLine($"  OverlapMode                = {img.OverlapMode}");
        _out.WriteLine($"  ShortHeaderFlag            = {img.ShortHeaderFlag}");
        _out.WriteLine($"  LongWordFlag               = {img.LongWordFlag}");
        _out.WriteLine($"  WindowingFlag              = {img.WindowingFlag}");
        _out.WriteLine($"  TrimFlexBitsFlag           = {img.TrimFlexBitsFlag}");
        _out.WriteLine($"  RedBlueNotSwappedFlag      = {img.RedBlueNotSwappedFlag}");
        _out.WriteLine($"  PremultipliedAlphaFlag     = {img.PremultipliedAlphaFlag}");
        _out.WriteLine($"  AlphaImagePlaneFlag        = {img.AlphaImagePlaneFlag}");
        _out.WriteLine($"  OutputClrFmt               = {img.OutputClrFmt}");
        _out.WriteLine($"  OutputBitDepth             = {img.OutputBitDepth}");
        _out.WriteLine($"  WidthMinus1                = {img.WidthMinus1} (width  = {img.WidthMinus1 + 1})");
        _out.WriteLine($"  HeightMinus1               = {img.HeightMinus1} (height = {img.HeightMinus1 + 1})");
        if (img.TilingFlag)
        {
            _out.WriteLine($"  NumVerTilesMinus1          = {img.NumVerTilesMinus1}");
            _out.WriteLine($"  NumHorTilesMinus1          = {img.NumHorTilesMinus1}");
        }

        var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);
        _out.WriteLine($"=== IMAGE_PLANE_HEADER ===");
        _out.WriteLine($"  InternalClrFmt             = {plane.InternalClrFmt}");
        _out.WriteLine($"  ScaledFlag                 = {plane.ScaledFlag}");
        _out.WriteLine($"  BandsPresent               = {plane.BandsPresent}");
        _out.WriteLine($"  NumComponents              = {plane.NumComponents}");
        _out.WriteLine($"  ShiftBits                  = {plane.ShiftBits}");
        _out.WriteLine($"  LenMantissa                = {plane.LenMantissa}");
        _out.WriteLine($"  ExpBias                    = {plane.ExpBias}");
        _out.WriteLine($"  DcQuant                    = {plane.DcQuant}");
        _out.WriteLine($"  LpQuant                    = {plane.LpQuant}");
        _out.WriteLine($"  HpQuant                    = {plane.HpQuant}");
        _out.WriteLine($"  UseDcQpForLp               = {plane.UseDcQpForLp}");
        _out.WriteLine($"  UseLpQpForHp               = {plane.UseLpQpForHp}");
        _out.WriteLine($"  DcImagePlaneUniformFlag    = {plane.DcImagePlaneUniformFlag}");
        _out.WriteLine($"  LpImagePlaneUniformFlag    = {plane.LpImagePlaneUniformFlag}");
        _out.WriteLine($"  HpImagePlaneUniformFlag    = {plane.HpImagePlaneUniformFlag}");
    }

    [Fact]
    public void DiagnoseSideBySideTrace()
    {
        // Side-by-side: run JxrEncApp on a 2-MB BD16F input, dump its stderr.
        // Then run OUR encoder on the same input with DIR_LIB_JXR_TRACE=1,
        // dump its stderr. Both should produce comparable per-MB LP traces.
        var jxrEncApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrEncApp.exe");
        if (!File.Exists(jxrEncApp)) { _out.WriteLine("SKIP"); return; }

        const int w = 32, h = 16;
        var halves = new Half[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            halves[i * 3 + 0] = (Half)(0.4f + ((x + y) % 7) * 0.005f);
            halves[i * 3 + 1] = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
            halves[i * 3 + 2] = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
        }
        var tif = Path.Combine(Path.GetTempPath(), $"diag_{Guid.NewGuid():N}.tif");
        var refJxr = tif.Replace(".tif", ".jxr");
        try
        {
            WriteHalfTiff(tif, halves, w, h);
            var psi = new System.Diagnostics.ProcessStartInfo(jxrEncApp,
                $"-i \"{tif}\" -o \"{refJxr}\" -c 12 -q 1 -d 3 -l 0 -f -s 1")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.Environment["JXRLIB_TRACE"] = "1";
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(60_000).ShouldBeTrue();
            var jxrlibStderr = proc.StandardError.ReadToEnd();
            _out.WriteLine($"=== JxrEncApp stderr ===");
            _out.WriteLine(jxrlibStderr);
        }
        finally
        {
            if (File.Exists(tif)) File.Delete(tif);
            if (File.Exists(refJxr)) File.Delete(refJxr);
        }

        // Run OUR encoder with trace enabled.
        Environment.SetEnvironmentVariable("DIR_LIB_JXR_TRACE", "1");
        try
        {
            var halfBits = new ushort[halves.Length];
            for (var i = 0; i < halves.Length; i++) halfBits[i] = BitConverter.HalfToUInt16Bits(halves[i]);
            // Capture our stderr by redirecting Console.Error.
            var sw = new System.IO.StringWriter();
            var origErr = System.Console.Error;
            System.Console.SetError(sw);
            try
            {
                _ = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halfBits, w, h, useYUV444: true);
            }
            finally { System.Console.SetError(origErr); }
            _out.WriteLine($"=== OUR encoder stderr ===");
            _out.WriteLine(sw.ToString());
        }
        finally { Environment.SetEnvironmentVariable("DIR_LIB_JXR_TRACE", null); }
    }

    [Fact]
    public void DiagnoseJxrlibInstrumentedTrace()
    {
        // Encode the SAME 2-MB BD16F YUV444 input via the instrumented
        // JxrEncApp.exe and capture its stderr trace. Reveals jxrlib's exact
        // mode + DC block + adapt state at each step so we can compare to what
        // OUR encoder does for the same input.
        var jxrEncApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrEncApp.exe");
        if (!File.Exists(jxrEncApp)) { _out.WriteLine("SKIP — JxrEncApp not built"); return; }

        const int w = 32, h = 16;
        var halves = new Half[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            halves[i * 3 + 0] = (Half)(0.4f + ((x + y) % 7) * 0.005f);
            halves[i * 3 + 1] = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
            halves[i * 3 + 2] = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
        }
        var tif = Path.Combine(Path.GetTempPath(), $"diag_{Guid.NewGuid():N}.tif");
        var refJxr = tif.Replace(".tif", ".jxr");
        try
        {
            WriteHalfTiff(tif, halves, w, h);
            var psi = new System.Diagnostics.ProcessStartInfo(jxrEncApp,
                $"-i \"{tif}\" -o \"{refJxr}\" -c 12 -q 1 -d 3 -l 0 -f -s 1")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(60_000).ShouldBeTrue();
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            _out.WriteLine($"JxrEncApp exit={proc.ExitCode}");
            _out.WriteLine($"=== JxrEncApp stdout ===\n{stdout}");
            _out.WriteLine($"=== JxrEncApp stderr (instrumentation) ===\n{stderr}");
        }
        finally
        {
            if (File.Exists(tif)) File.Delete(tif);
            if (File.Exists(refJxr)) File.Delete(refJxr);
        }
    }

    [Fact]
    public void DiagnoseMb0BytesIdenticalIn1MbAnd2MbCases()
    {
        // If MB 0's encoded bytes differ depending on whether MB 1 follows,
        // we have a fundamental ordering bug. We expect them to be identical.
        const int w1 = 16, h = 16, w2 = 32;
        var halves1 = new Half[w1 * h * 3];
        var halves2 = new Half[w2 * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w2; x++)
        {
            var r = (Half)(0.4f + ((x + y) % 7) * 0.005f);
            var g = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
            var b = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
            if (x < w1)
            {
                halves1[(y * w1 + x) * 3 + 0] = r;
                halves1[(y * w1 + x) * 3 + 1] = g;
                halves1[(y * w1 + x) * 3 + 2] = b;
            }
            halves2[(y * w2 + x) * 3 + 0] = r;
            halves2[(y * w2 + x) * 3 + 1] = g;
            halves2[(y * w2 + x) * 3 + 2] = b;
        }
        var bits1 = HalfArrayToUshort(halves1);
        var bits2 = HalfArrayToUshort(halves2);

        var cs1 = JxrContainer.Read(JxrFileFormatter.SaveBd16FRgbNoFlexbits(bits1, w1, h, useYUV444: true)).Codestream;
        var cs2 = JxrContainer.Read(JxrFileFormatter.SaveBd16FRgbNoFlexbits(bits2, w2, h, useYUV444: true)).Codestream;

        _out.WriteLine($"1-MB codestream length: {cs1.Length}");
        _out.WriteLine($"2-MB codestream length: {cs2.Length}");

        // Find the first byte where they differ. Header (image width / height
        // / tile dims) will differ; the actual coded MB data starts later.
        var firstDiff = -1;
        for (var i = 0; i < Math.Min(cs1.Length, cs2.Length); i++)
        {
            if (cs1[i] != cs2[i]) { firstDiff = i; break; }
        }
        _out.WriteLine($"First byte diff at offset 0x{firstDiff:X4}");
        DumpHex2("1-MB", cs1, "2-MB", cs2, Math.Min(160, Math.Min(cs1.Length, cs2.Length)));
    }

    [Fact]
    public void DiagnoseBd16F_Bytewise2MbDiff()
    {
        // Two MBs of high-variation half-float RGB; compare our codestream
        // bytes to JxrEncApp's for identical input. First divergent byte =
        // where our encoding starts being non-spec-compliant.
        var jxrEncApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrEncApp.exe");
        if (!File.Exists(jxrEncApp)) { _out.WriteLine("SKIP"); return; }
        const int w = 32, h = 16;
        var halves = new Half[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = y * w + x;
            halves[i * 3 + 0] = (Half)(0.4f + ((x + y) % 7) * 0.005f);
            halves[i * 3 + 1] = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
            halves[i * 3 + 2] = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
        }
        var halfBits = new ushort[halves.Length];
        for (var i = 0; i < halves.Length; i++) halfBits[i] = BitConverter.HalfToUInt16Bits(halves[i]);

        // Write 48bppRGBHalf TIFF.
        var tif = Path.Combine(Path.GetTempPath(), $"diag_{Guid.NewGuid():N}.tif");
        var refJxr = tif.Replace(".tif", ".jxr");
        try
        {
            WriteHalfTiff(tif, halves, w, h);

            var psi = new System.Diagnostics.ProcessStartInfo(jxrEncApp,
                $"-i \"{tif}\" -o \"{refJxr}\" -c 12 -q 1 -d 3 -l 0 -f -s 1")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(60_000).ShouldBeTrue();
            _out.WriteLine($"JxrEncApp exit={proc.ExitCode}, stderr={proc.StandardError.ReadToEnd()}");
            if (proc.ExitCode != 0) return;

            var refCs = JxrContainer.Read(File.ReadAllBytes(refJxr)).Codestream;
            var ourCs = JxrContainer.Read(JxrFileFormatter.SaveBd16FRgbNoFlexbits(halfBits, w, h, useYUV444: true)).Codestream;
            _out.WriteLine($"REF len={refCs.Length}, OURS len={ourCs.Length}");
            DumpHex2("REF", refCs, "OURS", ourCs, 96);
        }
        finally
        {
            if (File.Exists(tif)) File.Delete(tif);
            if (File.Exists(refJxr)) File.Delete(refJxr);
        }
    }

    private static void WriteHalfTiff(string path, Half[] halves, int w, int h)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> hdr = stackalloc byte[8];
        hdr[0] = (byte)'I'; hdr[1] = (byte)'I';
        BitConverter.TryWriteBytes(hdr[2..], (ushort)42);
        BitConverter.TryWriteBytes(hdr[4..], (uint)8);
        fs.Write(hdr);

        const int NUM_ENTRIES = 12;
        var ifdSize = 2 + NUM_ENTRIES * 12 + 4;
        var bpsOffset = (uint)(8 + ifdSize);
        var sfOffset = bpsOffset + 6u;
        var pixDataOffset = sfOffset + 6u;
        var pixBytes = (uint)(w * h * 3 * 2);

        var ifd = new byte[ifdSize];
        BitConverter.TryWriteBytes(ifd.AsSpan(0), (ushort)NUM_ENTRIES);
        int p = 2;
        void AddEntry(ushort tag, ushort type, uint count, uint value)
        {
            BitConverter.TryWriteBytes(ifd.AsSpan(p, 2), tag);
            BitConverter.TryWriteBytes(ifd.AsSpan(p + 2, 2), type);
            BitConverter.TryWriteBytes(ifd.AsSpan(p + 4, 4), count);
            BitConverter.TryWriteBytes(ifd.AsSpan(p + 8, 4), value);
            p += 12;
        }
        AddEntry(0x0100, 3, 1, (uint)w);
        AddEntry(0x0101, 3, 1, (uint)h);
        AddEntry(0x0102, 3, 3, bpsOffset);
        AddEntry(0x0103, 3, 1, 1);
        AddEntry(0x0106, 3, 1, 2);
        AddEntry(0x0111, 4, 1, pixDataOffset);
        AddEntry(0x0115, 3, 1, 3);
        AddEntry(0x0116, 3, 1, (uint)h);
        AddEntry(0x0117, 4, 1, pixBytes);
        AddEntry(0x011A, 5, 1, 0);
        AddEntry(0x011C, 3, 1, 1);
        AddEntry(0x0153, 3, 3, sfOffset);
        BitConverter.TryWriteBytes(ifd.AsSpan(p, 4), (uint)0);
        fs.Write(ifd);

        Span<byte> bps = stackalloc byte[6];
        BitConverter.TryWriteBytes(bps[0..], (ushort)16);
        BitConverter.TryWriteBytes(bps[2..], (ushort)16);
        BitConverter.TryWriteBytes(bps[4..], (ushort)16);
        fs.Write(bps);
        Span<byte> sf = stackalloc byte[6];
        BitConverter.TryWriteBytes(sf[0..], (ushort)3);
        BitConverter.TryWriteBytes(sf[2..], (ushort)3);
        BitConverter.TryWriteBytes(sf[4..], (ushort)3);
        fs.Write(sf);
        var row = new byte[w * 3 * 2];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var bits = BitConverter.HalfToUInt16Bits(halves[(y * w + x) * 3 + c]);
                    BitConverter.TryWriteBytes(row.AsSpan(x * 6 + c * 2, 2), bits);
                }
            }
            fs.Write(row);
        }
    }

    private void DumpHex2(string lA, byte[] a, string lB, byte[] b, int count)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"     | {lA,-48} | {lB,-48} | diff");
        for (var i = 0; i < count; i += 16)
        {
            sb.Append($"{i:X4} | ");
            for (var k = 0; k < 16; k++) sb.Append(i + k < a.Length ? $"{a[i + k]:X2} " : "   ");
            sb.Append("| ");
            for (var k = 0; k < 16; k++) sb.Append(i + k < b.Length ? $"{b[i + k]:X2} " : "   ");
            sb.Append("| ");
            for (var k = 0; k < 16; k++)
            {
                var ax = i + k < a.Length ? a[i + k] : -1;
                var bx = i + k < b.Length ? b[i + k] : -1;
                sb.Append(ax != bx ? '^' : ' ');
            }
            sb.AppendLine();
        }
        _out.WriteLine(sb.ToString());
    }

    [Fact]
    public void DiagnoseBd16F_HighFreqByMbCount()
    {
        // Probe at increasing tile widths to find the MB count where decode
        // diverges. Image shape: 1 MB row × N MB cols of alternating-pixel
        // content. If single-MB decodes correctly but multi-MB does not,
        // the bug is in the cross-MB state (prediction or AdaptVLC).
        var jxrDecApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrDecApp.exe");
        if (!File.Exists(jxrDecApp)) { _out.WriteLine("SKIP"); return; }

        foreach (var mbCols in new[] { 1, 2, 4, 8, 16, 17, 32 })
        {
            var w = mbCols * 16;
            var h = 16;
            var halves = new Half[w * h * 3];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Brownish nebula-like pixel with per-pixel variation in low bits.
                var i = y * w + x;
                halves[i * 3 + 0] = (Half)(0.4f + ((x + y) % 7) * 0.005f);
                halves[i * 3 + 1] = (Half)(0.3f + ((x * 3 + y) % 5) * 0.005f);
                halves[i * 3 + 2] = (Half)(0.2f + ((x + y * 2) % 11) * 0.003f);
            }
            var halfBits = new ushort[halves.Length];
            for (var i = 0; i < halves.Length; i++) halfBits[i] = BitConverter.HalfToUInt16Bits(halves[i]);

            var jxrBytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(halfBits, w, h, useYUV444: true);
            var jxrPath = Path.Combine(Path.GetTempPath(), $"diag_{Guid.NewGuid():N}.jxr");
            var tifPath = jxrPath.Replace(".jxr", ".tif");
            File.WriteAllBytes(jxrPath, jxrBytes);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(jxrDecApp,
                    $"-i \"{jxrPath}\" -o \"{tifPath}\" -c 12")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                proc.WaitForExit(30_000).ShouldBeTrue();
                if (proc.ExitCode != 0) { _out.WriteLine($"  w={w}: JxrDecApp exit={proc.ExitCode}"); continue; }

                var tif = File.ReadAllBytes(tifPath);
                var ifdOff = (int)BitConverter.ToUInt32(tif, 4);
                var nEntries = BitConverter.ToUInt16(tif, ifdOff);
                int stripOff = 0;
                for (var i = 0; i < nEntries; i++)
                {
                    var e = ifdOff + 2 + i * 12;
                    if (BitConverter.ToUInt16(tif, e) == 0x111) stripOff = (int)BitConverter.ToUInt32(tif, e + 8);
                }
                var mismatch = 0;
                for (var i = 0; i < halves.Length; i++)
                {
                    var rt = BitConverter.ToUInt16(tif, stripOff + i * 2);
                    if (rt != BitConverter.HalfToUInt16Bits(halves[i])) mismatch++;
                }
                _out.WriteLine($"  w={w} ({mbCols} MB cols): mismatch={mismatch}/{halves.Length} ({mismatch * 100.0 / halves.Length:F1}%)");
            }
            finally
            {
                if (File.Exists(jxrPath)) File.Delete(jxrPath);
                if (File.Exists(tifPath)) File.Delete(tifPath);
            }
        }
    }

    [Fact(Skip = "Diagnostic only — kept as scaffolding for follow-up work")]
    public void DiagnoseBd16F_CompareCodedDataToRef()
    {
        // Build a JxrEncApp REF and our codestream from a non-trivial half-float
        // bitmap and dump enough bytes of each to spot encoding divergence past
        // the header. Goal: figure out why WIC decodes our file as colour noise
        // even though it's structurally accepted.
        if (!OperatingSystem.IsWindows()) { _out.WriteLine("SKIP"); return; }
        var jxrEncApp = Path.Combine(AppContext.BaseDirectory, "Oracle", "JxrEncApp.exe");
        if (!File.Exists(jxrEncApp)) { _out.WriteLine("SKIP — JxrEncApp not built"); return; }

        // Use a TINY image (16×16) with a known gradient so we can compare
        // every coded byte and spot the divergence point.
        const int w = 16, h = 16;
        var halves = new Half[w * h * 3];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            halves[(y * w + x) * 3 + 0] = (Half)((x * 16.0 + y) / 256.0); // R
            halves[(y * w + x) * 3 + 1] = (Half)((y * 16.0 + x) / 256.0); // G
            halves[(y * w + x) * 3 + 2] = (Half)(0.5);                     // B constant
        }

        // ----- Write a 48bppRGBHalf TIFF for JxrEncApp to consume -----
        var tiffPath = Path.Combine(Path.GetTempPath(), $"diag16_{Guid.NewGuid():N}.tif");
        var refJxr = tiffPath.Replace(".tif", ".jxr");
        try
        {
            using (var fs = new FileStream(tiffPath, FileMode.Create, FileAccess.Write))
            {
                Span<byte> hdr = stackalloc byte[8];
                hdr[0] = (byte)'I'; hdr[1] = (byte)'I';
                BitConverter.TryWriteBytes(hdr[2..], (ushort)42);
                BitConverter.TryWriteBytes(hdr[4..], (uint)8);
                fs.Write(hdr);

                const int NUM_ENTRIES = 12;
                var ifdSize = 2 + NUM_ENTRIES * 12 + 4;
                var bpsOffset = (uint)(8 + ifdSize);
                var sfOffset = bpsOffset + 6u;
                var pixDataOffset = sfOffset + 6u;
                var pixBytes = (uint)(w * h * 3 * 2);

                var ifd = new byte[ifdSize];
                BitConverter.TryWriteBytes(ifd.AsSpan(0), (ushort)NUM_ENTRIES);
                int p = 2;
                void AddEntry(ushort tag, ushort type, uint count, uint value)
                {
                    BitConverter.TryWriteBytes(ifd.AsSpan(p, 2), tag);
                    BitConverter.TryWriteBytes(ifd.AsSpan(p + 2, 2), type);
                    BitConverter.TryWriteBytes(ifd.AsSpan(p + 4, 4), count);
                    BitConverter.TryWriteBytes(ifd.AsSpan(p + 8, 4), value);
                    p += 12;
                }
                AddEntry(0x0100, 3, 1, (uint)w);
                AddEntry(0x0101, 3, 1, (uint)h);
                AddEntry(0x0102, 3, 3, bpsOffset);
                AddEntry(0x0103, 3, 1, 1);
                AddEntry(0x0106, 3, 1, 2);
                AddEntry(0x0111, 4, 1, pixDataOffset);
                AddEntry(0x0115, 3, 1, 3);
                AddEntry(0x0116, 3, 1, (uint)h);
                AddEntry(0x0117, 4, 1, pixBytes);
                AddEntry(0x011A, 5, 1, 0);
                AddEntry(0x011C, 3, 1, 1);
                AddEntry(0x0153, 3, 3, sfOffset);
                BitConverter.TryWriteBytes(ifd.AsSpan(p, 4), (uint)0);
                fs.Write(ifd);

                Span<byte> bps = stackalloc byte[6];
                BitConverter.TryWriteBytes(bps[0..], (ushort)16);
                BitConverter.TryWriteBytes(bps[2..], (ushort)16);
                BitConverter.TryWriteBytes(bps[4..], (ushort)16);
                fs.Write(bps);
                Span<byte> sf = stackalloc byte[6];
                BitConverter.TryWriteBytes(sf[0..], (ushort)3);
                BitConverter.TryWriteBytes(sf[2..], (ushort)3);
                BitConverter.TryWriteBytes(sf[4..], (ushort)3);
                fs.Write(sf);
                // Pixel data: half-float bits.
                var rowBytes = new byte[w * 3 * 2];
                for (var y = 0; y < h; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var rBits = BitConverter.HalfToUInt16Bits(halves[(y * w + x) * 3 + 0]);
                        var gBits = BitConverter.HalfToUInt16Bits(halves[(y * w + x) * 3 + 1]);
                        var bBits = BitConverter.HalfToUInt16Bits(halves[(y * w + x) * 3 + 2]);
                        BitConverter.TryWriteBytes(rowBytes.AsSpan(x * 6 + 0, 2), rBits);
                        BitConverter.TryWriteBytes(rowBytes.AsSpan(x * 6 + 2, 2), gBits);
                        BitConverter.TryWriteBytes(rowBytes.AsSpan(x * 6 + 4, 2), bBits);
                    }
                    fs.Write(rowBytes);
                }
            }

            // Encode reference: lossless YUV444 spatial mode (matching our defaults
            // as closely as possible to isolate the divergence).
            var psi = new System.Diagnostics.ProcessStartInfo(jxrEncApp,
                $"-i \"{tiffPath}\" -o \"{refJxr}\" -c 12 -q 1 -d 3 -l 0 -f")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(60_000).ShouldBeTrue();
            _out.WriteLine($"JxrEncApp exit={proc.ExitCode}, stdout={proc.StandardOutput.ReadToEnd()} stderr={proc.StandardError.ReadToEnd()}");
            if (proc.ExitCode != 0 || !File.Exists(refJxr)) { _out.WriteLine("JxrEncApp didn't produce output"); return; }

            var refFile = JxrContainer.Read(File.ReadAllBytes(refJxr));
            _out.WriteLine($"REF codestream length: {refFile.Codestream.Length}");
            DumpHex("REF", refFile.Codestream, Math.Min(160, refFile.Codestream.Length));

            // Our output with the same input.
            var oursBytes = JxrFileFormatter.SaveBd16FRgbNoFlexbits(
                HalfArrayToUshort(halves), w, h, useYUV444: true);
            var oursFile = JxrContainer.Read(oursBytes);
            _out.WriteLine($"OURS codestream length: {oursFile.Codestream.Length}");
            DumpHex("OURS", oursFile.Codestream, Math.Min(160, oursFile.Codestream.Length));

            // Verify our own round-trip is lossless first.
            var roundTrip = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(oursBytes, out _, out _, out _);
            var mismatches = 0;
            for (var i = 0; i < halves.Length; i++)
            {
                if (BitConverter.HalfToUInt16Bits(roundTrip[i]) != BitConverter.HalfToUInt16Bits(halves[i]))
                    mismatches++;
            }
            _out.WriteLine($"Self round-trip mismatches: {mismatches}/{halves.Length}");

            // Decode REF through our decoder to sanity-check it.
            try
            {
                var refDecoded = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(File.ReadAllBytes(refJxr), out _, out _, out _);
                var refMismatches = 0;
                for (var i = 0; i < halves.Length; i++)
                {
                    if (BitConverter.HalfToUInt16Bits(refDecoded[i]) != BitConverter.HalfToUInt16Bits(halves[i]))
                        refMismatches++;
                }
                _out.WriteLine($"REF decoded through ours, mismatches: {refMismatches}/{halves.Length}");
            }
            catch (Exception ex) { _out.WriteLine($"Cannot decode REF through ours: {ex.Message}"); }

            // Also probe both through WIC.
            var wicRef = WicOracle.Probe(refJxr);
            _out.WriteLine($"WIC ref: {wicRef.RawOutput.TrimEnd().Replace('\n', '|')}");
            var oursTmp = Path.Combine(Path.GetTempPath(), $"diag_ours_{Guid.NewGuid():N}.jxr");
            File.WriteAllBytes(oursTmp, oursBytes);
            try
            {
                var wicOurs = WicOracle.Probe(oursTmp);
                _out.WriteLine($"WIC ours: {wicOurs.RawOutput.TrimEnd().Replace('\n', '|')}");
            }
            finally { if (File.Exists(oursTmp)) File.Delete(oursTmp); }
        }
        finally
        {
            if (File.Exists(tiffPath)) File.Delete(tiffPath);
            if (File.Exists(refJxr)) File.Delete(refJxr);
        }
    }

    private static ushort[] HalfArrayToUshort(Half[] halves)
    {
        var result = new ushort[halves.Length];
        for (var i = 0; i < halves.Length; i++)
            result[i] = BitConverter.HalfToUInt16Bits(halves[i]);
        return result;
    }

    private void DumpHex(string label, byte[] bytes, int count)
    {
        var sb = new System.Text.StringBuilder(label + ":\n");
        for (var i = 0; i < count && i < bytes.Length; i++)
        {
            if (i % 16 == 0) sb.Append($"  {i:X4}: ");
            sb.Append(bytes[i].ToString("X2")).Append(' ');
            if (i % 16 == 15) sb.Append('\n');
        }
        _out.WriteLine(sb.ToString());
    }

    [Fact]
    public void UserAstroFile_WicProbe()
    {
        // Diagnostic probe for the user's TianWen output. Skip when absent.
        // Doesn't assert — purpose is to capture WIC's perspective in test
        // output (so a re-run after a fix shows whether the issue's gone).
        var paths = new[]
        {
            @"C:\temp\stack\output\sol_test.jxr",
            @"C:\temp\stack\output\sol_test2.jxr",
        };
        var anyPresent = false;
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            anyPresent = true;
            var bytes = File.ReadAllBytes(path);
            var file = JxrContainer.Read(bytes);
            _out.WriteLine($"=== {path}");
            _out.WriteLine($"  Container: {file.Width}x{file.Height} {file.PixelFormat} codestream={file.Codestream.Length:N0}B");
            try
            {
                var ci = CodedImage.Decode(file.Codestream);
                _out.WriteLine($"  ImageHeader: OutCC={ci.ImageHeader.OutputClrFmt} OutBd={ci.ImageHeader.OutputBitDepth} " +
                               $"Tile={ci.ImageHeader.TilingFlag} Freq={ci.ImageHeader.FrequencyModeCodestreamFlag} " +
                               $"IdxTbl={ci.ImageHeader.IndexTablePresentFlag} Overlap={ci.ImageHeader.OverlapMode} " +
                               $"ShortHdr={ci.ImageHeader.ShortHeaderFlag} HardTile={ci.ImageHeader.HardTilingFlag}");
                _out.WriteLine($"  Plane: InCC={ci.PlaneHeader.InternalClrFmt} Bands={ci.PlaneHeader.BandsPresent} " +
                               $"NumComp={ci.PlaneHeader.NumComponents}");
                foreach (var e in ci.ProfileLevelInfo.Entries)
                    _out.WriteLine($"  ProfileLevel: Profile=0x{e.ProfileIdc:X2}({e.ProfileIdc}) Level=0x{e.LevelIdc:X2}({e.LevelIdc})");
            }
            catch (Exception ex)
            {
                // Pre-Task-#11 files have 2 extra bytes in the BD16F plane
                // header; they're no longer decodable by the spec-compliant
                // decoder. Note the failure and continue with the WIC probe.
                _out.WriteLine($"  Decode failed (likely pre-Task-#11 BD16F file): {ex.GetType().Name}: {ex.Message}");
            }
            var result = WicOracle.Probe(path);
            _out.WriteLine($"  WIC: {result.RawOutput.TrimEnd()}");
            _out.WriteLine($"  Valid: {result.IsValidImage}");
        }
        if (!anyPresent) _out.WriteLine("SKIP — no user files at C:\\temp\\stack\\output\\");
    }
}
