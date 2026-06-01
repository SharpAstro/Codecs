using System.Diagnostics;
using SharpAstro.Jxr;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Rung 7e (part 2) — the bit-exact oracle. Encodes an image with our codec,
/// writes a real <c>.jxr</c> file, and decodes it with Microsoft jxrlib's
/// reference <c>JxrDecApp.exe</c>. A lossless round-trip that comes back
/// pixel-identical proves our entire encode pipeline (container + codestream
/// headers + per-MB band entropy coding) is conformant with the reference
/// decoder — the milestone the whole re-port has been building toward.
/// Tests no-op (pass) when the oracle binary isn't present.
/// </summary>
public sealed class JxrCodestreamOracleTests
{
    private readonly ITestOutputHelper _out;
    public JxrCodestreamOracleTests(ITestOutputHelper output) => _out = output;

    // Encodes with our codec and decodes with jxrlib's reference JxrDecApp; a
    // lossless round-trip that returns pixel-identical proves the whole encode
    // pipeline — container + codestream headers + per-MB band entropy coding +
    // cross-MB DC/AD/CBP prediction + the m_bResetContext / m_bResetRGITotals
    // adaptive-state timing + signal transform + colour — is bit-exact conformant.
    [Theory]
    [InlineData(16, 16, "flat")]
    [InlineData(16, 16, "gradient")]
    [InlineData(32, 16, "gradient")]
    [InlineData(48, 32, "gradient")]
    [InlineData(64, 48, "gradient")]
    [InlineData(64, 48, "random")]
    [InlineData(96, 64, "random")]
    [InlineData(80, 80, "gradient")]
    [InlineData(272, 16, "gradient")] // spans a 16-MB group boundary (mbX wraps 0..16,17)
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient")]
    [InlineData(33, 40, "random")]
    [InlineData(100, 60, "gradient")]
    public void OurEncode_DecodedByJxrDecApp_IsLossless(int w, int h, string kind)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "flat" => Flat(w, h, 100, 150, 200),
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_oracle_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our file");

