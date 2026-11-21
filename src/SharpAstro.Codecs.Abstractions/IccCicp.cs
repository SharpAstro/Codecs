using System.Buffers.Binary;

namespace SharpAstro.Codecs.Abstractions;

/// <summary>
/// Reads the <c>cicp</c> tag (ICC.1:2022, v4.4) out of an ICC profile: H.273 code points for the colour the
/// profile describes. An HDR file may say what it is nowhere else: libheif's heif-enc, given a BT.2020 PQ
/// PNG from Lightroom Classic, writes a HEIC whose only colour statement is the PNG's "Rec. 2020 PQ" profile
/// (<c>cicp</c> 9/16/0/1), with neither an <c>nclx</c> box nor a VUI colour, and ImageMagick's TIFF of the
/// same PNG is the same. <c>SharpAstro.Color.Icc.IccProfiles.WithCicp</c> writes the tag.
/// </summary>
public static class IccCicp
{
    private const int HeaderSize = 128;
    private const uint CicpSignature = 0x63696370; // 'cicp'

    /// <summary>
    /// The profile's <c>cicp</c> code points, or false when it has none or the profile is too short or
    /// inconsistent to read one from. The matrix is what the tag says; for a profile describing RGB it is 0.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> profile, out ColorPrimaries primaries, out TransferFunction transfer,
        out MatrixCoefficients matrix, out bool fullRange)
    {
        primaries = default;
        transfer = default;
        matrix = default;
        fullRange = default;
        if (profile.Length < HeaderSize + 4)
            return false;
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.Slice(HeaderSize, 4));
        if (count > (uint)(profile.Length - HeaderSize - 4) / 12)
            return false;
        for (var i = 0; i < (int)count; i++)
        {
            var entry = profile.Slice(HeaderSize + 4 + 12 * i, 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(entry) != CicpSignature)
                continue;
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(4, 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry.Slice(8, 4));
            if (size < 12 || offset > (uint)profile.Length || size > (uint)profile.Length - offset)
                return false;
            var body = profile.Slice((int)offset, 12);
            if (BinaryPrimitives.ReadUInt32BigEndian(body) != CicpSignature)
                return false;
            primaries = (ColorPrimaries)body[8];
            transfer = (TransferFunction)body[9];
            matrix = (MatrixCoefficients)body[10];
            fullRange = body[11] != 0;
            return true;
        }
        return false;
    }
}
