using System.Buffers.Binary;
using System.Text;
using SharpAstro.Png;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The APNG compositor's fast paths against the plain code they replace, in the manner of
/// <c>JpegIdctEquivalenceTests</c>: the vector OVER (<see cref="PngCompositor.OverRowVector"/>) against the scalar
/// reference (<see cref="PngCompositor.OverRowScalar"/>) byte for byte, the opaque-canvas arithmetic they both lean on
/// against the general formula for every input, the per-row RGBA8 expansion against a per-pixel reading of its
/// documented mapping, and every Pillow reference frame through both blend paths. A machine only ever runs one blend
/// path by default, so without these the other could rot unseen.
/// </summary>
public sealed class PngApngKernelTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "apng");

    // ---------------------------------------------------------------- the arithmetic the vector path is built on

    /// <summary>
    /// With an opaque canvas (<c>ab = 255</c>) the general formula collapses to <c>(af·cf + (255 − af)·cb + 127) / 255</c> with
    /// the alpha staying 255. <see cref="PngCompositor.Over"/> takes that shortcut, so it is held to the general form
    /// for all 255 · 256 · 256 combinations of foreground alpha, foreground colour and canvas colour.
    /// </summary>
    [Fact]
    public void OpaqueCanvasShortcut_EqualsTheGeneralFormula_ForEveryAlphaAndColour()
    {
        byte[] source = [0, 0, 0, 0];
        byte[] target = [0, 0, 0, 255];
        for (var af = 1; af < 255; af++)
            for (var cf = 0; cf < 256; cf++)
                for (var cb = 0; cb < 256; cb++)
                {
                    source[0] = (byte)cf; source[3] = (byte)af;
                    target[0] = (byte)cb; target[3] = 255;
                    PngCompositor.Over(source, target);

                    // the general form with ab = 255
                    var alphaNumerator = af * 255 + 255 * (255 - af);
                    var general = (af * 255 * cf + (255 - af) * 255 * cb + alphaNumerator / 2) / alphaNumerator;
                    if (target[0] != general || target[3] != 255)
                        Assert.Fail($"af {af} cf {cf} cb {cb}: got {target[0]}/{target[3]}, general form {general}/255");
                }
    }

    /// <summary>The vector path divides by 255 as <c>(t + 1 + (t &gt;&gt; 8)) &gt;&gt; 8</c>; exact for every t it can see (at most 255·255 + 127).</summary>
    [Fact]
    public void DivisionBy255WithoutDividing_IsExact_ForEveryValueTheBlendCanProduce()
    {
        for (var t = 0; t <= 255 * 255 + 127; t++)
            ((t + 1 + (t >> 8)) >> 8).ShouldBe(t / 255, $"t = {t}");
    }

    // ---------------------------------------------------------------- vector against scalar

    [Fact]
    public void OverRowVector_EqualsScalar_OverEveryAlphaAndColour_OnAnOpaqueCanvas()
    {
        // one row per (af, cf): 256 pixels whose canvas colours run through every value in each channel, so every
        // (af, cf, cb) is met in every lane position of a group
        var source = new byte[256 * 4];
        var targetVector = new byte[256 * 4];
        var targetScalar = new byte[256 * 4];
        for (var af = 0; af < 256; af++)
            for (var cf = 0; cf < 256; cf++)
            {
                for (var i = 0; i < 256; i++)
                {
                    source[i * 4] = (byte)cf;
                    source[i * 4 + 1] = (byte)(cf ^ 0x55);
                    source[i * 4 + 2] = (byte)(255 - cf);
                    source[i * 4 + 3] = (byte)af;
                    targetVector[i * 4] = (byte)i;
                    targetVector[i * 4 + 1] = (byte)((i + 85) & 255);
                    targetVector[i * 4 + 2] = (byte)(255 - i);
                    targetVector[i * 4 + 3] = 255;
                }
                targetVector.CopyTo(targetScalar, 0);
                PngCompositor.OverRowVector(source, targetVector);
                PngCompositor.OverRowScalar(source, targetScalar);
                if (!targetVector.AsSpan().SequenceEqual(targetScalar))
                    Assert.Fail($"af {af} cf {cf}: the vector row differs from the scalar one");
            }
    }

    /// <summary>
    /// Random rows built out of runs, so that the four-pixel groups the vector path looks at as a whole are met in every
    /// class: source all opaque / all transparent / mixed, canvas all opaque / all transparent / partly transparent /
    /// mixed, in every combination, at lengths that leave every tail size.
    /// </summary>
    [Fact]
    public void OverRowVector_EqualsScalar_OnRandomRowsOfEveryGroupClass_AndEveryTailLength()
    {
        var rng = new Random(38);
        for (var iteration = 0; iteration < 6000; iteration++)
        {
            var pixels = rng.Next(0, 70);
            var source = new byte[pixels * 4];
            var target = new byte[pixels * 4];
            Fill(rng, source);
            Fill(rng, target);
            var expected = (byte[])target.Clone();
            var actual = (byte[])target.Clone();
            PngCompositor.OverRowScalar(source, expected);
            PngCompositor.OverRowVector(source, actual);
            actual.ShouldBe(expected, $"iteration {iteration}, {pixels} pixels");
        }

        // runs of alphas drawn from a few classes; colours random, a little of every kind of alpha
        static void Fill(Random rng, byte[] row)
        {
            for (var p = 0; p < row.Length / 4;)
            {
                var run = rng.Next(1, 12);
                var kind = rng.Next(4);
                for (var k = 0; k < run && p < row.Length / 4; k++, p++)
                {
                    row[p * 4] = (byte)rng.Next(256);
                    row[p * 4 + 1] = (byte)rng.Next(256);
                    row[p * 4 + 2] = (byte)rng.Next(256);
                    row[p * 4 + 3] = kind switch { 0 => 0, 1 => 255, 2 => (byte)rng.Next(256), _ => rng.Next(2) == 0 ? (byte)255 : (byte)rng.Next(1, 255) };
                }
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void OverRowVector_LeavesAnythingPastTheRowAlone(int pixels)
    {
        var source = new byte[(pixels + 3) * 4];
        var target = new byte[(pixels + 3) * 4];
        new Random(pixels).NextBytes(source);
        new Random(pixels + 100).NextBytes(target);
        var guard = target.AsSpan(pixels * 4).ToArray();
        PngCompositor.OverRowVector(source.AsSpan(0, pixels * 4), target.AsSpan(0, pixels * 4));
        target.AsSpan(pixels * 4).ToArray().ShouldBe(guard);
    }

    // ---------------------------------------------------------------- whole animations through both paths

    private static List<(string File, int Frame, string Sha)> References()
    {
        var rows = new List<(string, int, string)>();
        var header = true;
        foreach (var line in File.ReadAllLines(Path.Combine(FixtureDir, "apng-reference.tsv")))
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (header)
            {
                header = false;
                continue;
            }
            var f = line.Split('\t');
            rows.Add((f[0], int.Parse(f[1]), f[16]));
        }
        return rows;
    }

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in References().Select(r => r.File).Distinct())
            data.Add(file);
        return data;
    }

    /// <summary>Every frame of every Pillow fixture through the vector blend and through the scalar one: both reproduce Pillow's canvas, and (for the one file where Pillow's OVER is not the spec's) each other.</summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryFixtureFrame_IsTheSame_ThroughTheScalarAndTheVectorBlend_AndMatchesPillow(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, file));
        var rows = References().Where(r => r.File == file).ToList();
        var vector = Canvases(bytes, forceScalar: false);
        var scalar = Canvases(bytes, forceScalar: true);

        vector.Count.ShouldBe(rows.Count);
        scalar.Count.ShouldBe(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            vector[i].ShouldBe(scalar[i], $"{file} frame {i}: vector against scalar");
            if (file != "apng-blend-over-partial-alpha.png") // Pillow's OVER differs from the spec's there; PngApngTests pins that
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(vector[i])).ShouldBe(rows[i].Sha, $"{file} frame {i}: against Pillow");
        }
    }

    private static List<byte[]> Canvases(byte[] png, bool forceScalar)
    {
        var before = PngCompositor.ForceScalar;
        PngCompositor.ForceScalar = forceScalar;
        try
        {
            return [.. PngReader.EnumerateFrames(png).Select(f => (byte[])f.Rgba.Clone())];
        }
        finally
        {
            PngCompositor.ForceScalar = before;
        }
    }

    /// <summary>
    /// Random animations: odd canvas sizes (so rows end in every tail), frames at random rectangles, every dispose and blend
    /// op, alpha in runs of every class. The vector and scalar blends must agree frame for frame.
    /// </summary>
    [Fact]
    public void RandomAnimations_GiveTheSameCanvases_ThroughTheScalarAndTheVectorBlend()
    {
        var rng = new Random(3825);
        for (var animation = 0; animation < 40; animation++)
        {
            var png = RandomApng(rng, out var description);
            var vector = Canvases(png, forceScalar: false);
            var scalar = Canvases(png, forceScalar: true);
            vector.Count.ShouldBe(scalar.Count);
            for (var i = 0; i < vector.Count; i++)
                vector[i].ShouldBe(scalar[i], $"animation {animation} ({description}) frame {i}");
        }
    }

    // ---------------------------------------------------------------- the per-row expansion

    /// <summary>The documented mapping of <see cref="PngImage.ExpandToRgba8(Span{byte})"/>, written out one pixel at a time.</summary>
    private static byte[] ReferenceExpand(int colorType, int step, byte[]? palette, byte[]? paletteAlpha, byte[] source, int width)
    {
        var spp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new ArgumentException("colour type") };
        var result = new byte[width * 4];
        for (var x = 0; x < width; x++)
        {
            byte Sample(int channel) => source[(x * spp + channel) * step]; // the high byte of a 16-bit sample
            switch (colorType)
            {
                case 0: result[x * 4] = result[x * 4 + 1] = result[x * 4 + 2] = Sample(0); result[x * 4 + 3] = 255; break;
                case 2: result[x * 4] = Sample(0); result[x * 4 + 1] = Sample(1); result[x * 4 + 2] = Sample(2); result[x * 4 + 3] = 255; break;
                case 4: result[x * 4] = result[x * 4 + 1] = result[x * 4 + 2] = Sample(0); result[x * 4 + 3] = Sample(1); break;
                case 6: result[x * 4] = Sample(0); result[x * 4 + 1] = Sample(1); result[x * 4 + 2] = Sample(2); result[x * 4 + 3] = Sample(3); break;
                case 3:
                    var idx = Sample(0);
                    result[x * 4] = palette![idx * 3]; result[x * 4 + 1] = palette[idx * 3 + 1]; result[x * 4 + 2] = palette[idx * 3 + 2];
                    result[x * 4 + 3] = paletteAlpha is not null && idx < paletteAlpha.Length ? paletteAlpha[idx] : (byte)255;
                    break;
            }
        }
        return result;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(4, 2)]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    public void ExpandRowToRgba8_MatchesThePerPixelMapping(int colorType, int step)
    {
        var rng = new Random(colorType * 10 + step);
        var palette = new byte[256 * 3];
        rng.NextBytes(palette);
        var paletteAlpha = new byte[100];     // shorter than the palette: the rest read as opaque
        rng.NextBytes(paletteAlpha);
        var spp = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        foreach (var alpha in new byte[]?[] { null, paletteAlpha })
            for (var width = 0; width <= 41; width++)
            {
                var source = new byte[width * spp * step];
                rng.NextBytes(source);
                var actual = new byte[width * 4];
                PngImage.ExpandRowToRgba8(colorType, step, colorType == 3 ? palette : null, alpha, source, actual);
                actual.ShouldBe(ReferenceExpand(colorType, step, palette, alpha, source, width), $"type {colorType} step {step} width {width}");
            }
    }

    [Fact]
    public void ExpandRowToRgba8_RefusesAnIndexedRowWithoutAPalette_OrWithAnIndexPastIt()
    {
        var destination = new byte[8];
        Should.Throw<InvalidDataException>(() => PngImage.ExpandRowToRgba8(3, 1, null, null, new byte[2], destination))
            .Message.ShouldContain("no palette");
        // 2 entries, index 2
        Should.Throw<InvalidDataException>(() => PngImage.ExpandRowToRgba8(3, 1, new byte[6], null, [0, 2], destination))
            .Message.ShouldContain("out of range");
        Should.Throw<InvalidDataException>(() => PngImage.ExpandRowToRgba8(5, 1, null, null, new byte[2], destination));
    }

    // ---------------------------------------------------------------- allocation

    /// <summary>
    /// Once the first frame is out, enumerating allocates next to nothing per frame: it used to allocate a samples array
    /// and an expanded RGBA8 array of the frame's size every time (more than two frames' worth), and now reuses one
    /// buffer. What remains is the inflater's own managed state, which is the same however big the frame is, so the
    /// bound is a fraction of one frame, far below what the old code spent and well above what the inflater costs.
    /// </summary>
    [Fact]
    public void Streaming_AllocatesLessThanOneFramePerFrame_OnceTheBuffersExist()
    {
        const int width = 320, height = 240, frames = 24;       // a frame is 300 KB of RGBA8
        var png = SteadyApng(width, height, frames);
        var frameBytes = width * height * 4;

        using var e = PngReader.EnumerateFrames(png).GetEnumerator();
        e.MoveNext().ShouldBeTrue();                            // the canvas, the buffers
        e.MoveNext().ShouldBeTrue();                            // frame 1 sizes the reused samples buffer
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        while (e.MoveNext())
            count++;
        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;

        count.ShouldBe(frames - 2);
        perFrame.ShouldBeLessThan(frameBytes / 3.0, $"{perFrame:N0} bytes per frame against a {frameBytes:N0}-byte frame");
    }

    [Fact]
    public void DecodeFrames_StillHonoursItsLimit()
    {
        var png = SteadyApng(64, 64, 6);
        PngReader.DecodeFrames(png, 64 * 64 * 6).Frames.Count.ShouldBe(6);
        Should.Throw<Exception>(() => PngReader.DecodeFrames(png, 64 * 64 * 6 - 1));
    }

    // ---------------------------------------------------------------- building animations

    private static byte[] SteadyApng(int width, int height, int frames)
    {
        var rng = new Random(77);
        var list = new List<(byte[] Rgba, int W, int H, int X, int Y, int Dispose, int Blend)>();
        for (var i = 0; i < frames; i++)
        {
            var rgba = new byte[width * height * 4];
            for (var p = 0; p < width * height; p++)
            {
                rgba[p * 4] = (byte)(p % width + rng.Next(8));
                rgba[p * 4 + 1] = (byte)(p / width + rng.Next(8));
                rgba[p * 4 + 2] = (byte)(i * 9 + rng.Next(8));
                rgba[p * 4 + 3] = (p / 7) % 3 == 0 ? (byte)rng.Next(256) : (byte)255;
            }
            list.Add((rgba, width, height, 0, 0, 0, i == 0 ? 0 : 1));
        }
        return Assemble(width, height, list);
    }

    private static byte[] RandomApng(Random rng, out string description)
    {
        var width = rng.Next(1, 40);
        var height = rng.Next(1, 24);
        var frames = rng.Next(2, 10);
        var list = new List<(byte[] Rgba, int W, int H, int X, int Y, int Dispose, int Blend)>();
        for (var i = 0; i < frames; i++)
        {
            var w = i == 0 ? width : rng.Next(1, width + 1);
            var h = i == 0 ? height : rng.Next(1, height + 1);
            var x = i == 0 ? 0 : rng.Next(0, width - w + 1);
            var y = i == 0 ? 0 : rng.Next(0, height - h + 1);
            var rgba = new byte[w * h * 4];
            for (var p = 0; p < w * h;)
            {
                var run = rng.Next(1, 12);
                var kind = rng.Next(4);
                for (var k = 0; k < run && p < w * h; k++, p++)
                {
                    rgba[p * 4] = (byte)rng.Next(256);
                    rgba[p * 4 + 1] = (byte)rng.Next(256);
                    rgba[p * 4 + 2] = (byte)rng.Next(256);
                    rgba[p * 4 + 3] = kind switch { 0 => 0, 1 => 255, 2 => (byte)rng.Next(256), _ => rng.Next(2) == 0 ? (byte)255 : (byte)rng.Next(1, 255) };
                }
            }
            list.Add((rgba, w, h, x, y, rng.Next(3), rng.Next(2)));
            if (i == 0)
                rgba[3] = 255;
        }
        description = $"{width}x{height}, {frames} frames";
        return Assemble(width, height, list);
    }

    /// <summary>An APNG whose IDAT is frame 0 (which covers the canvas) and whose other frames are <c>fdAT</c>, from <see cref="PngWriter"/>'s own IDAT payloads.</summary>
    private static byte[] Assemble(int width, int height, List<(byte[] Rgba, int W, int H, int X, int Y, int Dispose, int Blend)> frames)
    {
        var chunks = new List<(string Type, byte[] Data)>();
        uint sequence = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var parts = Split(PngWriter.Encode(f.Rgba, f.W, f.H));
            if (i == 0)
            {
                chunks.Add(parts.First(c => c.Type == "IHDR"));
                var actl = new byte[8];
                BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Count);
                chunks.Add(("acTL", actl));
            }
            var control = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(control, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), (uint)f.W);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), (uint)f.H);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(12), (uint)f.X);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(16), (uint)f.Y);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 10);
            control[24] = (byte)f.Dispose;
            control[25] = (byte)f.Blend;
            chunks.Add(("fcTL", control));
            foreach (var idat in parts.Where(c => c.Type == "IDAT"))
            {
                if (i == 0)
                    chunks.Add(idat);
                else
                {
                    var fdat = new byte[4 + idat.Data.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(fdat, sequence++);
                    idat.Data.CopyTo(fdat, 4);
                    chunks.Add(("fdAT", fdat));
                }
            }
        }
        chunks.Add(("IEND", []));

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> word = stackalloc byte[4];
        foreach (var (type, data) in chunks)
        {
            var typeBytes = Encoding.ASCII.GetBytes(type);
            BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
            ms.Write(word);
            ms.Write(typeBytes);
            ms.Write(data);
            BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typeBytes, data));
            ms.Write(word);
        }
        return ms.ToArray();
    }

    private static List<(string Type, byte[] Data)> Split(byte[] png)
    {
        var chunks = new List<(string, byte[])>();
        for (var pos = 8; pos < png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            chunks.Add((Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos + 8, length).ToArray()));
            pos += 12 + length;
        }
        return chunks;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
