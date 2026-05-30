namespace SharpAstro.Exr;

/// <summary>
/// OpenEXR's shared pre/post byte transform used by both the ZIP and RLE codecs
/// (ImfZip.cpp / ImfRle.cpp). Before compressing, the block bytes are (1) reordered
/// by de-interleaving into two halves and (2) delta-predicted; decompression undoes
/// the predictor then the reorder. The transform is what makes the subsequent
/// entropy/zlib stage compress float imagery well.
/// </summary>
internal static class ExrByteTransform
{
    /// <summary>Reorder (de-interleave into two halves) then forward delta-predict. Returns a new buffer.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> raw)
    {
        int n = raw.Length;
        var t = new byte[n];

        // Reorder: alternately fill the front half and the back half.
        int half = (n + 1) / 2;
        int i1 = 0, i2 = half, s = 0;
        while (true)
        {
            if (s < n) t[i1++] = raw[s++]; else break;
            if (s < n) t[i2++] = raw[s++]; else break;
        }

        // Predictor: store each byte as a delta from the previous *original* byte (+384, mod 256).
        if (n > 1)
        {
            int p = t[0];
            for (var k = 1; k < n; k++)
            {
                int d = t[k] - p + (128 + 256);
                p = t[k];
                t[k] = (byte)d;
            }
        }
        return t;
    }

    /// <summary>Undo the forward delta-predictor, then undo the reorder. Returns a new buffer.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> transformed)
    {
        int n = transformed.Length;
        var t = transformed.ToArray();

        // Undo predictor: each byte = previous reconstructed byte + delta - 128 (mod 256).
        for (var k = 1; k < n; k++)
        {
            int d = t[k - 1] + t[k] - 128;
            t[k] = (byte)d;
        }

        // Undo reorder: interleave the two halves back together.
        var raw = new byte[n];
        int half = (n + 1) / 2;
        int i1 = 0, i2 = half, s = 0;
        while (true)
        {
            if (s < n) raw[s++] = t[i1++]; else break;
            if (s < n) raw[s++] = t[i2++]; else break;
        }
        return raw;
    }
}
