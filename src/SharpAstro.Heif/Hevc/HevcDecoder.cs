namespace SharpAstro.Heif.Hevc;

/// <summary>
/// Decodes one HEVC-coded HEIF image item: the parameter sets from its <c>hvcC</c>, the slices from its
/// data. Rung 2 decodes intra (IDR) pictures inside the envelope measured on the Canon files and refuses
/// everything else by name, before allocating a sample, with <see cref="NotSupportedException"/>; a stream
/// that breaks the syntax or semantics is <see cref="InvalidDataException"/>.
/// </summary>
internal static class HevcDecoder
{
    public static HevcPicture Decode(HevcDecoderConfiguration configuration, ReadOnlySpan<byte> itemData, HevcDecodeStatistics? statistics = null)
    {
        var (sps, pps) = ParameterSets(configuration);

        // Split (and so length-check) the whole item before any plane is allocated.
        var nals = HevcNalUnit.SplitLengthPrefixed(itemData, configuration.LengthSize);
        var decoder = new HevcPictureDecoder(sps, pps, statistics);
        var slices = 0;
        foreach (var bytes in nals)
        {
            var nal = HevcNalUnit.Parse(bytes);
            if (nal.LayerId != 0) continue; // another layer's data; the base layer is the picture
            if (nal.IsSlice)
            {
                decoder.DecodeSlice(nal);
                slices++;
            }
            else if (nal.Type is HevcNalUnit.VpsNut or HevcNalUnit.SpsNut or HevcNalUnit.PpsNut)
            {
                throw new NotSupportedException($"The item data carries a parameter set (NAL type {nal.Type}); only those in 'hvcC' are used.");
            }
            // SEI, access unit delimiters and the like carry nothing the samples depend on.
        }
        if (slices == 0) throw new InvalidDataException("The item data holds no slice.");
        return decoder.Finish();
    }

    /// <summary>
    /// The SPS and PPS of an item's <c>hvcC</c>, checked against each other, the configuration, the envelope
    /// and the size limit: everything <see cref="Decode"/> refuses before it reads a slice.
    /// </summary>
    public static (HevcSps Sps, HevcPps Pps) ParameterSets(HevcDecoderConfiguration configuration)
    {
        var spsNals = configuration.NalUnitsOfType(HevcNalUnit.SpsNut).ToList();
        var ppsNals = configuration.NalUnitsOfType(HevcNalUnit.PpsNut).ToList();
        if (spsNals.Count != 1 || ppsNals.Count != 1)
            throw new NotSupportedException($"'hvcC' carries {spsNals.Count} SPS and {ppsNals.Count} PPS; one of each is handled.");

        var sps = HevcSps.Parse(HevcNalUnit.Parse(spsNals[0]).Rbsp);
        var pps = HevcPps.Parse(HevcNalUnit.Parse(ppsNals[0]).Rbsp);
        if (pps.SpsId != sps.Id)
            throw new InvalidDataException($"The PPS refers to SPS {pps.SpsId}, but the SPS is {sps.Id}.");
        HevcSurvey.CheckAgreement(configuration, sps);
        CheckEnvelope(sps, pps);

        if ((long)sps.Width * sps.Height > HeifLimits.MaxImagePixels)
            throw new InvalidDataException($"The SPS declares {sps.Width}x{sps.Height}, more than {HeifLimits.MaxImagePixels} pixels.");
        return (sps, pps);
    }

    /// <summary>
    /// The rung 2 envelope: what the Canon R7 and R6 pictures use (roadmap/heif.md, "The files, measured").
    /// Each refusal names the feature and the rung that owns it.
    /// </summary>
    internal static void CheckEnvelope(HevcSps sps, HevcPps pps)
    {
        static void Refuse(string feature, int rung) =>
            throw new NotSupportedException($"{feature} is not decoded yet (rung {rung}).");

        if (sps.ChromaFormatIdc is not (1 or 2)) Refuse($"chroma_format_idc {sps.ChromaFormatIdc} (only 4:2:0 and 4:2:2)", 4);
        if (sps.SeparateColourPlanes) Refuse("Separate colour planes", 4);
        if (sps.BitDepthLuma > 12 || sps.BitDepthChroma > 12) Refuse($"A bit depth of {Math.Max(sps.BitDepthLuma, sps.BitDepthChroma)}", 4);
        // Nothing here can check a picture whose luma and chroma depths differ (ffmpeg has no such pixel format).
        if (sps.BitDepthLuma != sps.BitDepthChroma) Refuse($"Luma at {sps.BitDepthLuma} bits with chroma at {sps.BitDepthChroma}", 4);
        if (sps.PcmEnabled) Refuse("PCM", 4);
        var range = sps.RangeExtension;
        if (range.TransformSkipRotation) Refuse("Transform skip rotation", 4);
        if (range.TransformSkipContext) Refuse("The transform skip context", 4);
        if (range.ImplicitRdpcm) Refuse("Implicit RDPCM", 4);
        if (range.ExplicitRdpcm) Refuse("Explicit RDPCM", 4);
        if (range.ExtendedPrecisionProcessing) Refuse("Extended precision processing", 4);
        if (range.IntraSmoothingDisabled) Refuse("Disabled intra smoothing", 4);
        if (range.PersistentRiceAdaptation) Refuse("Persistent Rice adaptation", 4);
        if (range.CabacBypassAlignment) Refuse("CABAC bypass alignment", 4);
        if (sps.OtherExtensions) Refuse("An SPS multilayer, 3D or screen content extension", 4);

        if (pps.SignDataHiding) Refuse("Sign data hiding", 4);
        if (pps.TransformSkip) Refuse("Transform skip", 4);
        if (pps.TransquantBypass) Refuse("Transquant bypass", 4);
        if (pps.Tiles) Refuse("Tiles", 4);
        if (pps.EntropyCodingSync) Refuse("Wavefront parallel processing", 4);
        if (pps.CrossComponentPrediction) Refuse("Cross-component prediction", 4);
        if (pps.ChromaQpOffsetList) Refuse("CU chroma QP offset lists", 4);
        if (pps.OtherExtensions) Refuse("A PPS multilayer, 3D or screen content extension", 4);
    }
}
