using System.Buffers.Binary;
using SharpAstro.GainMap;
using SharpAstro.Jpeg;
using Shouldly;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// ISO/TS 21496-1 binary metadata reader. The writer here is test-only (writing the structure is not a
/// shipped feature), and a round trip through it cannot see a mistake the writer shares with the reader,
/// so the layout is also pinned by hand-written byte vectors for both fraction encodings.
/// </summary>
public sealed class Iso21496MetadataTests
{
    private const uint Den = 1_000_000;

    /// <summary>Test-side writer: the inverse of <see cref="Iso21496Metadata.Parse"/>.</summary>
    private static byte[] Write(GainMapMetadata metadata, bool commonDenominator, ushort minimumVersion = 0)
    {
        var bytes = new List<byte>();
        void U8(byte v) => bytes.Add(v);
        void U16(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(s, v); bytes.AddRange(s.ToArray()); }
        void U32(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(s, v); bytes.AddRange(s.ToArray()); }
        void I32(int v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(s, v); bytes.AddRange(s.ToArray()); }

        var multichannel = metadata.Channels is { Count: 3 };
        byte flags = 0;
        if (multichannel) flags |= Iso21496Metadata.FlagMultichannel;
        if (metadata.UseBaseColourSpace) flags |= Iso21496Metadata.FlagUseBaseColourSpace;
        if (commonDenominator) flags |= Iso21496Metadata.FlagCommonDenominator;
        if (metadata.BaseRenditionIsHdr) flags |= Iso21496Metadata.FlagBackwardDirection;

        U16(minimumVersion);
        U16(0);
        U8(flags);
        if (commonDenominator) U32(Den);

        void Unsigned(double v) { U32((uint)Math.Round(v * Den)); if (!commonDenominator) U32(Den); }
        void Signed(double v) { I32((int)Math.Round(v * Den)); if (!commonDenominator) U32(Den); }

        var low = Math.Log2(metadata.HdrCapacityMin);
        var high = Math.Log2(metadata.HdrCapacityMax);
        Unsigned(metadata.BaseRenditionIsHdr ? high : low);
        Unsigned(metadata.BaseRenditionIsHdr ? low : high);
        for (var c = 0; c < (multichannel ? 3 : 1); c++)
        {
            var ch = metadata.GetChannel(c);
            Signed(Math.Log2(ch.GainMapMin));
            Signed(Math.Log2(ch.GainMapMax));
            Unsigned(ch.Gamma);
            Signed(ch.OffsetSdr);
            Signed(ch.OffsetHdr);
        }

        return [.. bytes];
    }

    private static GainMapMetadata Scalar() => new()
    {
        GainMapMin = 1,
        GainMapMax = 4,
        Gamma = 1.25,
        OffsetSdr = 1.0 / 64,
        OffsetHdr = 1.0 / 32,
        HdrCapacityMin = 1,
        HdrCapacityMax = 8,
    };

    private static GainMapMetadata ThreeChannel() => new()
    {
        GainMapMin = 0.5,
        GainMapMax = 2,
        Gamma = 1,
        OffsetSdr = 0.015625,
        OffsetHdr = 0.015625,
        HdrCapacityMin = 1,
        HdrCapacityMax = 16,
        UseBaseColourSpace = false,
        Channels =
        [
            new GainMapChannel(0.5, 2, 1, 0.015625, 0.015625),
            new GainMapChannel(0.25, 4, 1.5, 0.03125, 0.0625),
            new GainMapChannel(1, 8, 0.8, 0, 0.125),
        ],
    };

