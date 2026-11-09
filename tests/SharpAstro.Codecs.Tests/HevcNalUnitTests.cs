using SharpAstro.Heif.Hevc;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Emulation prevention and the mapping between NAL unit payload offsets and RBSP positions. Entry point
/// offsets count the removed <c>03</c> bytes (7.4.7.1), so a substream is found by converting a payload
/// offset to an RBSP position; no x265 fixture happens to put an emulation prevention byte before a
/// substream start, so this is the test that sees a mapping that forgets them.
/// </summary>
public sealed class HevcNalUnitTests
{
    private static HevcNalUnit Nal(params byte[] payload) => HevcNalUnit.Parse([0x26, 0x01, .. payload]); // IDR_W_RADL

    [Fact]
    public void EmulationPrevention_IsRemoved_AndItsPlacesRecorded()
    {
        // 00 00 03 01 | 00 00 03 00 00 03 02
        var nal = Nal(0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x02);
        nal.Rbsp.ShouldBe(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x02 });
        nal.EmulationPrevention.ShouldBe(new[] { 2, 5, 7 }, "each removed byte preceded RBSP positions 2, 5 and 7");
    }

    [Fact]
    public void PayloadOffsetsAndRbspPositions_MapBothWays()
    {
        var nal = Nal(0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x02);
        // Payload bytes 0,1 | (03 at 2) | 3 | 4,5 | (03 at 6) | 7,8 | (03 at 9) | 10
        int[] payloadOf = [0, 1, 3, 4, 5, 7, 8, 10];
        for (var rbsp = 0; rbsp < payloadOf.Length; rbsp++)
        {
            nal.PayloadOffset(rbsp).ShouldBe(payloadOf[rbsp], $"RBSP byte {rbsp}");
            nal.RbspPosition(payloadOf[rbsp]).ShouldBe(rbsp, $"payload byte {payloadOf[rbsp]}");
        }
    }

    [Fact]
    public void WithoutEmulationPrevention_TheMappingIsTheIdentity()
    {
        var nal = Nal(1, 2, 3, 0, 0, 1, 4);
        nal.EmulationPrevention.ShouldBeEmpty();
        for (var i = 0; i < nal.Rbsp.Length; i++)
        {
            nal.PayloadOffset(i).ShouldBe(i);
            nal.RbspPosition(i).ShouldBe(i);
        }
    }
}
