namespace SharpAstro.Jpeg;

/// <summary>
/// One JPEG Huffman table in stb_image's accelerated layout: a 9-bit fast-lookup
/// array for short codes plus the canonical (maxcode/delta) ladder for the rest.
/// The build algorithm is ported 1:1 from stb_image's <c>stbi__build_huffman</c> /
/// <c>stbi__build_fast_ac</c> so the decode path stays byte-exact with the
/// StbImageSharp reference decoder.
/// </summary>
internal sealed class JpegHuffman
{
    public const int FastBits = 9;

    public readonly byte[] Fast = new byte[1 << FastBits];
    public readonly ushort[] Code = new ushort[256];
    public readonly byte[] Values = new byte[256];
    public readonly byte[] Size = new byte[257];
    public readonly uint[] MaxCode = new uint[18];
    public readonly int[] Delta = new int[17];

    /// <summary>Builds code tables from the 16 DHT length counts. Returns false on a malformed table.</summary>
    public bool Build(ReadOnlySpan<int> count)
    {
        int k = 0;
        for (var i = 0; i < 16; ++i)
        {
            for (var j = 0; j < count[i]; ++j)
            {
                Size[k++] = (byte)(i + 1);
                if (k >= 257)
                    return false; // bad size list
            }
        }

        Size[k] = 0;
        uint code = 0;
        k = 0;
        int jj;
        for (jj = 1; jj <= 16; ++jj)
        {
            Delta[jj] = (int)(k - code);
            if (Size[k] == jj)
            {
                while (Size[k] == jj)
                    Code[k++] = (ushort)code++;

                if (code - 1 >= 1u << jj)
                    return false; // bad code lengths
            }

            MaxCode[jj] = code << (16 - jj);
            code <<= 1;
        }

        MaxCode[jj] = 0xffffffff;
        Fast.AsSpan().Fill(255);
        for (var i = 0; i < k; ++i)
        {
            int s = Size[i];
            if (s <= FastBits)
            {
                var c = Code[i] << (FastBits - s);
                var m = 1 << (FastBits - s);
                for (var j = 0; j < m; ++j)
                    Fast[c + j] = (byte)i;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the combined run/magnitude fast-AC table: when an AC code plus its
    /// magnitude bits fit in the 9-bit window, the whole (run, value, length)
    /// triple decodes in a single lookup.
    /// </summary>
    public void BuildFastAc(short[] fastAc)
    {
        for (var i = 0; i < (1 << FastBits); ++i)
        {
            var fast = Fast[i];
            fastAc[i] = 0;
            if (fast < 255)
            {
                int rs = Values[fast];
                var run = (rs >> 4) & 15;
                var magbits = rs & 15;
                int len = Size[fast];
                if (magbits != 0 && len + magbits <= FastBits)
                {
                    var k = ((i << len) & ((1 << FastBits) - 1)) >> (FastBits - magbits);
                    var m = 1 << (magbits - 1);
                    if (k < m)
                        k += (int)((~0U << magbits) + 1);
                    if (k >= -128 && k <= 127)
                        fastAc[i] = (short)(k * 256 + run * 16 + len + magbits);
                }
            }
        }
    }
}
