using SharpAstro.Jxl;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// JPEG XL entropy coding (ISO/IEC 18181-1 §C.2) — validated by self round-trip, since the
/// entropy layer has no isolated Magick oracle (Magick only round-trips whole files). Our
/// encoder and decoder are faithful inverses of jxl-coding; encode→decode identity over a
/// spread of inputs is the regression guard. The decoder is re-validated end-to-end against
/// real libjxl bytes once Modular decode (Rung 4) lands.
///   Rung 3a: hybrid integer (token + trailing bits) config + value round-trip.
/// </summary>
public sealed class JxlEntropyTests
{
    [Fact]
    public void IntegerConfig_SerializesAndParsesBack()
    {
        foreach (int las in (int[])[5, 6, 7, 8, 15])
        {
            foreach (JxlIntegerConfig cfg in ConfigsFor(las))
            {
                var bw = new JxlBitWriter();
                cfg.Write(bw, las);
                var br = new JxlBitReader(bw.ToArray());
                JxlIntegerConfig back = JxlIntegerConfig.Parse(ref br, las);

                string label = $"las={las} se={cfg.SplitExponent} msb={cfg.MsbInToken} lsb={cfg.LsbInToken}";
                back.SplitExponent.ShouldBe(cfg.SplitExponent, label);
                back.MsbInToken.ShouldBe(cfg.MsbInToken, label);
                back.LsbInToken.ShouldBe(cfg.LsbInToken, label);
                back.Split.ShouldBe(cfg.Split, label);
            }
        }
    }

    [Fact]
    public void HybridUint_TokenAndTrailingBits_RoundTrip()
    {
        uint[] values = [0, 1, 2, 3, 7, 8, 15, 16, 31, 32, 63, 100, 255, 256, 1000, 65535, 65536, 1_000_000, 16_777_215];

        // The token itself rides the entropy coder (tested at later rungs); here we drive it
        // directly and only round-trip the config's trailing-bit expansion.
        foreach (int las in (int[])[5, 8, 15])
        {
            foreach (JxlIntegerConfig cfg in ConfigsFor(las))
            {
                foreach (uint value in values)
                {
                    uint token = cfg.EncodeToken(value, out uint restBits, out int restBitCount);

                    var bw = new JxlBitWriter();
                    bw.WriteBits(restBits, restBitCount);
                    var br = new JxlBitReader(bw.ToArray());
                    uint decoded = cfg.ReadUint(ref br, token);

                    decoded.ShouldBe(value, $"las={las} se={cfg.SplitExponent} msb={cfg.MsbInToken} lsb={cfg.LsbInToken} v={value}");
                    if (value < cfg.Split)
                        token.ShouldBe(value); // literal token range
                }
            }
        }
    }

    // A representative spread of valid (split_exponent, msb, lsb) triples for the alphabet size.
    private static IEnumerable<JxlIntegerConfig> ConfigsFor(int logAlphabetSize)
    {
        int[] splits = logAlphabetSize <= 8
            ? Enumerable.Range(0, logAlphabetSize + 1).ToArray()
            : [0, 1, 4, 8, 12, 15];
        foreach (int se in splits)
        {
            // When split_exponent == log_alphabet_size the msb/lsb fields aren't transmitted
            // (forced to 0), so only that triple is representable for that split.
            if (se == logAlphabetSize)
            {
                yield return JxlIntegerConfig.Create(se, 0, 0);
                continue;
            }
            for (int msb = 0; msb <= se; msb++)
                for (int lsb = 0; lsb + msb <= se; lsb++)
                    yield return JxlIntegerConfig.Create(se, msb, lsb);
        }
    }
}
