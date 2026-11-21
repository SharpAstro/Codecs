namespace SharpAstro.Jxl;

/// <summary>
/// 8-bit RGB from a VarDCT JPEG XL file, for the encoder's round-trip tests: the general
/// <see cref="JxlVarDctDecoder"/> (which applies every filter the frame asks for, as libjxl does), with samples
/// of another bit depth rescaled to 0..255.
/// </summary>
internal static class JxlVarDctFrame
{
    public static int[][] DecodeToRgb24(byte[] containerOrCodestream)
    {
        JxlVarDctResult r = JxlVarDctDecoder.Decode(containerOrCodestream);
        if (r.BitsPerSample == 8)
            return r.Channels;
        int max = (1 << r.BitsPerSample) - 1;
        var rgb = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            int[] src = r.Channels[c];
            var dst = new int[src.Length];
            for (int i = 0; i < src.Length; i++)
                dst[i] = (int)((src[i] * 255L * 2 + max) / (2L * max));
            rgb[c] = dst;
        }
        return rgb;
    }
}
