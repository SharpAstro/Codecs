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
                StoreSubBlock(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
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
                StoreSubBlock(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Decode a BD8 + Rgb + NoFlexbits codestream produced by
    /// <see cref="JxrEncoder.EncodeBd8RgbNoFlexbits"/>. Returns interleaved
    /// <c>R, G, B</c> bytes in row-major order — <c>width × height × 3</c>.
    /// </summary>
    public static byte[] DecodeBd8RgbNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);

        if (img.ImageHeader.OutputClrFmt != JxrOutputColorFormat.Rgb ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd8 ||
            img.PlaneHeader.InternalClrFmt != JxrInternalColorFormat.Rgb ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.NoFlexbits)
        {
            throw new NotSupportedException(
                $"JxrDecoder.DecodeBd8RgbNoFlexbits expects Rgb/BD8/NoFlexbits; " +
                $"got {img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }

        width = img.Width;
        height = img.Height;
const int numComponents = 3;
        const JxrInternalColorFormat format = JxrInternalColorFormat.Rgb;

        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var mb = img.Macroblocks[mby * mbW + mbx];
            for (var comp = 0; comp < numComponents; comp++)
            {
                mbDc[mbx, mby, comp] = mb.Dc[comp];
                for (var p = 0; p < 16; p++)
                    mbDcLp[mbx, mby, comp, p] = mb.Lp[comp * 16 + p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blk, p] = mb.Hp[comp * 256 + blk * 16 + p];
            }
        }

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Decode(mbHp, mbHpMode, format);

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Decode(mbDc, predDc, format, mbDcMode: mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Decode(mbDcLp, predDcLp, mbDcMode, format);

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
            mbDcLp[mbx, mby, comp, 0] = mbDc[mbx, mby, comp];

        var pixels = new byte[width * height * 3];
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var p = 0; p < 16; p++) dcGrid[p] = mbDcLp[mbx, mby, comp, p];
            Transforms.ICT4x4(dcGrid);

            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var blkIdx = sbRow * 4 + sbCol;
                subBlock[0] = dcGrid[blkIdx];
                for (var p = 1; p < 16; p++)
                    subBlock[p] = mbHp[mbx, mby, comp, blkIdx, p];
                Transforms.ICT4x4(subBlock);
                StoreSubBlockRgb(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
            }
        }

        return pixels;
    }

    // Sub-block stores skip pixels past the declared image bounds — the
    // bottom and right edge MBs may cover the padded region beyond the
    // original image dimensions.

    private static void StoreSubBlock(byte[] pixels, int width, int height, int x0, int y0, ReadOnlySpan<int> src)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; var x = x0 + c;
            if (y >= height || x >= width) continue;
            var v = src[r * 4 + c] + JxrEncoder.Bd8Bias;
            if (v < 0) v = 0;
            else if (v > 255) v = 255;
            pixels[y * width + x] = (byte)v;
        }
    }

    private static void StoreSubBlockRgb(byte[] pixels, int width, int height, int x0, int y0, int comp, ReadOnlySpan<int> src)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; var x = x0 + c;
            if (y >= height || x >= width) continue;
            var v = src[r * 4 + c] + JxrEncoder.Bd8Bias;
            if (v < 0) v = 0;
            else if (v > 255) v = 255;
            pixels[(y * width + x) * 3 + comp] = (byte)v;
        }
    }

    /// <summary>
    /// Decode a BD16 + YOnly + NoFlexbits codestream produced by
    /// <see cref="JxrEncoder.EncodeBd16GrayscaleNoFlexbits"/>.
    /// </summary>
    public static ushort[] DecodeBd16GrayscaleNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);

        if (img.ImageHeader.OutputClrFmt != JxrOutputColorFormat.YOnly ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd16 ||
            img.PlaneHeader.InternalClrFmt != JxrInternalColorFormat.YOnly ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.NoFlexbits)
        {
            throw new NotSupportedException(
                $"JxrDecoder.DecodeBd16GrayscaleNoFlexbits expects YOnly/BD16/NoFlexbits; " +
                $"got {img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }

        width = img.Width;
        height = img.Height;
var (mbDc, mbDcLp, mbHp) = UnpackAndInversePredict(img, JxrInternalColorFormat.YOnly, 1);

        var pixels = new ushort[width * height];
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];
        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

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
                StoreSubBlock16(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
            }
        }
        return pixels;
    }

    /// <summary>
    /// Decode a BD16 + Rgb + NoFlexbits codestream produced by
    /// <see cref="JxrEncoder.EncodeBd16RgbNoFlexbits"/>. Returns interleaved
    /// 16-bit R, G, B samples.
    /// </summary>
    public static ushort[] DecodeBd16RgbNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);

        if (img.ImageHeader.OutputClrFmt != JxrOutputColorFormat.Rgb ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd16 ||
            img.PlaneHeader.InternalClrFmt != JxrInternalColorFormat.Rgb ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.NoFlexbits)
        {
            throw new NotSupportedException(
                $"JxrDecoder.DecodeBd16RgbNoFlexbits expects Rgb/BD16/NoFlexbits; " +
                $"got {img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }

        width = img.Width;
        height = img.Height;
const int numComponents = 3;
        var (mbDc, mbDcLp, mbHp) = UnpackAndInversePredict(img, JxrInternalColorFormat.Rgb, numComponents);

        var pixels = new ushort[width * height * 3];
        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];
        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var p = 0; p < 16; p++) dcGrid[p] = mbDcLp[mbx, mby, comp, p];
            Transforms.ICT4x4(dcGrid);

            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var blkIdx = sbRow * 4 + sbCol;
                subBlock[0] = dcGrid[blkIdx];
                for (var p = 1; p < 16; p++)
                    subBlock[p] = mbHp[mbx, mby, comp, blkIdx, p];
                Transforms.ICT4x4(subBlock);
                StoreSubBlock16Rgb(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
            }
        }
        return pixels;
    }

    /// <summary>
    /// Shared decoder pipeline up to the inverse FCT: unpack Macroblock[] into
    /// the 3D / 4D / 5D prediction buffers, run inverse HP → DC → LP, stitch
    /// super-DC back into <c>mbDcLp[..., 0]</c>. Returned tuple lets callers
    /// proceed with the per-MB inverse-FCT cascade.
    /// </summary>
    private static (int[,,], int[,,,], int[,,,,]) UnpackAndInversePredict(
        CodedImage img, JxrInternalColorFormat format, int numComponents)
    {
        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        var mbDc = new int[mbW, mbH, numComponents];
        var mbDcLp = new int[mbW, mbH, numComponents, 16];
        var mbHp = new int[mbW, mbH, numComponents, 16, 16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        {
            var mb = img.Macroblocks[mby * mbW + mbx];
            for (var comp = 0; comp < numComponents; comp++)
            {
                mbDc[mbx, mby, comp] = mb.Dc[comp];
                for (var p = 0; p < 16; p++)
                    mbDcLp[mbx, mby, comp, p] = mb.Lp[comp * 16 + p];
                for (var blk = 0; blk < 16; blk++)
                for (var p = 1; p < 16; p++)
                    mbHp[mbx, mby, comp, blk, p] = mb.Hp[comp * 256 + blk * 16 + p];
            }
        }

        var mbHpMode = new int[mbW, mbH];
        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
            mbHpMode[mbx, mby] = HpPrediction.CalcMode(mbDcLp, mbx, mby, format, numComponents);
        HpPrediction.Decode(mbHp, mbHpMode, format);

        var predDc = new int[mbW, mbH, numComponents];
        var mbDcMode = new int[mbW, mbH];
        DcPrediction.Decode(mbDc, predDc, format, mbDcMode: mbDcMode);

        var predDcLp = new int[mbW, mbH, numComponents, 16];
        LpPrediction.Decode(mbDcLp, predDcLp, mbDcMode, format);

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
            mbDcLp[mbx, mby, comp, 0] = mbDc[mbx, mby, comp];

        return (mbDc, mbDcLp, mbHp);
    }

    private static void StoreSubBlock16(ushort[] pixels, int width, int height, int x0, int y0, ReadOnlySpan<int> src)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; var x = x0 + c;
            if (y >= height || x >= width) continue;
            var v = src[r * 4 + c] + JxrEncoder.Bd16Bias;
            if (v < 0) v = 0;
            else if (v > 65535) v = 65535;
            pixels[y * width + x] = (ushort)v;
        }
    }

    private static void StoreSubBlock16Rgb(ushort[] pixels, int width, int height, int x0, int y0, int comp, ReadOnlySpan<int> src)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            var y = y0 + r; var x = x0 + c;
            if (y >= height || x >= width) continue;
            var v = src[r * 4 + c] + JxrEncoder.Bd16Bias;
            if (v < 0) v = 0;
            else if (v > 65535) v = 65535;
            pixels[(y * width + x) * 3 + comp] = (ushort)v;
        }
    }

    /// <summary>
    /// Decode a BD16F + YOnly + NoFlexbits codestream — half-float monochrome. The
    /// returned ushorts are IEEE binary16 bit patterns (same layout as
    /// <c>System.Half</c>).
    /// </summary>
    public static ushort[] DecodeBd16FGrayscaleNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);
        ValidateBd16F(img, JxrOutputColorFormat.YOnly, JxrInternalColorFormat.YOnly);

        width = img.Width;
        height = img.Height;
