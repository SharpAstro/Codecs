namespace SharpAstro.Webp;

internal sealed class Vp8Yuv
{
    public Vp8Yuv(int width, int height, byte[] y, byte[] u, byte[] v)
    {
        Width = width;
        Height = height;
        Y = y;
        U = u;
        V = v;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Y { get; }
    public byte[] U { get; }
    public byte[] V { get; }
}
