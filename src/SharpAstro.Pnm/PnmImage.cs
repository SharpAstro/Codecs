namespace SharpAstro.Pnm;

/// <summary>
/// A decoded Netpbm image at its <b>native</b> sample values: nothing is scaled, so a
/// <c>maxval</c> 15 image holds 0..15 and <see cref="MaxValue"/> says so.
/// <para>
/// Samples are interleaved, row-major, <see cref="Channels"/> per pixel. A PBM bitmap is returned
/// as <c>MaxValue = 1</c> with <b>0 = black</b> (the file's 1 = black is undone by the reader), so
/// every image reads the same way: 0 is the darkest sample.
/// </para>
/// </summary>
public sealed class PnmImage
{
    /// <param name="header">The parsed header.</param>
    /// <param name="samples">Interleaved native samples, <c>Width * Height * Channels</c> of them.</param>
    public PnmImage(PnmHeader header, ushort[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        Header = header;
        Samples = samples;
    }

    /// <summary>The file's header.</summary>
    public PnmHeader Header { get; }

    /// <summary>Pixels across.</summary>
    public int Width => Header.Width;

    /// <summary>Pixels down.</summary>
    public int Height => Header.Height;

    /// <summary>Samples per pixel, 1..4.</summary>
    public int Channels => Header.Channels;

    /// <summary>The native maximum sample value, 1..65535.</summary>
    public int MaxValue => Header.MaxValue;

    /// <summary>What the channels mean.</summary>
    public PnmTupleType TupleType => Header.TupleType;

    /// <summary>Interleaved native samples, row-major.</summary>
    public ushort[] Samples { get; }
}
