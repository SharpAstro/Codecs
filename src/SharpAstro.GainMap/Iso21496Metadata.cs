using System.Buffers.Binary;
using SharpAstro.Jpeg;

namespace SharpAstro.GainMap;

/// <summary>
/// Reader for the binary gain-map metadata structure of ISO/TS 21496-1: the payload that rides in a
/// JPEG APP2 segment tagged with the ISO URN and in the HEIF <c>tmap</c> item's metadata. Clean-room
/// from the standard's text and its published summaries; libultrahdr and libheif are oracle binaries
/// only, never sources. The carrier (finding the payload) is the caller's job, this maps the bytes
/// onto <see cref="GainMapMetadata"/>.
/// <para>
/// Layout, all integers big-endian:
/// </para>
/// <code>
/// u16  minimum_version          the oldest reader that can interpret this (must be 0)
/// u16  writer_version           informational
/// u8   flags                    bit 7 is_multichannel, bit 6 use_base_colour_space,
///                               bit 3 common_denominator, bit 2 backward_direction; others reserved
/// -- common_denominator = 1 --
/// u32  denominator              shared by every fraction below, must not be 0
/// u32  baseline_hdr_headroom numerator,  u32 alternate_hdr_headroom numerator
/// per channel (1, or 3 when is_multichannel):
///   i32 gain_map_min, i32 gain_map_max, u32 gamma, i32 baseline_offset, i32 alternate_offset (numerators)
/// -- common_denominator = 0 --
/// (u32 numerator, u32 denominator) for baseline_hdr_headroom, then for alternate_hdr_headroom
/// per channel: (i32 numerator, u32 denominator) for gain_map_min, gain_map_max, then (u32, u32) for
///   gamma, then (i32, u32) for baseline_offset and alternate_offset
/// </code>
/// <para>
/// The headrooms, <c>gain_map_min</c> and <c>gain_map_max</c> are log2 values; gamma and the offsets
/// are linear. <see cref="GainMapMetadata"/> keeps linear ratios, so the log2 ones are exponentiated.
/// The baseline rendition is the one the carrier stores as the image and the alternate is the one the
/// map reconstructs. In the forward direction the baseline is the SDR-ish end
/// (<see cref="GainMapMetadata.HdrCapacityMin"/>); with <c>backward_direction</c> the baseline is the
/// HDR end and <see cref="GainMapMetadata.BaseRenditionIsHdr"/> is set, the same way Adobe's
/// <c>BaseRenditionIsHDR</c> keeps the capacity pair ordered SDR then HDR.
/// </para>
/// <para>
/// Refused with <see cref="InvalidDataException"/>: a truncated payload, a zero denominator, a
/// minimum version this reader does not know, and parameters that are not finite or not usable
/// (<see cref="GainMapMetadata.Validate"/>). Bytes after the structure are ignored, since a newer
/// writer version may append fields a reader with the same minimum version can skip.
/// </para>
/// </summary>
public static class Iso21496Metadata
{
    /// <summary>The only <c>minimum_version</c> this reader understands.</summary>
    public const int SupportedMinimumVersion = 0;

    /// <summary>Flag bit: three channels of parameters follow instead of one.</summary>
    public const byte FlagMultichannel = 0x80;

    /// <summary>Flag bit: the gain is applied in the base image's colour space.</summary>
    public const byte FlagUseBaseColourSpace = 0x40;

    /// <summary>Flag bit: every fraction shares one denominator.</summary>
    public const byte FlagCommonDenominator = 0x08;

    /// <summary>Flag bit: the baseline rendition is the HDR one.</summary>
    public const byte FlagBackwardDirection = 0x04;

    /// <summary>Parses a metadata payload.</summary>
    /// <exception cref="InvalidDataException">The payload is truncated, malformed or out of range.</exception>
    public static GainMapMetadata Parse(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);

        var minimumVersion = reader.U16();
        _ = reader.U16(); // writer_version: informational
        if (minimumVersion != SupportedMinimumVersion)
            throw new InvalidDataException($"ISO 21496-1 metadata needs a reader of version {minimumVersion}; this one reads {SupportedMinimumVersion}.");

        var flags = reader.U8();
        var multichannel = (flags & FlagMultichannel) != 0;
        var useBase = (flags & FlagUseBaseColourSpace) != 0;
        var common = (flags & FlagCommonDenominator) != 0;
        var backward = (flags & FlagBackwardDirection) != 0;

        uint sharedDenominator = 0;
        if (common)
        {
            sharedDenominator = reader.U32();
            if (sharedDenominator == 0)
                throw new InvalidDataException("ISO 21496-1 metadata has a zero common denominator.");
        }

        var baselineHeadroom = Unsigned(ref reader, common, sharedDenominator);
        var alternateHeadroom = Unsigned(ref reader, common, sharedDenominator);

        var count = multichannel ? 3 : 1;
        var channels = new GainMapChannel[count];
        for (var c = 0; c < count; c++)
        {
            var min = Signed(ref reader, common, sharedDenominator);
            var max = Signed(ref reader, common, sharedDenominator);
            var gamma = Unsigned(ref reader, common, sharedDenominator);
            var offsetBaseline = Signed(ref reader, common, sharedDenominator);
            var offsetAlternate = Signed(ref reader, common, sharedDenominator);
            channels[c] = new GainMapChannel(Exp2(min), Exp2(max), gamma, offsetBaseline, offsetAlternate);
        }

        var low = Exp2(backward ? alternateHeadroom : baselineHeadroom);
        var high = Exp2(backward ? baselineHeadroom : alternateHeadroom);
        var first = channels[0];
        var metadata = new GainMapMetadata
        {
            GainMapMin = first.GainMapMin,
            GainMapMax = first.GainMapMax,
            Gamma = first.Gamma,
            OffsetSdr = first.OffsetSdr,
            OffsetHdr = first.OffsetHdr,
            HdrCapacityMin = low,
            HdrCapacityMax = high,
            BaseRenditionIsHdr = backward,
            UseBaseColourSpace = useBase,
            Channels = multichannel ? channels : null,
        };

        try
        {
            metadata.Validate();
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidDataException("ISO 21496-1 metadata is out of range: " + e.Message, e);
        }

        return metadata;
    }

    private static double Unsigned(ref Reader reader, bool common, uint sharedDenominator)
    {
        var numerator = reader.U32();
        return Fraction(numerator, common ? sharedDenominator : reader.U32());
    }

    private static double Signed(ref Reader reader, bool common, uint sharedDenominator)
    {
        var numerator = reader.I32();
        return Fraction(numerator, common ? sharedDenominator : reader.U32());
    }

    private static double Fraction(long numerator, uint denominator)
    {
        if (denominator == 0)
            throw new InvalidDataException("ISO 21496-1 metadata has a fraction with a zero denominator.");
        var value = (double)numerator / denominator;
        return double.IsFinite(value)
            ? value
            : throw new InvalidDataException("ISO 21496-1 metadata has a non-finite parameter.");
    }

    private static double Exp2(double log2)
    {
        var value = Math.Pow(2, log2);
        return double.IsFinite(value)
            ? value
            : throw new InvalidDataException("ISO 21496-1 metadata has a log2 parameter that overflows a linear ratio.");
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (_data.Length - _position < count)
                throw new InvalidDataException($"ISO 21496-1 metadata is truncated: needs {count} more byte(s) at offset {_position} of {_data.Length}.");
            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    }
}