return DecodeBd16PipelineCore(img, numComponents: 1, JxrInternalColorFormat.YOnly);
    }

    /// <summary>
    /// Decode a BD16F + Rgb + NoFlexbits codestream — half-float RGB, the HDR-master
    /// deliverable. Returns interleaved <c>R, G, B</c> half-float bit patterns.
    /// </summary>
    public static ushort[] DecodeBd16FRgbNoFlexbits(
        ReadOnlySpan<byte> codestream, out int width, out int height)
    {
        var img = CodedImage.Decode(codestream);
        ValidateBd16F(img, JxrOutputColorFormat.Rgb, JxrInternalColorFormat.Rgb);

        width = img.Width;
        height = img.Height;
return DecodeBd16PipelineCore(img, numComponents: 3, JxrInternalColorFormat.Rgb);
    }

    private static void ValidateBd16F(CodedImage img,
        JxrOutputColorFormat expectedOut, JxrInternalColorFormat expectedInternal)
    {
        if (img.ImageHeader.OutputClrFmt != expectedOut ||
            img.ImageHeader.OutputBitDepth != JxrOutputBitDepth.Bd16F ||
            img.PlaneHeader.InternalClrFmt != expectedInternal ||
            img.PlaneHeader.BandsPresent != JxrBandsPresent.NoFlexbits)
        {
            throw new NotSupportedException(
                $"BD16F decode expects {expectedOut}/Bd16F/{expectedInternal}/NoFlexbits; got " +
                $"{img.ImageHeader.OutputClrFmt}/{img.ImageHeader.OutputBitDepth}/" +
                $"{img.PlaneHeader.InternalClrFmt}/{img.PlaneHeader.BandsPresent}");
        }
    }

    private static ushort[] DecodeBd16PipelineCore(CodedImage img, int numComponents, JxrInternalColorFormat format)
    {
        var (_, mbDcLp, mbHp) = UnpackAndInversePredict(img, format, numComponents);

        var width = img.Width;
        var height = img.Height;
        var mbW = img.WidthInMb;
        var mbH = img.HeightInMb;

        var pixels = numComponents == 1
            ? new ushort[width * height]
            : new ushort[width * height * numComponents];

        Span<int> subBlock = stackalloc int[16];
        Span<int> dcGrid = stackalloc int[16];

        for (var mby = 0; mby < mbH; mby++)
        for (var mbx = 0; mbx < mbW; mbx++)
        for (var comp = 0; comp < numComponents; comp++)
        {
            for (var p = 0; p < 16; p++) dcGrid[p] = mbDcLp[mbx, mby, comp, p];
            Transforms.ICT4x4(dcGrid);

            for (var sbRow = 0; sbRow < 4; sbRow++)
            for (var sbCol = 0; sbCol < 4; sbCol++)
            {
                var blkIdx = sbRow * 4 + sbCol;
                subBlock[0] = dcGrid[blkIdx];
                for (var p = 1; p < 16; p++)
                    subBlock[p] = mbHp[mbx, mby, comp, blkIdx, p];
                Transforms.ICT4x4(subBlock);

                if (numComponents == 1)
                    StoreSubBlock16(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, subBlock);
                else
                    StoreSubBlock16Rgb(pixels, width, height, mbx * 16 + sbCol * 4, mby * 16 + sbRow * 4, comp, subBlock);
            }
        }
        return pixels;
    }
}
