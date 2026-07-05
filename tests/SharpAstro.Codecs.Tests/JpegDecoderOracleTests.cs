using System.Security.Cryptography;
using ImageMagick;
using SharpAstro.Jpeg;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Byte-exact regression tests for <see cref="JpegDecoder"/>: full-scale output
/// must match a committed golden baseline on a matrix of inputs (subsampling ×
/// quality × progressive × colour model, edge dimensions, grayscale, Adobe CMYK,
/// restart intervals, and one real-world camera file). Inputs are encoded in-test
/// with Magick.NET (libjpeg) plus a hand-built restart bitstream, so the matrix
/// costs no committed image fixtures; only a tiny <c>jpeg-oracle-golden.tsv</c>
/// digest manifest is stored.
///
/// <para><b>Provenance of the golden.</b> The baseline was frozen from
/// <see cref="JpegDecoder"/>'s own output at the moment the in-repo StbImageSharp
/// (stb_image) reference decoder confirmed it byte-for-byte — i.e. the golden IS
/// the stb-validated decode. The stb port has since been removed from the repo;
/// this manifest is what preserves that byte-exact guarantee. To refresh it after
/// an intentional decoder change, run this project with
/// <c>REGEN_JPEG_ORACLE=1</c> (see <see cref="RegenerateGolden"/>) and review the
/// resulting diff.</para>
///
/// <para>A separate tolerance check against Magick's own (libjpeg-turbo) decode
/// (<see cref="FullScale_WithinToleranceOfLibjpeg"/>) guards against faithfully
/// replicating an stb bug: stb and libjpeg use the same accurate integer IDCT and
/// triangular chroma upsampling, so they agree within a couple of code values.</para>
///
/// <para>The inputs are reproducible only against the pinned Magick.NET version
/// (see <c>Directory.Packages.props</c>); a Magick bump can change the encoded
/// bytes and require a golden regen.</para>
/// </summary>
public sealed class JpegDecoderOracleTests
{
    // ------------------------------------------------------------ helpers

