using System.Text;
using BenchmarkDotNet.Attributes;
using SharpAstro.Svg;

namespace SharpAstro.Codecs.Benchmarks;

/// <summary>
/// Reading an SVG document into a scene, whole, through the public API: time and bytes allocated a read.
/// </summary>
/// <remarks>
/// <para>A read is XML parsing, the style cascade and geometry, and it is meant to happen ONCE a document, never in a
/// frame. These numbers say what that once costs, and whether a change to the reader moved it. Three shapes of document,
/// because they stress different parts:</para>
/// <list type="bullet">
/// <item><b>Icon</b>: 294 bytes, three stroked paths. The fixed cost of a read (the XML reader's own buffers) is most of it.</item>
/// <item><b>Sprite</b>: 200 symbols and 200 uses with a style sheet. Elements, attributes, the cascade and <c>use</c>.</item>
/// <item><b>Large</b>: 5,000 paths of 20 curves each, 2 MB. Path data and geometry.</item>
/// </list>
/// <para>Run with <c>--filter '*SvgRead*' --job short --inProcess</c> (the last because BenchmarkDotNet cannot find its
/// project while pilot worktrees exist under the repo). Only compare cases from one run.</para>
/// </remarks>
[MemoryDiagnoser]
public class SvgReadBenchmarks
{
    private byte[] _icon = [], _sprite = [], _large = [];

    [GlobalSetup]
    public void Setup()
    {
        _icon = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<path d=\"M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z\"/><path d=\"M12 6v6l4 2\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>");

        var sprite = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"480\" height=\"240\">" +
            "<style>.o { fill: none; stroke: #223; stroke-width: 2; stroke-linecap: round } .f { fill: #c33 } symbol .hi { fill: #fc0 } #s7 path { stroke: #090 }</style><defs>");
        for (int i = 0; i < 200; i++)
        {
            sprite.Append($"<symbol id=\"s{i}\" viewBox=\"0 0 24 24\"><path class=\"o\" d=\"M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2z\"/>" +
                $"<rect class=\"f\" x=\"{i % 7 + 4}\" y=\"8\" width=\"6\" height=\"6\" rx=\"1\"/><circle class=\"hi\" cx=\"12\" cy=\"12\" r=\"2\"/></symbol>");
        }
        sprite.Append("</defs>");
        for (int i = 0; i < 200; i++)
        {
            sprite.Append($"<use href=\"#s{i}\" x=\"{i % 20 * 24}\" y=\"{i / 20 * 24}\" width=\"24\" height=\"24\"/>");
        }
        _sprite = Encoding.UTF8.GetBytes(sprite.Append("</svg>").ToString());

        var large = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1000\" height=\"1000\"><style>.a{fill:#c00}.b{fill:url(#g)}</style>" +
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#f00\"/><stop offset=\"1\" stop-color=\"#00f\"/></linearGradient></defs>");
        var random = new Random(1);
        for (int i = 0; i < 5000; i++)
        {
            large.Append("<path class=\"").Append(i % 2 == 0 ? 'a' : 'b').Append("\" d=\"M").Append(random.Next(1000)).Append(' ').Append(random.Next(1000));
            for (int k = 0; k < 20; k++)
            {
                large.Append(" c").Append(random.Next(-20, 20)).Append(' ').Append(random.Next(-20, 20)).Append(' ')
                    .Append(random.Next(-20, 20)).Append(' ').Append(random.Next(-20, 20)).Append(' ')
                    .Append(random.Next(-20, 20)).Append(' ').Append(random.Next(-20, 20));
            }
            large.Append("z\"/>");
        }
        _large = Encoding.UTF8.GetBytes(large.Append("</svg>").ToString());

        // A benchmark of something that reads nothing is worse than none: say what each document really holds.
        Check(_icon, 3);
        Check(_sprite, 600);
        Check(_large, 5000);
    }

    private static void Check(byte[] svg, int shapes)
    {
        SvgDocument document = SvgReader.Read(svg);
        if (document.Scene.Nodes.Length != shapes || document.Issues.Length != 0)
        {
            throw new InvalidOperationException($"Expected {shapes} shapes and no issues, read {document.Scene.Nodes.Length} and {document.Issues.Length}.");
        }
    }

    [Benchmark(Description = "icon, 294 bytes")]
    public int Icon() => SvgReader.Read(_icon).Scene.Nodes.Length;

    [Benchmark(Description = "sprite, 200 symbols and uses")]
    public int Sprite() => SvgReader.Read(_sprite).Scene.Nodes.Length;

    [Benchmark(Description = "large, 5,000 paths, 2 MB")]
    public int Large() => SvgReader.Read(_large).Scene.Nodes.Length;
}
