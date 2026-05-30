using System.Buffers.Binary;

namespace SharpAstro.Exr;

/// <summary>
/// Top-level façade: HDR float pixels ⟷ a complete <c>.exr</c> file, mirroring
/// <c>JxrImageCodec</c>. Mono is stored as a single <c>Y</c> channel, RGB as
/// <c>R</c>/<c>G</c>/<c>B</c>; values are written <b>verbatim</b> (no normalization —
/// OpenEXR is scene-linear HDR). Compression defaults to <see cref="ExrCompression.Zip"/>
/// (lossless, the practical default for float data); pass <see cref="ExrCompression.Piz"/>
/// or others as needed. All schemes here are lossless, so round-trips are bit-exact.
/// </summary>
public static class ExrImageCodec
{
    // ------------------------------------------------------------------ mono FLOAT

    /// <summary>Encode a <paramref name="width"/>×<paramref name="height"/> mono image as a single
    /// 32-bit-float <c>Y</c> channel. Values are written verbatim.</summary>
    public static byte[] EncodeMonoFloat(ReadOnlySpan<float> pixels, int width, int height, ExrCompression compression = ExrCompression.Zip)
    {
        var img = new ExrImage { Width = width, Height = height, Compression = compression };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Float }, FloatToBytes(pixels, width * height));
        return ExrFile.Write(img);
    }

    /// <summary>Decode an <c>.exr</c> written by <see cref="EncodeMonoFloat"/> (single FLOAT channel) into a float plane.</summary>
    public static (int width, int height, float[] pixels) DecodeMonoFloat(ReadOnlySpan<byte> exr)
    {
        var img = ExrFile.Read(exr);
        return (img.Width, img.Height, BytesToFloat(MonoChannel(img)));
    }

    // ------------------------------------------------------------------ mono HALF

    /// <summary>Encode a mono image as a single 16-bit half-float <c>Y</c> channel.</summary>
    public static byte[] EncodeMonoHalf(ReadOnlySpan<Half> pixels, int width, int height, ExrCompression compression = ExrCompression.Zip)
    {
        var img = new ExrImage { Width = width, Height = height, Compression = compression };
        img.AddChannel(new ExrChannel { Name = "Y", Type = ExrPixelType.Half }, HalfToBytes(pixels, width * height));
        return ExrFile.Write(img);
    }

    /// <summary>Decode an <c>.exr</c> written by <see cref="EncodeMonoHalf"/> into a half-float plane.</summary>
    public static (int width, int height, Half[] pixels) DecodeMonoHalf(ReadOnlySpan<byte> exr)
    {
        var img = ExrFile.Read(exr);
        return (img.Width, img.Height, BytesToHalf(MonoChannel(img)));
    }

    // ------------------------------------------------------------------ RGB HALF (interleaved)

    /// <summary>Encode interleaved <c>Half[width*height*3]</c> (RGBRGB…) as half-float R/G/B channels —
    /// the consumer's HDR RGB shape.</summary>
    public static byte[] EncodeRgbHalf(ReadOnlySpan<Half> rgb, int width, int height, ExrCompression compression = ExrCompression.Zip)
    {
        int n = width * height;
        if (rgb.Length < n * 3) throw new ArgumentException("Interleaved RGB half buffer must hold width*height*3 samples.", nameof(rgb));
        var (r, g, b) = (new byte[n * 2], new byte[n * 2], new byte[n * 2]);
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(i * 2), BitConverter.HalfToInt16Bits(rgb[i * 3]));
            BinaryPrimitives.WriteInt16LittleEndian(g.AsSpan(i * 2), BitConverter.HalfToInt16Bits(rgb[i * 3 + 1]));
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), BitConverter.HalfToInt16Bits(rgb[i * 3 + 2]));
        }
        var img = new ExrImage { Width = width, Height = height, Compression = compression };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Half }, r);
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Half }, g);
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Half }, b);
        return ExrFile.Write(img);
    }

    /// <summary>Decode an <c>.exr</c> written by <see cref="EncodeRgbHalf"/> into interleaved <c>Half[width*height*3]</c>.</summary>
    public static (int width, int height, Half[] rgb) DecodeRgbHalf(ReadOnlySpan<byte> exr)
    {
        var img = ExrFile.Read(exr);
        int n = img.Width * img.Height;
        var (r, g, b) = (img.GetData("R"), img.GetData("G"), img.GetData("B"));
        var rgb = new Half[n * 3];
        for (var i = 0; i < n; i++)
        {
            rgb[i * 3] = BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(i * 2)));
            rgb[i * 3 + 1] = BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(g.AsSpan(i * 2)));
            rgb[i * 3 + 2] = BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2)));
        }
        return (img.Width, img.Height, rgb);
    }

    // ------------------------------------------------------------------ RGB FLOAT (interleaved)

    /// <summary>Encode interleaved <c>float[width*height*3]</c> (RGBRGB…) as 32-bit-float R/G/B channels.</summary>
    public static byte[] EncodeRgbFloat(ReadOnlySpan<float> rgb, int width, int height, ExrCompression compression = ExrCompression.Zip)
    {
        int n = width * height;
        if (rgb.Length < n * 3) throw new ArgumentException("Interleaved RGB float buffer must hold width*height*3 samples.", nameof(rgb));
        var (r, g, b) = (new byte[n * 4], new byte[n * 4], new byte[n * 4]);
        for (var i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(r.AsSpan(i * 4), rgb[i * 3]);
            BinaryPrimitives.WriteSingleLittleEndian(g.AsSpan(i * 4), rgb[i * 3 + 1]);
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), rgb[i * 3 + 2]);
        }
        var img = new ExrImage { Width = width, Height = height, Compression = compression };
        img.AddChannel(new ExrChannel { Name = "R", Type = ExrPixelType.Float }, r);
        img.AddChannel(new ExrChannel { Name = "G", Type = ExrPixelType.Float }, g);
        img.AddChannel(new ExrChannel { Name = "B", Type = ExrPixelType.Float }, b);
        return ExrFile.Write(img);
    }

    /// <summary>Decode an <c>.exr</c> written by <see cref="EncodeRgbFloat"/> into interleaved <c>float[width*height*3]</c>.</summary>
    public static (int width, int height, float[] rgb) DecodeRgbFloat(ReadOnlySpan<byte> exr)
    {
        var img = ExrFile.Read(exr);
        int n = img.Width * img.Height;
        var (r, g, b) = (img.GetData("R"), img.GetData("G"), img.GetData("B"));
        var rgb = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            rgb[i * 3] = BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(i * 4));
            rgb[i * 3 + 1] = BinaryPrimitives.ReadSingleLittleEndian(g.AsSpan(i * 4));
            rgb[i * 3 + 2] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4));
        }
        return (img.Width, img.Height, rgb);
    }

    // ------------------------------------------------------------------ helpers

    // The single channel of a mono image — prefer "Y", else the sole channel.
    private static byte[] MonoChannel(ExrImage img)
    {
        if (img.HasChannel("Y")) return img.GetData("Y");
        if (img.Channels.Count == 1) return img.GetData(0);
        throw new InvalidDataException($"Expected a mono EXR (a 'Y' or single channel), found {img.Channels.Count} channels.");
    }

    private static byte[] FloatToBytes(ReadOnlySpan<float> src, int n)
    {
        var b = new byte[n * 4];
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), src[i]);
        return b;
    }

    private static float[] BytesToFloat(byte[] b)
    {
        var f = new float[b.Length / 4];
        for (var i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(i * 4));
        return f;
    }

    private static byte[] HalfToBytes(ReadOnlySpan<Half> src, int n)
    {
        var b = new byte[n * 2];
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), BitConverter.HalfToInt16Bits(src[i]));
        return b;
    }

    private static Half[] BytesToHalf(byte[] b)
    {
        var h = new Half[b.Length / 2];
        for (var i = 0; i < h.Length; i++) h[i] = BitConverter.Int16BitsToHalf(BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i * 2)));
        return h;
    }
}
