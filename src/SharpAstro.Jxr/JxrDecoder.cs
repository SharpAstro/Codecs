namespace SharpAstro.Jxr;

/// <summary>
/// Pixel-level JXR decoder facade — inverse of <see cref="JxrEncoder"/>.
/// Unpacks a JXR codestream back into raw sample data via the
/// <see cref="CodedImage"/> reader plus the inverse transform / prediction
/// pipeline.
/// </summary>
public static class JxrDecoder
{
    /// <summary>
    /// Decode a BD8 + YOnly + DcOnly codestream produced by
    /// <see cref="JxrEncoder.EncodeBd8GrayscaleDcOnly"/>. Returns the
    /// row-major pixel buffer; output dimensions are reported via
    /// <paramref name="width"/> / <paramref name="height"/> out parameters.
    /// </summary>
    public static byte[] DecodeBd8GrayscaleDcOnly(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);

        if (img.ImageHeader.OutputClrFmt != JxrOutputColorFormat.YOnly ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd8 ||
            img.PlaneHeader.InternalClrFmt != JxrInternalColorFormat.YOnly ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.DcOnly)
        {
            throw new NotSupportedException(
                $"JxrDecoder.DecodeBd8GrayscaleDcOnly expects YOnly/BD8/DcOnly; " +
                $"got {img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }

        width = img.Width;
        height = img.Height;

        if ((width & 15) != 0 || (height & 15) != 0)
            throw new NotSupportedException("non-multiple-of-16 dimensions not yet supported");

        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        // Inverse DC prediction first — the codestream carried residuals.
        var mbDc = new int[mbW, mbH, 1];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbDc[mbx, mby, 0] = img.Macroblocks[mby * mbW + mbx].Dc[0];

        var predDc = new int[mbW, mbH, 1];
        DcPrediction.Decode(mbDc, predDc, JxrInternalColorFormat.YOnly);

        var pixels = new byte[width * height];
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            // DcOnly: only super-DC was kept. Reconstruct DC grid by inverse-
            // transforming [super-DC, 0, 0, ..., 0] — restores the per-sub-block
            // DC values as a uniform spread.
            dcGrid.Clear();
            dcGrid[0] = mbDc[mbx, mby, 0];
            Transforms.ICT4x4(dcGrid);

            // For each sub-block, plant its DC value (positions 1..15 stay zero
            // since we dropped HP) and invert the FCT.
            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                subBlock.Clear();
                subBlock[0] = dcGrid[sbRow * 4 + sbCol];
                Transforms.ICT4x4(subBlock);
                StoreSubBlock(pixels, width, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
            }
        }

        return pixels;
    }

    private static void StoreSubBlock(byte[] pixels, int width, int x0, int y0, ReadOnlySpan<int> src)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var v = src[r * 4 + c] + JxrEncoder.Bd8Bias;
            // Clamp on output — quantisation or band-dropping can push samples outside [0, 255].
            if (v < 0) v = 0;
            else if (v > 255) v = 255;
            pixels[(y0 + r) * width + (x0 + c)] = (byte)v;
        }
    }
}