            var (dw, dh, dr, dg, db) = ReadBmp24(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] ({kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] ({kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] ({kind} {w}x{h})");
            }
        }
        finally
        {
            File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // Rung 7f.2/7f.3 — Photo Overlap encoder conformance: our overlap-on encode (OL_ONE
    // = jxrlib's default, OL_TWO), decoded by the reference JxrDecApp, must come back lossless.
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(272, 16, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    [InlineData(272, 16, "gradient", 2)]
    public void OurEncode_Overlap_DecodedByJxrDecApp_IsLossless(int w, int h, string kind, int overlap)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_ol{overlap}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var bmpPath = tmp + ".bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (exit, stdout, stderr) = Run(decApp, $"-i \"{jxrPath}\" -o \"{bmpPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrDecApp must decode our OL_ONE file");

            var (dw, dh, dr, dg, db) = ReadBmp24(bmpPath);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] (OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] (OL{overlap} {kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] (OL{overlap} {kind} {w}x{h})");
            }
        }
        finally
        {
            File.Delete(jxrPath);
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
        }
    }

    // Rung 7f.2/7f.3 — Photo Overlap decoder conformance: a lossless spatial OL_ONE/OL_TWO
    // YUV444 file produced by the reference JxrEncApp must decode losslessly through our
    // container reader + codestream decoder (the inverse overlap _alternate operators).
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 1)]
    public void JxrlibEncode_Overlap_DecodedByUs_IsLossless(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_dec{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // -f spatial, -l N overlap level, -d 3 YUV444, -q 1 lossless, -c 0 24bppBGR.
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var jxr = File.ReadAllBytes(jxrPath);
            var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] (OL{overlap} decode {kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] (OL{overlap} decode {kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] (OL{overlap} decode {kind} {w}x{h})");
            }
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Chroma subsampling (YUV420 = -d 1, YUV422 = -d 2), OL_NONE. A subsampled file produced by
    // the reference JxrEncApp must decode through our codec to exactly what JxrDecApp reconstructs
    // from the same codestream (chroma is lossy vs the original, so the reference decode — not the
    // original — is the oracle). This is the first end-to-end chroma decode (C5).
    // Subsampled chroma always runs in jxrlib's scaled-arithmetic mode (even at QP 1): the transform
    // input is <<3-scaled on encode and the output is >>3 (SHIFTZERO+QPFRACBITS) on decode, and the
    // chroma 2x2 second stage uses the x2 strDCT2x2dnDec variant — both honoured by our decode path.
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 32, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 32, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    [InlineData(17, 13, "gradient", 1)] // non-16-aligned
    [InlineData(34, 30, "gradient", 2)]
    public void JxrlibChromaEncode_DecodedByUs_MatchesJxrDecApp(int w, int h, string kind, int sub)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        var decApp = FindOracle("JxrDecApp.exe");
        if (encApp is null || decApp is null) { _out.WriteLine("oracle binaries not found — skipping."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x5A + w * 31 + h + sub),
            _ => Gradient(w, h),
        };

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_chroma{sub}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        var refPath = tmp + "_ref.bmp";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // -d 1 = YUV420, -d 2 = YUV422; -l 0 = OL_NONE; -q 1 lossless coeffs; -f spatial; -c 0 24bppBGR.
            var (e1, so1, se1) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d {sub} -q 1 -l 0 -f");
            _out.WriteLine($"JxrEncApp exit={e1}\n{so1}\n{se1}");
            e1.ShouldBe(0, "JxrEncApp must encode the chroma BMP");

            var jxr = File.ReadAllBytes(jxrPath);
            var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr); // our decode

            var (e2, so2, se2) = Run(decApp, $"-i \"{jxrPath}\" -o \"{refPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={e2}\n{so2}\n{se2}");
            e2.ShouldBe(0, "JxrDecApp must decode the chroma file");
            var (rw, rh, rr, rg, rb) = ReadBmp24(refPath);

            dw.ShouldBe(w);
            dh.ShouldBe(h);
            rw.ShouldBe(w);
            rh.ShouldBe(h);
            int diffs = 0, maxd = 0;
            var first = new System.Collections.Generic.List<string>();
            for (var i = 0; i < w * h; i++)
            {
                int er = Math.Abs(dr[i] - rr[i]), eg = Math.Abs(dg[i] - rg[i]), eb = Math.Abs(db[i] - rb[i]);
                if (er != 0 || eg != 0 || eb != 0)
                {
                    diffs++;
                    maxd = Math.Max(maxd, Math.Max(er, Math.Max(eg, eb)));
                    if (first.Count < 12) first.Add($"({i % w},{i / w}) R {dr[i]}/{rr[i]} G {dg[i]}/{rg[i]} B {db[i]}/{rb[i]}");
                }
            }
            _out.WriteLine($"d{sub} {kind} {w}x{h}: diffs={diffs}/{w * h} maxd={maxd}");
            foreach (var f in first) _out.WriteLine(f);
            diffs.ShouldBe(0, $"d{sub} {kind} {w}x{h}: {diffs} pixels differ, maxd={maxd}");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(refPath)) File.Delete(refPath);
        }
    }

    // C2b — chroma POT overlap decode. Same as the C5 chroma decode oracle but with the Photo
    // Overlap Transform on (-l 1 = OL_ONE, -l 2 = OL_TWO): a subsampled, overlapped file from the
    // reference JxrEncApp must decode through our codec to exactly what JxrDecApp reconstructs.
    // This exercises the reduced-grid chroma POT post-filters (ChromaOverlapTransform), including
    // the corner-prediction state on multi-MB grids and the non-16-aligned pad-then-crop path.
    [Theory]
    [InlineData(16, 16, "gradient", 1, 1)] // 1x1 MB (left==right, top==bottom degenerate)
    [InlineData(32, 16, "gradient", 1, 1)] // 2x1
    [InlineData(16, 32, "gradient", 1, 1)] // 1x2
    [InlineData(32, 32, "gradient", 1, 1)] // 2x2 (first interior corner predictions)
    [InlineData(48, 32, "gradient", 1, 1)]
    [InlineData(64, 48, "gradient", 1, 1)]
    [InlineData(64, 48, "random", 1, 1)]
    [InlineData(80, 80, "gradient", 1, 1)]
    [InlineData(17, 13, "gradient", 1, 1)] // non-16-aligned
    [InlineData(34, 30, "random", 1, 1)]
    [InlineData(272, 16, "gradient", 1, 1)] // 17 MB wide, single row — staggered window across many cols
    [InlineData(96, 64, "random", 2, 2)]    // 6x4 MB, 422 OL_TWO interior
    // OL_TWO + subsampled chroma needs ≥ 2 MB width (jxrlib refuses 16-wide), so start at 32.
    [InlineData(32, 32, "gradient", 1, 2)]
    [InlineData(48, 32, "gradient", 1, 2)]
    [InlineData(64, 48, "random", 1, 2)]
    [InlineData(80, 80, "gradient", 1, 2)]
    [InlineData(34, 30, "gradient", 1, 2)]
    [InlineData(16, 16, "gradient", 2, 1)]
    [InlineData(32, 32, "gradient", 2, 1)]
    [InlineData(48, 32, "gradient", 2, 1)]
    [InlineData(64, 48, "random", 2, 1)]
    [InlineData(80, 80, "gradient", 2, 1)]
    [InlineData(34, 30, "random", 2, 1)]
    [InlineData(32, 32, "gradient", 2, 2)]
    [InlineData(48, 32, "gradient", 2, 2)]
    [InlineData(64, 48, "random", 2, 2)]
    [InlineData(80, 80, "gradient", 2, 2)]
    [InlineData(34, 30, "gradient", 2, 2)]
    public void JxrlibChromaEncode_Overlap_DecodedByUs_MatchesJxrDecApp(int w, int h, string kind, int sub, int ol)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        var decApp = FindOracle("JxrDecApp.exe");
        if (encApp is null || decApp is null) { _out.WriteLine("oracle binaries not found — skipping."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x5A + w * 31 + h + sub * 7 + ol),
            _ => Gradient(w, h),
        };

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_chol{sub}_{ol}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        var refPath = tmp + "_ref.bmp";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // -d 1 = YUV420, -d 2 = YUV422; -l {ol} overlap; -q 1 lossless coeffs; -f spatial; -c 0 24bppBGR.
            var (e1, so1, se1) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d {sub} -q 1 -l {ol} -f");
            _out.WriteLine($"JxrEncApp exit={e1}\n{so1}\n{se1}");
            e1.ShouldBe(0, "JxrEncApp must encode the chroma BMP");

            var jxr = File.ReadAllBytes(jxrPath);
            var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr); // our decode

            var (e2, so2, se2) = Run(decApp, $"-i \"{jxrPath}\" -o \"{refPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={e2}\n{so2}\n{se2}");
            e2.ShouldBe(0, "JxrDecApp must decode the chroma file");
            var (rw, rh, rr, rg, rb) = ReadBmp24(refPath);

            dw.ShouldBe(w);
            dh.ShouldBe(h);
            rw.ShouldBe(w);
            rh.ShouldBe(h);
            int diffs = 0, maxd = 0;
            var first = new System.Collections.Generic.List<string>();
            for (var i = 0; i < w * h; i++)
            {
                int er = Math.Abs(dr[i] - rr[i]), eg = Math.Abs(dg[i] - rg[i]), eb = Math.Abs(db[i] - rb[i]);
                if (er != 0 || eg != 0 || eb != 0)
                {
                    diffs++;
                    maxd = Math.Max(maxd, Math.Max(er, Math.Max(eg, eb)));
                    if (first.Count < 12) first.Add($"({i % w},{i / w}) R {dr[i]}/{rr[i]} G {dg[i]}/{rg[i]} B {db[i]}/{rb[i]}");
                }
            }
            _out.WriteLine($"d{sub} OL{ol} {kind} {w}x{h}: diffs={diffs}/{w * h} maxd={maxd}");
            foreach (var f in first) _out.WriteLine(f);
            diffs.ShouldBe(0, $"d{sub} OL{ol} {kind} {w}x{h}: {diffs} pixels differ, maxd={maxd}");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(refPath)) File.Delete(refPath);
        }
    }

    // Chroma ENCODE byte-match: our entire codestream for a subsampled image must be byte-for-byte
    // identical to what the reference JxrEncApp emits (-d 1 = YUV420, -d 2 = YUV422, -q 1 lossless,
    // -l {ol}). Exercises the <<3-scaled colour load, the 5-tap chroma downsample, the reduced-grid
    // forward PCT + POT pre-filter (OL_ONE/OL_TWO), and the YUV420/422 plane header.
    [Theory]
    [InlineData(16, 16, "gradient", 1, 0)]
    [InlineData(32, 16, "gradient", 1, 0)]
    [InlineData(32, 32, "gradient", 1, 0)]
    [InlineData(48, 32, "gradient", 1, 0)]
    [InlineData(64, 48, "random", 1, 0)]
    [InlineData(80, 80, "gradient", 1, 0)]
    [InlineData(16, 16, "gradient", 2, 0)]
    [InlineData(48, 32, "gradient", 2, 0)]
    [InlineData(64, 48, "random", 2, 0)]
    [InlineData(80, 80, "gradient", 2, 0)]
    [InlineData(17, 13, "gradient", 1, 0)] // sub-MB / non-16-aligned
    [InlineData(34, 30, "random", 1, 0)]
    [InlineData(33, 40, "random", 2, 0)]
    [InlineData(100, 60, "gradient", 2, 0)]
    // OL_ONE
    [InlineData(16, 16, "gradient", 1, 1)]
    [InlineData(32, 32, "gradient", 1, 1)]
    [InlineData(48, 32, "gradient", 1, 1)]
    [InlineData(64, 48, "random", 1, 1)]
    [InlineData(80, 80, "gradient", 1, 1)]
    [InlineData(16, 16, "gradient", 2, 1)]
    [InlineData(48, 32, "gradient", 2, 1)]
    [InlineData(64, 48, "random", 2, 1)]
    [InlineData(80, 80, "gradient", 2, 1)]
    [InlineData(34, 30, "random", 1, 1)]
    [InlineData(33, 40, "gradient", 2, 1)]
    // OL_TWO (jxlib requires >= 2 MB wide for subsampled + 2 levels of overlap)
    [InlineData(32, 32, "gradient", 1, 2)]
    [InlineData(48, 32, "gradient", 1, 2)]
    [InlineData(64, 48, "random", 1, 2)]
    [InlineData(80, 80, "gradient", 1, 2)]
    [InlineData(32, 32, "gradient", 2, 2)]
    [InlineData(48, 32, "gradient", 2, 2)]
    [InlineData(64, 48, "random", 2, 2)]
    [InlineData(80, 80, "gradient", 2, 2)]
    [InlineData(34, 30, "random", 1, 2)]
    [InlineData(96, 64, "gradient", 2, 2)]
    public void OurEncode_Chroma_CodestreamMatchesJxrlib(int w, int h, string kind, int sub, int ol)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x7E5 + w * 31 + h + sub * 7 + ol) : Gradient(w, h);

        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: ol,
            internalClrFmt: sub == 1 ? JxrInternalColorFormat.YUV420 : JxrInternalColorFormat.YUV422);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_chenc{sub}_{ol}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d {sub} -q 1 -l {ol} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (d{sub} OL{ol} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (d{sub} OL{ol} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Facade end-to-end: a subsampled .jxr we produce via JxrImageCodec.EncodeRgb24 (container write +
    // codestream) must decode identically through our own JxrImageCodec.DecodeRgb24 and through the
    // reference JxrDecApp. (Codestream byte-equality is proven separately; this pins the .jxr container
    // write for the subsampled path and the public facade round-trip.)
    [Theory]
    [InlineData(64, 48, "gradient", 1, 0)]
    [InlineData(64, 48, "random", 2, 1)]
    [InlineData(80, 80, "gradient", 1, 2)]
    [InlineData(34, 30, "random", 2, 1)]
    public void JxrImageCodec_Chroma_RoundTripsViaJxrDecApp(int w, int h, string kind, int sub, int ol)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x33 + w * 7 + h + sub + ol) : Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: ol,
            internalClrFmt: sub == 1 ? JxrInternalColorFormat.YUV420 : JxrInternalColorFormat.YUV422);

        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr); // our decode of our file

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_fac{sub}_{ol}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var refPath = tmp + "_ref.bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (e, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{refPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={e}\n{so}\n{se}");
            e.ShouldBe(0, "JxrDecApp must decode our subsampled file");
            var (rw, rh, rr, rg, rb) = ReadBmp24(refPath);

            (dw, dh).ShouldBe((w, h));
            (rw, rh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(rr[i], $"R[{i}] (facade d{sub} OL{ol} {kind} {w}x{h})");
                dg[i].ShouldBe(rg[i], $"G[{i}]");
                db[i].ShouldBe(rb[i], $"B[{i}]");
            }
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(refPath)) File.Delete(refPath);
        }
    }

    // Band control — TRIM_FLEXBITS (jxrlib -F N, 1..15): keeps all bands but trims the N low-order
    // bits of the flexbits refinement plane (the consumer's controlled-precision HDR-master mode).
    // Our codestream must be byte-for-byte identical to JxrEncApp's, across trim levels + overlap.
    [Theory]
    [InlineData(64, 48, "gradient", 1, 0)]
    [InlineData(64, 48, "random", 3, 0)]
    [InlineData(64, 48, "random", 7, 0)]
    [InlineData(64, 48, "random", 15, 0)]
    [InlineData(80, 80, "gradient", 5, 0)]
    [InlineData(48, 32, "random", 2, 1)] // OL_ONE
    [InlineData(64, 48, "random", 7, 1)]
    [InlineData(80, 80, "gradient", 4, 1)]
    [InlineData(48, 32, "random", 3, 2)] // OL_TWO
    [InlineData(64, 48, "random", 11, 2)]
    [InlineData(17, 13, "random", 6, 1)] // non-16-aligned
    [InlineData(33, 40, "random", 9, 2)]
    public void OurEncode_TrimFlexBits_CodestreamMatchesJxrlib(int w, int h, string kind, int trim, int ol)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x7E5 + w * 31 + h + trim) : Gradient(w, h);

        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: ol, trimFlexBits: trim);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_trim{trim}_{ol}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -F {trim} -l {ol} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (F{trim} OL{ol} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (F{trim} OL{ol} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // NO_FLEXBITS (jxrlib -s 1) for BD8 RGB: the flexbits refinement plane is omitted. Since
    // sbSubband != SB_ALL, jxrlib forces scaled-arithmetic even at QP 1 — so this is the first
    // exercise of the scaled-444 path (<<3 colour load + chroma ×2 NormalizeEnc + >>3 output). Our
    // codestream must be byte-for-byte identical to JxrEncApp -s 1.
    [Theory]
    [InlineData(16, 16, "gradient", 0)]
    [InlineData(32, 32, "gradient", 0)]
    [InlineData(48, 32, "gradient", 0)]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "gradient", 0)]
    [InlineData(64, 48, "random", 1)]   // OL_ONE
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(64, 48, "random", 2)]   // OL_TWO
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(17, 13, "random", 0)]   // non-16-aligned
    [InlineData(33, 40, "random", 1)]
    public void OurEncode_NoFlexBits_CodestreamMatchesJxrlib(int w, int h, string kind, int ol)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x4F + w * 17 + h + ol) : Gradient(w, h);
        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: ol, noFlexBits: true);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_nf{ol}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -s 1 -l {ol} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (NoFlex OL{ol} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (NoFlex OL{ol} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // NO_FLEXBITS decode (scaled-444): our decode of our NO_FLEXBITS RGB file must match JxrDecApp's.
    [Theory]
    [InlineData(64, 48, "random", 0)]
    [InlineData(80, 80, "random", 1)]
    [InlineData(48, 32, "random", 2)]
    [InlineData(33, 40, "random", 1)]
    public void OurEncode_NoFlexBits_DecodesLikeJxrDecApp(int w, int h, string kind, int ol)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0xC3 + w * 5 + h + ol) : Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: ol, noFlexBits: true);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_nfdec{ol}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var refPath = tmp + "_ref.bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (e, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{refPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={e}\n{so}\n{se}");
            e.ShouldBe(0, "JxrDecApp must decode our NO_FLEXBITS file");
            var (rw, rh, rr, rg, rb) = ReadBmp24(refPath);
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(rr[i], $"R[{i}] (NoFlex OL{ol} {w}x{h})");
                dg[i].ShouldBe(rg[i], $"G[{i}]");
                db[i].ShouldBe(rb[i], $"B[{i}]");
            }
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(refPath)) File.Delete(refPath);
        }
    }

    // TRIM_FLEXBITS decode: a trimmed .jxr we produce must decode identically through our own
    // DecodeRgb24 and the reference JxrDecApp (exercises the decode-side flexbits-trim path, which
    // — like the encode side — was unreached until now).
    [Theory]
    [InlineData(64, 48, "random", 3, 0)]
    [InlineData(64, 48, "random", 7, 1)]
    [InlineData(80, 80, "random", 5, 2)]
    [InlineData(33, 40, "random", 9, 1)]
    public void OurEncode_TrimFlexBits_DecodesLikeJxrDecApp(int w, int h, string kind, int trim, int ol)
    {
        var decApp = FindOracle("JxrDecApp.exe");
        if (decApp is null) { _out.WriteLine("JxrDecApp.exe not found — skipping."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x91 + w * 13 + h + trim) : Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: ol, trimFlexBits: trim);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr); // our decode of our trimmed file

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_trimdec{trim}_{ol}_{Guid.NewGuid():N}");
        var jxrPath = tmp + ".jxr";
        var refPath = tmp + "_ref.bmp";
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var (e, so, se) = Run(decApp, $"-i \"{jxrPath}\" -o \"{refPath}\" -c 0");
            _out.WriteLine($"JxrDecApp exit={e}\n{so}\n{se}");
            e.ShouldBe(0, "JxrDecApp must decode our trimmed file");
            var (rw, rh, rr, rg, rb) = ReadBmp24(refPath);
            (dw, dh).ShouldBe((w, h));
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(rr[i], $"R[{i}] (trim F{trim} OL{ol} {w}x{h})");
                dg[i].ShouldBe(rg[i], $"G[{i}]");
                db[i].ShouldBe(rb[i], $"B[{i}]");
            }
        }
        finally
        {
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
            if (File.Exists(refPath)) File.Delete(refPath);
        }
    }

    // Rung 7f.2/7f.3 — the strongest overlap check: our entire codestream must be byte-for-byte
    // identical to what the reference JxrEncApp emits for the same image and settings.
    [Theory]
    [InlineData(16, 16, "gradient", 1)]
    [InlineData(32, 16, "gradient", 1)]
    [InlineData(48, 32, "gradient", 1)]
    [InlineData(64, 48, "random", 1)]
    [InlineData(80, 80, "gradient", 1)]
    [InlineData(16, 16, "gradient", 2)]
    [InlineData(32, 16, "gradient", 2)]
    [InlineData(48, 32, "gradient", 2)]
    [InlineData(64, 48, "random", 2)]
    [InlineData(80, 80, "gradient", 2)]
    // sub-MB / non-16-aligned dimensions (partial macroblocks edge-replicated)
    [InlineData(17, 13, "gradient", 1)]
    [InlineData(33, 40, "random", 1)]
    [InlineData(100, 60, "gradient", 2)]
    public void OurEncode_Overlap_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind switch
        {
            "random" => Random(w, h, seed: 0x7E5 + w * 31 + h),
            _ => Gradient(w, h),
        };

        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: overlap);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_cmp{overlap}_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (OL{overlap} {kind} {w}x{h})");
            for (var i = 0; i < ours.Length; i++)
                ours[i].ShouldBe(theirs[i], $"codestream byte {i} (0x{i:X}) (OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Multi-tile (SOFT tiles) — the strongest check: our entire multi-tile codestream must be
    // byte-for-byte identical to what the reference JxrEncApp emits with the same uniform tiling
    // (-U cols rows). Evenly-divisible MB grids keep the uniform split unambiguous. Exercises the
    // tiling IMAGE_HEADER fields, per-tile packet headers, INDEX_TABLE_TILES, and per-tile entropy.
    [Theory]
    [InlineData(64, 32, "gradient", 0, 2, 1)]   // 4x2 MB -> 2 cols of 2 MB
    [InlineData(64, 64, "gradient", 0, 2, 2)]   // 4x4 MB -> 2x2 tiles of 2x2 MB
    [InlineData(128, 64, "random", 0, 4, 2)]    // 8x4 MB -> 4x2 tiles of 2x2 MB
    [InlineData(544, 16, "gradient", 0, 2, 1)]  // 34x1 MB -> 2 tiles of 17 MB (spans the 16-MB group boundary)
    [InlineData(64, 32, "gradient", 1, 2, 1)]   // overlap OL_ONE
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]  // overlap OL_TWO
    public void OurEncode_Tiled_CodestreamMatchesJxrlib(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x7E5 + w * 31 + h) : Gradient(w, h);

        int mbW = (w + 15) / 16, mbH = (h + 15) / 16;
        var layout = JxrTileLayout.Uniform(mbW, mbH, cols, rows);
        var ours = JxrCodestream.Encode(r, g, b, w, h, overlap: overlap, tiles: layout);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_tile_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // JxrEncApp -U takes (rows, cols): the first count is horizontal slices (rows), the second
            // vertical slices (columns) — the opposite of our JxrTileLayout.Uniform(.., cols, rows).
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -U {rows} {cols} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the tiled BMP");

            var theirs = JxrContainer.Read(File.ReadAllBytes(jxrPath)).Codestream;
            int n = Math.Min(ours.Length, theirs.Length);
            int firstDiff = -1;
            for (var i = 0; i < n; i++) if (ours[i] != theirs[i]) { firstDiff = i; break; }
            string ctx = firstDiff < 0 ? "" :
                $" ours[{firstDiff}..]={BitConverter.ToString(ours[firstDiff..Math.Min(firstDiff + 8, ours.Length)])}" +
                $" theirs[{firstDiff}..]={BitConverter.ToString(theirs[firstDiff..Math.Min(firstDiff + 8, theirs.Length)])}";
            _out.WriteLine($"ours={ours.Length} theirs={theirs.Length} firstDiff={firstDiff}{ctx}");
            firstDiff.ShouldBe(-1, $"first differing byte 0x{(firstDiff < 0 ? 0 : firstDiff):X} (tiled {cols}x{rows} OL{overlap} {kind} {w}x{h}){ctx}");
            ours.Length.ShouldBe(theirs.Length, $"codestream length (tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Multi-tile decode: a tiled .jxr produced by the reference JxrEncApp (-U) must decode losslessly
    // through our container reader + multi-tile codestream decoder (INDEX_TABLE_TILES + per-tile
    // entropy + whole-image inverse overlap). The complement of the encode byte-match.
    [Theory]
    [InlineData(64, 32, "gradient", 0, 2, 1)]
    [InlineData(64, 64, "gradient", 0, 2, 2)]
    [InlineData(128, 64, "random", 0, 4, 2)]
    [InlineData(544, 16, "gradient", 0, 2, 1)]
    [InlineData(64, 64, "random", 1, 2, 2)]
    [InlineData(128, 64, "gradient", 2, 4, 2)]
    public void JxrlibEncode_Tiled_DecodedByUs_IsLossless(int w, int h, string kind, int overlap, int cols, int rows)
    {
        var encApp = FindOracle("JxrEncApp.exe");
        if (encApp is null) { _out.WriteLine("JxrEncApp.exe not found — skipping oracle test."); return; }

        var (r, g, b) = kind == "random" ? Random(w, h, seed: 0x7E5 + w * 31 + h) : Gradient(w, h);

        var tmp = Path.Combine(Path.GetTempPath(), $"jxr_tdec_{Guid.NewGuid():N}");
        var bmpPath = tmp + ".bmp";
        var jxrPath = tmp + ".jxr";
        WriteBmp24(bmpPath, w, h, r, g, b);
        try
        {
            // -U takes (rows, cols) — see OurEncode_Tiled_CodestreamMatchesJxrlib.
            var (exit, stdout, stderr) = Run(encApp, $"-i \"{bmpPath}\" -o \"{jxrPath}\" -c 0 -d 3 -q 1 -l {overlap} -U {rows} {cols} -f");
            _out.WriteLine($"JxrEncApp exit={exit}\n{stdout}\n{stderr}");
            exit.ShouldBe(0, "JxrEncApp must encode the tiled BMP");

            var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(File.ReadAllBytes(jxrPath));
            dw.ShouldBe(w);
            dh.ShouldBe(h);
            for (var i = 0; i < w * h; i++)
            {
                dr[i].ShouldBe(r[i], $"R[{i}] (tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
                dg[i].ShouldBe(g[i], $"G[{i}] (tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
                db[i].ShouldBe(b[i], $"B[{i}] (tiled {cols}x{rows} OL{overlap} {kind} {w}x{h})");
            }
        }
        finally
        {
            if (File.Exists(bmpPath)) File.Delete(bmpPath);
            if (File.Exists(jxrPath)) File.Delete(jxrPath);
        }
    }

    // Multi-tile self round-trip (no oracle needed): our tiled encode → our tiled decode is lossless.
    [Theory]
    [InlineData(64, 32, 2, 1, 0)]
    [InlineData(64, 64, 2, 2, 1)]
    [InlineData(128, 64, 4, 2, 2)]
    [InlineData(544, 16, 2, 1, 0)]
    [InlineData(96, 80, 4, 3, 1)] // uneven uniform split: 6 MB / 4 cols = [1,1,1,3], 5 MB / 3 rows = [1,1,3]
    public void OurEncode_Tiled_OurDecode_RoundTrips(int w, int h, int cols, int rows, int overlap)
    {
        var (r, g, b) = Gradient(w, h);
        int mbW = (w + 15) / 16, mbH = (h + 15) / 16;
        var layout = JxrTileLayout.Uniform(mbW, mbH, cols, rows);

        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h, overlap: overlap, tiles: layout);
        var (dw, dh, dr, dg, db) = JxrImageCodec.DecodeRgb24(jxr);

        dw.ShouldBe(w);
        dh.ShouldBe(h);
        for (var i = 0; i < w * h; i++)
        {
            dr[i].ShouldBe(r[i], $"R[{i}] (self {cols}x{rows} OL{overlap} {w}x{h})");
            dg[i].ShouldBe(g[i], $"G[{i}] (self {cols}x{rows} OL{overlap} {w}x{h})");
            db[i].ShouldBe(b[i], $"B[{i}] (self {cols}x{rows} OL{overlap} {w}x{h})");
        }
    }

    /// <summary>
    /// Rung 7e.4 — the Windows-Photo milestone: our encoded <c>.jxr</c> must open in
    /// WIC (<c>System.Windows.Media.Imaging.BitmapDecoder</c>, what Windows Photo /
    /// Microsoft Photos use), instantiate a frame at the right dimensions, and decode
    /// to non-zero pixels (WIC silently yields all-zero pixels on some malformed
    /// codestreams, so Frames>0 alone isn't enough). Windows-only; no-ops elsewhere.
    /// </summary>
    [Theory]
    [InlineData(16, 16)]
    [InlineData(48, 32)]
    [InlineData(64, 48)]
    public void OurEncode_OpensInWic(int w, int h)
    {
        if (!OperatingSystem.IsWindows()) { _out.WriteLine("Not Windows — skipping WIC test."); return; }

        var (r, g, b) = Gradient(w, h);
        var jxr = JxrImageCodec.EncodeRgb24(r, g, b, w, h);
        var jxrPath = Path.Combine(Path.GetTempPath(), $"jxr_wic_{Guid.NewGuid():N}.jxr");
        File.WriteAllBytes(jxrPath, jxr);
        try
        {
            var wic = WicOracle.Probe(jxrPath);
            if (!wic.Available) { _out.WriteLine($"WIC unavailable — skipping. {wic.RawOutput}"); return; }
            _out.WriteLine(wic.RawOutput);

            wic.IsValidImage.ShouldBeTrue($"WIC must accept our .jxr ({w}x{h}); error: {wic.Error}");
            wic.Width.ShouldBe(w);
            wic.Height.ShouldBe(h);
            wic.HasNonZeroPixels.ShouldBeTrue("WIC must decode to non-zero pixels (not a silent all-zero decode)");
        }
        finally
        {
            File.Delete(jxrPath);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static (int[] r, int[] g, int[] b) Random(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var r = new int[w * h]; var g = new int[w * h]; var b = new int[w * h];
        for (var i = 0; i < w * h; i++) { r[i] = rng.Next(256); g[i] = rng.Next(256); b[i] = rng.Next(256); }
        return (r, g, b);
    }

    private static (int[] r, int[] g, int[] b) Flat(int w, int h, int r, int g, int b)
    {
        var ra = new int[w * h]; var ga = new int[w * h]; var ba = new int[w * h];
        Array.Fill(ra, r); Array.Fill(ga, g); Array.Fill(ba, b);
        return (ra, ga, ba);
    }

    private static (int[] r, int[] g, int[] b) Gradient(int w, int h)
    {
        var r = new int[w * h]; var g = new int[w * h]; var b = new int[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int i = y * w + x;
                r[i] = (x * 3 + y * 2) & 0xff;
                g[i] = (128 + x - y) & 0xff;
                b[i] = (x + y * 3) & 0xff;
            }
        return (r, g, b);
    }

    /// <summary>Write a 24bpp BGR bottom-up BMP (what <c>JxrEncApp -c 0</c> reads).</summary>
    private static void WriteBmp24(string path, int w, int h, int[] r, int[] g, int[] b)
    {
        int stride = (w * 3 + 3) & ~3;
        int dataSize = stride * h;
        int fileSize = 54 + dataSize;
        var bytes = new byte[fileSize];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);     // pixel-data offset
        BitConverter.GetBytes(40).CopyTo(bytes, 14);     // BITMAPINFOHEADER size
        BitConverter.GetBytes(w).CopyTo(bytes, 18);
        BitConverter.GetBytes(h).CopyTo(bytes, 22);      // positive ⇒ bottom-up
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 34);
        for (var row = 0; row < h; row++)
        {
            int srcRow = h - 1 - row; // BMP rows run bottom-to-top
            int dst = 54 + row * stride;
            for (var x = 0; x < w; x++)
            {
                int i = srcRow * w + x;
                bytes[dst + x * 3 + 0] = (byte)b[i];
                bytes[dst + x * 3 + 1] = (byte)g[i];
                bytes[dst + x * 3 + 2] = (byte)r[i];
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Parse a 24bpp BGR bottom-up BMP (what <c>JxrDecApp -c 0</c> writes).</summary>
    private static (int w, int h, int[] r, int[] g, int[] b) ReadBmp24(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M')
            throw new InvalidDataException("Not a BMP");
        int dataOffset = BitConverter.ToInt32(bytes, 10);
        int width = BitConverter.ToInt32(bytes, 18);
        int height = BitConverter.ToInt32(bytes, 22);
        int bpp = BitConverter.ToInt16(bytes, 28);
        if (bpp != 24) throw new InvalidDataException($"Expected 24bpp BMP, got {bpp}");
        bool topDown = height < 0;
        int h = Math.Abs(height);
        int stride = (width * 3 + 3) & ~3;

        var r = new int[width * h]; var g = new int[width * h]; var b = new int[width * h];
        for (var row = 0; row < h; row++)
        {
            int srcRow = topDown ? row : (h - 1 - row);
            int src = dataOffset + srcRow * stride;
            for (var x = 0; x < width; x++)
            {
                int p = src + x * 3;
                int i = row * width + x;
                b[i] = bytes[p];
                g[i] = bytes[p + 1];
                r[i] = bytes[p + 2];
            }
        }
        return (width, h, r, g, b);
    }

    private static (int exit, string stdout, string stderr) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, so, se);
    }

    /// <summary>Walk up from the test output directory to find Oracle/bin/&lt;exe&gt;.</summary>
    private static string? FindOracle(string exe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Oracle", "bin", exe);
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(dir.FullName, "tests", "SharpAstro.Codecs.Tests", "Oracle", "bin", exe);
            if (File.Exists(nested)) return nested;
        }
        return null;
    }
}
