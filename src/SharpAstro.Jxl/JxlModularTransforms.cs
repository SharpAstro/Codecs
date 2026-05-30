namespace SharpAstro.Jxl;

/// <summary>
/// Modular transform channel-list bookkeeping (ISO/IEC 18181-1 §H.6, jxl-modular transform.rs).
/// Forward (parse-time) reshapes the channel list so the entropy decoder fills the right grids;
/// inverse (after decode, in reverse order) reconstructs the original channels. RCT and Palette
/// are implemented; Squeeze is not yet.
/// </summary>
internal static class JxlModularTransforms
{
    public static void ApplyForward(List<JxlModularChannel> channels, JxlTransform t, ref int nbMeta)
    {
        switch (t.Type)
        {
            case JxlTransformType.Rct:
                int endC = t.BeginC + 3;
                if (endC > channels.Count)
                    throw new InvalidDataException("JPEG XL RCT: channel range out of bounds.");
                int rw = channels[t.BeginC].Width, rh = channels[t.BeginC].Height;
                for (int i = t.BeginC + 1; i < endC; i++)
                    if (channels[i].Width != rw || channels[i].Height != rh)
                        throw new InvalidDataException("JPEG XL RCT: channels differ in size.");
                break; // RCT does not reshape the channel list

            case JxlTransformType.Palette:
                ForwardPalette(channels, t, ref nbMeta);
                break;

            case JxlTransformType.Squeeze:
                throw new NotSupportedException("JPEG XL Squeeze transform is not yet supported.");
        }
    }

    public static void ApplyInverse(List<JxlModularChannel> channels, JxlTransform t, int bitDepth)
    {
        switch (t.Type)
        {
            case JxlTransformType.Rct:
                InverseRct(channels, t);
                break;
            case JxlTransformType.Palette:
                InversePalette(channels, t, bitDepth);
                break;
            case JxlTransformType.Squeeze:
                throw new NotSupportedException("JPEG XL Squeeze transform is not yet supported.");
        }
    }

    private static void InverseRct(List<JxlModularChannel> channels, JxlTransform t)
    {
        int bc = t.BeginC;
        var grids = new[] { channels[bc].Data, channels[bc + 1].Data, channels[bc + 2].Data };
        JxlRct.Inverse(t.RctType, grids, beginC: 0);
        channels[bc].Data = grids[0];
        channels[bc + 1].Data = grids[1];
        channels[bc + 2].Data = grids[2];
    }

    private static void ForwardPalette(List<JxlModularChannel> channels, JxlTransform t, ref int nbMeta)
    {
        int endC = t.BeginC + t.NumC;
        if (endC > channels.Count)
            throw new InvalidDataException("JPEG XL Palette: channel range out of bounds.");
        if (t.BeginC < nbMeta)
            nbMeta += 2 - t.NumC;
        else
            nbMeta += 1;

        channels.RemoveRange(t.BeginC + 1, t.NumC - 1);
        // Palette meta-channel: width = nb_colours, height = num_c, unshiftable.
        channels.Insert(0, new JxlModularChannel(t.NbColours, t.NumC, -1, -1));
    }

    private static void InversePalette(List<JxlModularChannel> channels, JxlTransform t, int bitDepth)
    {
        JxlModularChannel palette = channels[0];
        channels.RemoveAt(0);

        int bc = t.BeginC;
        int numC = t.NumC;
        int nbColours = t.NbColours;
        JxlModularChannel index = channels[bc];
        int w = index.Width, h = index.Height;

        var outCh = new JxlModularChannel[numC];
        for (int c = 0; c < numC; c++)
            outCh[c] = new JxlModularChannel(w, h);

        int half = (1 << Math.Max(bitDepth - 3, 0));
        int scale = (1 << bitDepth) - 1;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int idx = index.Get(x, y);
                if (idx >= 0 && idx < nbColours)
                {
                    for (int c = 0; c < numC; c++)
                        outCh[c].Set(x, y, palette.Get(idx, c)); // palette[colour=idx][channel=c]
                }
                else if (idx >= nbColours)
                {
                    int i = idx - nbColours;
                    if (i < 64)
                        for (int c = 0; c < numC; c++)
                            outCh[c].Set(x, y, ((i >> (2 * c)) & 3) * scale / 4 + half);
                    else
                    {
                        int i2 = i - 64;
                        for (int c = 0; c < numC; c++)
                        {
                            outCh[c].Set(x, y, (i2 % 5) * scale / 4);
                            i2 /= 5;
                        }
                    }
                }
                else
                {
                    throw new NotSupportedException("JPEG XL delta-palette entries are not yet supported.");
                }
            }

        channels[bc] = outCh[0];
        for (int c = 1; c < numC; c++)
            channels.Insert(bc + c, outCh[c]);
    }
}
