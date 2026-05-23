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

    /// <summary>
    /// Decode a BD8 + YOnly + NoFlexbits codestream produced by
    /// <see cref="JxrEncoder.EncodeBd8GrayscaleNoFlexbits"/>. Lossless when the
    /// encoder ran at <c>DcQuant=LpQuant=HpQuant=1</c> and OverlapMode=0.
    /// </summary>
    public static byte[] DecodeBd8GrayscaleNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);

        if (img.ImageHeader.OutputClrFmt != JxrOutputColorFormat.YOnly ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd8 ||
            img.PlaneHeader.InternalClrFmt != JxrInternalColorFormat.YOnly ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.NoFlexbits)
        {
            throw new NotSupportedException(
                $"JxrDecoder.DecodeBd8GrayscaleNoFlexbits expects YOnly/BD8/NoFlexbits; " +
                $"got {img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }

        width = img.Width;
        height = img.Height;

        if ((width & 15) != 0 || (height & 15) != 0)
            throw new NotSupportedException("non-multiple-of-16 dimensions not yet supported");

        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        // Repopulate the 3D / 4D / 5D prediction buffers from the flat Macroblock[].
        var mbDc = new int[mbW, mbH, 1];
        var mbDcLp = new int[mbW, mbH, 1, 16];
        var mbHp = new int[mbW, mbH, 1, 16, 16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var mb = img.Macroblocks[mby * mbW + mbx];
            mbDc[mbx, mby, 0] = mb.Dc[0];
            for (var p = 0; p < 16; p++) mbDcLp[mbx, mby, 0, p] = mb.Lp[p];
            for (var blk = 0; blk < 16; blk++)
            for (var p = 1; p < 16; p++)
                mbHp[mbx, mby, 0, blk, p] = mb.Hp[blk * 16 + p];
        }

        // Inverse prediction cascade: HP first, then DC, then LP. HP CalcMode
        // reads the residual LP coefficients (which is what the bitstream gives
        // us right now), matching what the encoder saw when it computed mode.
        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, JxrInternalColorFormat.YOnly, numComponents: 1);
        HpPrediction.Decode(mbHp, mbHpMode, JxrInternalColorFormat.YOnly);

        var predDc = new int[mbW, mbH, 1];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Decode(mbDc, predDc, JxrInternalColorFormat.YOnly, mbDcMode: mbDcMode);

        var predDcLp = new int[mbW, mbH, 1, 16];
        LpPrediction.Decode(mbDcLp, predDcLp, mbDcMode, JxrInternalColorFormat.YOnly);

        // After inverse prediction, stitch the reconstructed super-DC back into
        // position 0 of mbDcLp so the inverse DC-grid FCT sees the right value.
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbDcLp[mbx, mby, 0, 0] = mbDc[mbx, mby, 0];

        var pixels = new byte[width * height];
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            for (var p = 0; p < 16; p++) dcGrid[p] = mbDcLp[mbx, mby, 0, p];
            Transforms.ICT4x4(dcGrid);

            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var blkIdx = sbRow * 4 + sbCol;
                subBlock[0] = dcGrid[blkIdx];
                for (var p = 1; p < 16; p++)
                    subBlock[p] = mbHp[mbx, mby, 0, blkIdx, p];
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