    private static void ShouldMatch(GainMapMetadata actual, GainMapMetadata expected)
    {
        actual.ChannelCount.ShouldBe(expected.ChannelCount);
        actual.UseBaseColourSpace.ShouldBe(expected.UseBaseColourSpace);
        actual.BaseRenditionIsHdr.ShouldBe(expected.BaseRenditionIsHdr);
        actual.HdrCapacityMin.ShouldBe(expected.HdrCapacityMin, 1e-5);
        actual.HdrCapacityMax.ShouldBe(expected.HdrCapacityMax, 1e-5);
        for (var c = 0; c < expected.ChannelCount; c++)
        {
            var a = actual.GetChannel(c);
            var e = expected.GetChannel(c);
            a.GainMapMin.ShouldBe(e.GainMapMin, 1e-5);
            a.GainMapMax.ShouldBe(e.GainMapMax, 1e-5);
            a.Gamma.ShouldBe(e.Gamma, 1e-5);
            a.OffsetSdr.ShouldBe(e.OffsetSdr, 1e-5);
            a.OffsetHdr.ShouldBe(e.OffsetHdr, 1e-5);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_OneChannel(bool commonDenominator)
        => ShouldMatch(Iso21496Metadata.Parse(Write(Scalar(), commonDenominator)), Scalar());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_ThreeChannels(bool commonDenominator)
    {
        var parsed = Iso21496Metadata.Parse(Write(ThreeChannel(), commonDenominator));
        parsed.Channels.ShouldNotBeNull().Count.ShouldBe(3);
        ShouldMatch(parsed, ThreeChannel());
        // The scalar properties mirror channel 0.
        parsed.GainMapMax.ShouldBe(2, 1e-5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTrip_BackwardDirection(bool commonDenominator)
    {
        var metadata = Scalar() with { BaseRenditionIsHdr = true };
        var parsed = Iso21496Metadata.Parse(Write(metadata, commonDenominator));
        ShouldMatch(parsed, metadata);
        parsed.BaseRenditionIsHdr.ShouldBeTrue();
    }

    [Fact]
    public void BackwardDirection_PutsTheBaselineHeadroomAtTheHdrEnd()
    {
        // baseline headroom log2 = 3 (8x), alternate = 0 (1x), backward: base is HDR.
        byte[] payload =
        [
            0, 0, 0, 0,                // versions
            0x40 | 0x08 | 0x04,        // base colour space, common denominator, backward
            0, 0, 0, 1,                // denominator 1
            0, 0, 0, 3,                // baseline headroom 3
            0, 0, 0, 0,                // alternate headroom 0
            0, 0, 0, 0,                // min 0 (1x)
            0, 0, 0, 1,                // max 1 (2x)
            0, 0, 0, 1,                // gamma 1
            0, 0, 0, 0,                // baseline offset 0
            0, 0, 0, 0,                // alternate offset 0
        ];
        var parsed = Iso21496Metadata.Parse(payload);
        parsed.BaseRenditionIsHdr.ShouldBeTrue();
        parsed.HdrCapacityMin.ShouldBe(1);
        parsed.HdrCapacityMax.ShouldBe(8);
        parsed.BaselineHdrHeadroom.ShouldBe(1);
        parsed.AlternateHdrHeadroom.ShouldBe(8);
    }

    [Fact]
    public void HandVector_CommonDenominator_OneChannel()
    {
        // Written field by field so each value reads as itself, not as a byte soup.
        var payload = Concat(
            [0, 0, 0, 2, 0x40 | 0x08],
            Be(64u),
            Be(0u), Be(192u),          // headrooms 0 and 192/64 = 3
            Be(0), Be(128),            // min 0 -> 1x, max 128/64 = 2 -> 4x
            Be(80u),                   // gamma 80/64 = 1.25
            Be(1), Be(2));             // offsets 1/64 and 2/64
        var parsed = Iso21496Metadata.Parse(payload);
        parsed.ChannelCount.ShouldBe(1);
        parsed.Channels.ShouldBeNull();
        parsed.UseBaseColourSpace.ShouldBeTrue();
        parsed.BaseRenditionIsHdr.ShouldBeFalse();
        parsed.HdrCapacityMin.ShouldBe(1);
        parsed.HdrCapacityMax.ShouldBe(8);
        parsed.GainMapMin.ShouldBe(1);
        parsed.GainMapMax.ShouldBe(4);
        parsed.Gamma.ShouldBe(1.25);
        parsed.OffsetSdr.ShouldBe(1.0 / 64);
        parsed.OffsetHdr.ShouldBe(2.0 / 64);
    }

    [Fact]
    public void HandVector_PerFractionDenominators_ThreeChannels()
    {
        var payload = Concat(
            [0, 0, 0, 0, 0x80],                  // multichannel; base colour space off; no common denominator
            Be(0u), Be(1u),                      // baseline headroom 0/1
            Be(2u), Be(1u),                      // alternate headroom 2/1 -> 4x
            // channel 0..2: min, max, gamma, baseline offset, alternate offset, each as (numerator, denominator)
            Frac(0), Frac(1), Frac(1u), Frac(0), Frac(0),
            Frac(-1), Frac(2), Frac(3u, 2), Frac(1, 8), Frac(1, 16),
            Frac(0), Frac(3), Frac(1u, 2), Frac(0), Frac(0));
        var parsed = Iso21496Metadata.Parse(payload);
        parsed.ChannelCount.ShouldBe(3);
        parsed.UseBaseColourSpace.ShouldBeFalse();
        parsed.HdrCapacityMax.ShouldBe(4);
        parsed.GetChannel(0).GainMapMax.ShouldBe(2);
        var g = parsed.GetChannel(1);
        g.GainMapMin.ShouldBe(0.5);
        g.GainMapMax.ShouldBe(4);
        g.Gamma.ShouldBe(1.5);
        g.OffsetSdr.ShouldBe(0.125);
        g.OffsetHdr.ShouldBe(0.0625);
        var b = parsed.GetChannel(2);
        b.GainMapMax.ShouldBe(8);
        b.Gamma.ShouldBe(0.5);
    }

    [Fact]
    public void BytesAfterTheStructure_AreIgnored()
    {
        var payload = Write(Scalar(), commonDenominator: true).Concat(new byte[] { 1, 2, 3 }).ToArray();
        ShouldMatch(Iso21496Metadata.Parse(payload), Scalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryTruncation_IsRefused(bool commonDenominator)
    {
        foreach (var metadata in new[] { Scalar(), ThreeChannel() })
        {
            var full = Write(metadata, commonDenominator);
            for (var length = 0; length < full.Length; length++)
                Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(full.AsSpan(0, length)), $"length {length}");
            Should.NotThrow(() => Iso21496Metadata.Parse(full));
        }
    }

    [Fact]
    public void ZeroCommonDenominator_IsRefused()
    {
        var payload = Write(Scalar(), commonDenominator: true);
        payload[5] = payload[6] = payload[7] = payload[8] = 0;
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload)).Message.ShouldContain("denominator");
    }

    [Theory]
    [InlineData(0)]  // baseline headroom
    [InlineData(1)]  // alternate headroom
    [InlineData(2)]  // gain_map_min
    [InlineData(3)]  // gain_map_max
    [InlineData(4)]  // gamma
    [InlineData(5)]  // baseline offset
    [InlineData(6)]  // alternate offset
    public void ZeroDenominatorOnAnyFraction_IsRefused(int field)
    {
        var payload = Write(Scalar(), commonDenominator: false);
        // After the 5-byte header every fraction is 8 bytes: numerator, then denominator.
        var denominatorAt = 5 + field * 8 + 4;
        payload[denominatorAt] = payload[denominatorAt + 1] = payload[denominatorAt + 2] = payload[denominatorAt + 3] = 0;
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload)).Message.ShouldContain("denominator");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void UnknownMinimumVersion_IsRefused(int version)
    {
        var payload = Write(Scalar(), commonDenominator: true, minimumVersion: (ushort)version);
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload)).Message.ShouldContain("version");
    }