    private static byte[] MakeRgbPattern(int w, int h)
    {
        // Gradients + checker + sine + LCG noise: exercises DC ramps, sharp
        // edges (long AC runs), and dense AC coefficients deterministically.
        var rgb = new byte[w * h * 3];
        var seed = 0x12345678u;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                seed = seed * 1664525u + 1013904223u;
                var noise = (int)(seed >> 28);
                rgb[i + 0] = (byte)Math.Clamp(x * 255 / Math.Max(1, w - 1) + noise - 8, 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, h - 1) + (((x >> 3) ^ (y >> 3)) & 1) * 60, 0, 255);
                rgb[i + 2] = (byte)(128 + 100 * Math.Sin(x * 0.35));
            }
        }

        return rgb;
    }

    internal static byte[] EncodeJpeg(
        byte[] rgb, int w, int h, int quality = 90, string? sampling = null,
        bool progressive = false, bool gray = false, bool cmyk = false)
    {
        var settings = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "RGB");
        using var img = new MagickImage();
        img.ReadPixels(rgb, settings);
        img.Quality = (uint)quality;
        if (sampling != null)
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", sampling);
        if (progressive)
            img.Settings.Interlace = Interlace.Jpeg;
        if (gray)
            img.ColorSpace = ColorSpace.Gray;
        if (cmyk)
            img.ColorSpace = ColorSpace.CMYK;
        return img.ToByteArray(MagickFormat.Jpeg);
    }

    private static string DockPanesPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "DockPanes.jpg");

    // ------------------------------------------------------------ the case matrix
    //
    // One source of truth for every byte-exact case: (label, lazy builder). The
    // [Theory] below and the golden regenerator both iterate it, so the input
    // construction never drifts between "what we assert" and "what we froze".

    private static readonly (string Label, Func<byte[]> Build)[] OracleCases = BuildOracleCases();

    private static (string, Func<byte[]>)[] BuildOracleCases()
    {
        var cases = new List<(string, Func<byte[]>)>();

        // Sampling × quality × progressive, at odd dimensions (partial edge MCUs
        // in both axes).
        foreach (var sampling in new[] { "4:4:4", "4:2:2", "4:2:0" })
            foreach (var quality in new[] { 95, 75, 25 })
                foreach (var progressive in new[] { false, true })
                {
                    var s = sampling;
                    var q = quality;
                    var p = progressive;
                    const int w = 67, h = 45;
                    cases.Add((
                        $"{s} q{q}{(p ? " progressive" : "")}",
                        () => EncodeJpeg(MakeRgbPattern(w, h), w, h, q, s, p)));
                }

        // Edge dimensions.
        foreach (var (w, h) in new[] { (1, 1), (8, 8), (16, 16), (17, 13), (130, 7), (7, 130) })
        {
            var ww = w;
            var hh = h;
            cases.Add((
                $"{ww}x{hh} 4:2:0",
                () => EncodeJpeg(MakeRgbPattern(ww, hh), ww, hh, 85, "4:2:0")));
        }

        // Grayscale, baseline + progressive.
        foreach (var progressive in new[] { false, true })
        {
            var p = progressive;
            const int w = 50, h = 38;
            cases.Add((
                $"grayscale{(p ? " progressive" : "")}",
                () => EncodeJpeg(MakeRgbPattern(w, h), w, h, 80, gray: true, progressive: p)));
        }

        // Adobe CMYK.
        cases.Add(("Adobe CMYK", () => EncodeJpeg(MakeRgbPattern(40, 30), 40, 30, 90, cmyk: true)));

        // Restart intervals (hand-built, deterministic, no encoder).
        cases.Add(("restart intervals", () => BuildRestartMarkerJpeg(blocksW: 10, blocksH: 7, restartInterval: 7)));

        // One real-world camera-style file.
        cases.Add(("DockPanes.jpg", () => File.ReadAllBytes(DockPanesPath)));

        return cases.ToArray();
    }

    public static IEnumerable<object[]> CaseLabels() => OracleCases.Select(c => new object[] { c.Label });

    private static byte[] BuildCase(string label) => OracleCases.First(c => c.Label == label).Build();

    // ------------------------------------------------------------ the byte-exact assertion

    [Theory]
    [MemberData(nameof(CaseLabels))]
    public void ByteExact(string label)
    {
        var jpeg = BuildCase(label);
        var ours = JpegDecoder.Decode(jpeg);

        var golden = Golden.Value;
        golden.ShouldContainKey(label);
        var (w, h, sha) = golden[label];

        ours.Width.ShouldBe(w, label);
        ours.Height.ShouldBe(h, label);
        Sha256(ours.Pixels).ShouldBe(sha, $"{label}: decoded pixels differ from the frozen golden baseline");
    }

    private static string Sha256(byte[] pixels) => Convert.ToHexString(SHA256.HashData(pixels));

    // ------------------------------------------------------------ golden manifest

    private static readonly Lazy<Dictionary<string, (int W, int H, string Sha)>> Golden = new(LoadGolden);

    private static string GoldenFileName => "jpeg-oracle-golden.tsv";

    private static Dictionary<string, (int, int, string)> LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", GoldenFileName);
        var map = new Dictionary<string, (int, int, string)>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split('\t');
            map[parts[0]] = (int.Parse(parts[1]), int.Parse(parts[2]), parts[3]);
        }

        return map;
    }

    /// <summary>
    /// Regenerates <c>jpeg-oracle-golden.tsv</c> from the current decoder output.
    /// Skipped unless <c>REGEN_JPEG_ORACLE=1</c>; run it after an intentional
    /// decoder change and review the diff (the baseline was originally frozen
    /// while the stb reference decoder validated the output byte-for-byte).
    /// </summary>
    [Fact]
    public void RegenerateGolden()
    {
        if (Environment.GetEnvironmentVariable("REGEN_JPEG_ORACLE") != "1")
        {
            Assert.Skip("set REGEN_JPEG_ORACLE=1 to rewrite the golden baseline");
            return;
        }

        var lines = new List<string>
        {
            "# jpeg-oracle-golden.tsv - byte-exact baseline for JpegDecoderOracleTests.",
            "# label<TAB>width<TAB>height<TAB>sha256(RGBA pixels, uppercase hex).",
            "# Regenerate with REGEN_JPEG_ORACLE=1; see JpegDecoderOracleTests for provenance.",
        };
        foreach (var (label, build) in OracleCases)
        {
            var ours = JpegDecoder.Decode(build());
            lines.Add($"{label}\t{ours.Width}\t{ours.Height}\t{Sha256(ours.Pixels)}");
        }

        var outPath = Path.Combine(RepoFixturesDir(), GoldenFileName);
        File.WriteAllLines(outPath, lines);
    }

    private static string RepoFixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "SharpAstro.Codecs.Tests", "Fixtures");
            if (Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("could not locate the SharpAstro.Codecs.Tests/Fixtures source directory");
    }

    // ------------------------------------------------------------ restart markers

    [Fact]
    public void RestartIntervals_StreamContainsMarkers()
    {
        // Byte-exact decode is covered by the ByteExact theory ("restart intervals");
        // this proves the hand-built stream actually contains what we claim to test.
        var jpeg = BuildRestartMarkerJpeg(blocksW: 10, blocksH: 7, restartInterval: 7);
        ContainsMarker(jpeg, 0xDD).ShouldBeTrue("stream should declare a DRI interval");
        ContainsMarker(jpeg, 0xD0).ShouldBeTrue("stream should contain RST0");
    }

    private static bool ContainsMarker(byte[] jpeg, byte marker)
    {
        for (var i = 0; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Minimal valid baseline JPEG: one grayscale component, custom Huffman
    /// tables (12 four-bit DC categories; a single one-bit AC EOB code), flat
    /// quantization, DC-only blocks with varying levels, and RSTn markers with
    /// DC-predictor resets every <paramref name="restartInterval"/> MCUs.
    /// </summary>
    private static byte[] BuildRestartMarkerJpeg(int blocksW, int blocksH, int restartInterval)
    {
        var output = new List<byte>();

        static void Marker(List<byte> o, byte m, params byte[] payload)
        {
            o.Add(0xFF);
            o.Add(m);
            if (m is 0xD8 or 0xD9)
                return;
            var len = payload.Length + 2;
            o.Add((byte)(len >> 8));
            o.Add((byte)len);
            o.AddRange(payload);
        }

        Marker(output, 0xD8); // SOI

        // DQT: table 0, 8-bit, all ones (identity quantization).
        var dqt = new byte[1 + 64];
        dqt[0] = 0x00;
        Array.Fill(dqt, (byte)1, 1, 64);
        Marker(output, 0xDB, dqt);

        // SOF0: 8-bit, 1 component, 1×1 sampling, quant table 0.
        var width = blocksW * 8;
        var height = blocksH * 8;
        Marker(output, 0xC0,
            8,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            1,          // components
            1, 0x11, 0  // id=1, h=v=1, tq=0
        );

        // DHT DC table 0: all 12 categories at code length 4 → canonical codes 0..11.
        var dhtDc = new byte[1 + 16 + 12];
        dhtDc[0] = 0x00;
        dhtDc[1 + 3] = 12; // twelve codes of length 4
        for (var i = 0; i < 12; i++)
            dhtDc[1 + 16 + i] = (byte)i;
        Marker(output, 0xC4, dhtDc);

        // DHT AC table 0: a single 1-bit code for EOB (run=0, size=0).
        var dhtAc = new byte[1 + 16 + 1];
        dhtAc[0] = 0x10;
        dhtAc[1 + 0] = 1;  // one code of length 1
        dhtAc[1 + 16] = 0x00;
        Marker(output, 0xC4, dhtAc);

        Marker(output, 0xDD, (byte)(restartInterval >> 8), (byte)restartInterval); // DRI

        Marker(output, 0xDA, 1, 1, 0x00, 0, 63, 0); // SOS: component 1, DC/AC table 0

        // Entropy data: per block emit DC category+magnitude then the 1-bit EOB.
        var acc = 0;
        var nbits = 0;

        void PutBits(int value, int count)
        {
            if (count == 0)
                return;
            acc = (acc << count) | (value & ((1 << count) - 1));
            nbits += count;
            while (nbits >= 8)
            {
                var b = (byte)(acc >> (nbits - 8));
                output.Add(b);
                if (b == 0xFF)
                    output.Add(0x00); // byte stuffing
                nbits -= 8;
                acc &= (1 << nbits) - 1;
            }
        }

        void AlignWithOnes()
        {
            if (nbits > 0)
                PutBits((1 << (8 - nbits)) - 1, 8 - nbits);
        }

        var pred = 0;
        var rst = 0;
        var sinceRestart = 0;
        var totalBlocks = blocksW * blocksH;
        for (var b = 0; b < totalBlocks; b++)
        {
            if (sinceRestart == restartInterval)
            {
                AlignWithOnes();
                output.Add(0xFF);
                output.Add((byte)(0xD0 + (rst & 7)));
                rst++;
                pred = 0;
                sinceRestart = 0;
            }

            var dc = (b * 37) % 401 - 200; // deterministic level sweep, both signs
            var diff = dc - pred;
            pred = dc;

            var cat = 0;
            for (var a = Math.Abs(diff); a > 0; a >>= 1)
                cat++;
            PutBits(cat, 4);                                       // DC category (4-bit code = category)
            PutBits(diff >= 0 ? diff : diff + (1 << cat) - 1, cat); // magnitude bits
            PutBits(0, 1);                                          // AC EOB

            sinceRestart++;
        }

        AlignWithOnes();
        Marker(output, 0xD9); // EOI
        return output.ToArray();
    }

    // ------------------------------------------------------------ independent oracle

    [Fact]
    public void FullScale_WithinToleranceOfLibjpeg()
    {
        const int w = 64, h = 48;
        var jpeg = EncodeJpeg(MakeRgbPattern(w, h), w, h, 90, "4:2:0");
        var ours = JpegDecoder.Decode(jpeg);

        using var m = new MagickImage(jpeg);
        using var px = m.GetPixels();
        var vals = px.GetValues()!;
        var mc = (int)px.Channels;

        var sum = 0.0;
        var max = 0;
        for (var i = 0; i < w * h; i++)
        {
            for (var ch = 0; ch < 3; ch++)
            {
                var libjpeg = (int)Math.Round(vals[i * mc + ch] / 257f);
                var diff = Math.Abs(libjpeg - ours.Pixels[i * 4 + ch]);
                sum += diff;
                max = Math.Max(max, diff);
            }
        }

        max.ShouldBeLessThanOrEqualTo(6, "stb-style and libjpeg decodes should agree closely");
        (sum / (w * h * 3)).ShouldBeLessThanOrEqualTo(1.5);
    }

    // ------------------------------------------------------------ info + errors

    [Fact]
    public void ReadInfo_ReportsDimensionsComponentsProgressive()
    {
        var baseline = EncodeJpeg(MakeRgbPattern(67, 45), 67, 45, 90, "4:2:0");
        JpegDecoder.ReadInfo(baseline).ShouldBe(
            new JpegInfo(67, 45, 3, Progressive: false) { ChromaSubsampled = true });

        var progressive = EncodeJpeg(MakeRgbPattern(20, 10), 20, 10, 90, progressive: true);
        var progInfo = JpegDecoder.ReadInfo(progressive);
        (progInfo.Width, progInfo.Height, progInfo.Components, progInfo.Progressive)
            .ShouldBe((20, 10, 3, true));

        var fullChroma = EncodeJpeg(MakeRgbPattern(16, 16), 16, 16, 90, "4:4:4");
        JpegDecoder.ReadInfo(fullChroma).ChromaSubsampled.ShouldBeFalse();

        var subsampled422 = EncodeJpeg(MakeRgbPattern(16, 16), 16, 16, 90, "4:2:2");
        JpegDecoder.ReadInfo(subsampled422).ChromaSubsampled.ShouldBeTrue();

        var gray = EncodeJpeg(MakeRgbPattern(12, 9), 12, 9, 90, gray: true);
        var grayInfo = JpegDecoder.ReadInfo(gray);
        grayInfo.Components.ShouldBe(1);
        grayInfo.ChromaSubsampled.ShouldBeFalse();

        var cmyk = EncodeJpeg(MakeRgbPattern(12, 9), 12, 9, 90, cmyk: true);
        JpegDecoder.ReadInfo(cmyk).Components.ShouldBe(4);
    }

    [Fact]
    public void Rejects_NonJpegInput()
    {
        Should.Throw<InvalidDataException>(() => JpegDecoder.ReadInfo(Array.Empty<byte>()));
        Should.Throw<InvalidDataException>(() => JpegDecoder.ReadInfo("definitely not a JPEG"u8.ToArray()));
        Should.Throw<InvalidDataException>(() => JpegDecoder.Decode(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }));
    }
}
