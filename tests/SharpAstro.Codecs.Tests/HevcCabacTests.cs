using SharpAstro.Heif.Hevc;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// The CABAC engine's shortcuts against the spec's step-by-step forms: <see cref="HevcCabac.Bypass(int)"/>
/// (one long division) against <see cref="HevcCabac.BypassBinByBin"/> (9.3.4.3.4 a bin at a time), with
/// context-coded decisions in between so the offset and range come from every state the engine can be in,
/// and the over-read limit at its exact boundary.
/// </summary>
public sealed class HevcCabacTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Bypass_InOneDivision_EqualsBinByBin(int seed)
    {
        var random = new Random(seed);
        var data = new byte[16384]; // 3000 steps of up to 32 bits each fit
        random.NextBytes(data);
        data[0] &= 0x7F; // the first nine bits must not be 510 or 511

        var fast = new HevcCabac(data, 0, data.Length);
        var slow = new HevcCabac(data, 0, data.Length);
        fast.Start(30);
        slow.Start(30);
        for (var step = 0; step < 3000; step++)
        {
            if (random.Next(3) == 0)
            {
                var context = random.Next(HevcContext.Count);
                fast.Decision(context).ShouldBe(slow.Decision(context), $"decision at step {step}");
            }
            else
            {
                var count = random.Next(0, 33);
                fast.Bypass(count).ShouldBe(slow.BypassBinByBin(count), $"{count} bypass bins at step {step}");
            }
        }
        fast.Terminate().ShouldBe(slow.Terminate());
    }

    [Fact]
    public void ReadingPastTheEnd_IsAllowedFor64BitsAndNoMore()
    {
        // Nine bits start the engine; then every bypass bin reads one bit. With one byte of data, the first
        // 64 bits past its end read as zero and the 65th is a slice that ended early.
        var cabac = new HevcCabac([0x00, 0x00], 0, 1);
        cabac.Start(30);                                  // 9 bits: 8 of data, 1 past the end
        cabac.Bypass(32);
        cabac.Bypass(31);                                 // 64 past the end
        Should.Throw<InvalidDataException>(() => cabac.Bypass(1));
    }
}