    [Fact]
    public void ANewerWriterVersion_WithMinimumVersionZero_IsAccepted()
    {
        var payload = Write(Scalar(), commonDenominator: true);
        payload[3] = 7; // writer_version low byte
        ShouldMatch(Iso21496Metadata.Parse(payload), Scalar());
    }

    [Fact]
    public void ALog2ThatOverflowsALinearRatio_IsRefusedAsNonFinite()
    {
        // gain_map_max = 2^31 - 1 over denominator 1: 2^(2^31) is infinity.
        var payload = Concat(
            [0, 0, 0, 0, 0x40 | 0x08],
            Be(1u),
            Be(0u), Be(1u),
            Be(0), Be(int.MaxValue),
            Be(1u), Be(0), Be(0));
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload)).Message.ShouldContain("overflow");
    }

    [Fact]
    public void NegativeOffset_IsRefusedAsOutOfRange()
    {
        var payload = Concat(
            [0, 0, 0, 0, 0x40 | 0x08],
            Be(1u),
            Be(0u), Be(1u),
            Be(0), Be(1),
            Be(1u), Be(-1), Be(0));
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload)).Message.ShouldContain("out of range");
    }

    [Fact]
    public void ZeroGamma_IsRefusedAsOutOfRange()
    {
        var payload = Concat(
            [0, 0, 0, 0, 0x40 | 0x08],
            Be(1u),
            Be(0u), Be(1u),
            Be(0), Be(1),
            Be(0u), Be(0), Be(0));
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload));
    }

    [Fact]
    public void MinAboveMax_IsRefusedAsOutOfRange()
    {
        var payload = Concat(
            [0, 0, 0, 0, 0x40 | 0x08],
            Be(1u),
            Be(0u), Be(1u),
            Be(2), Be(1),
            Be(1u), Be(0), Be(0));
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload));
    }

    [Fact]
    public void EqualHeadrooms_AreRefused()
    {
        var payload = Concat(
            [0, 0, 0, 0, 0x40 | 0x08],
            Be(1u),
            Be(1u), Be(1u),
            Be(0), Be(1),
            Be(1u), Be(0), Be(0));
        Should.Throw<InvalidDataException>(() => Iso21496Metadata.Parse(payload));
    }

    [Fact]
    public void AParsedPayload_ReconstructsLikeTheSameMetadataBuiltByHand()
    {
        var parsed = Iso21496Metadata.Parse(Write(ThreeChannel(), commonDenominator: false));
        var reference = ThreeChannel();
        for (var c = 0; c < 3; c++)
        {
            parsed.GetChannel(c).Gamma.ShouldBe(reference.GetChannel(c).Gamma, 1e-6);
            parsed.Weight(8).ShouldBe(reference.Weight(8), 1e-6);
        }
    }

    private static byte[] Be(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); return b; }

    private static byte[] Be(int value) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); return b; }

    private static byte[] Frac(int numerator, uint denominator = 1) => [.. Be(numerator), .. Be(denominator)];

    private static byte[] Frac(uint numerator, uint denominator = 1) => [.. Be(numerator), .. Be(denominator)];

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
