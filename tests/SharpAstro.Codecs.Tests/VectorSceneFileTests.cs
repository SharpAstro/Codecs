using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using SharpAstro.Svg;
using SharpAstro.VectorGraphics;
using Shouldly;
using static SharpAstro.Codecs.Tests.SvgTestSupport;

namespace SharpAstro.Codecs.Tests;

/// <summary>The binary form of a scene: that it brings back exactly what was written, and that it does not trust its bytes.</summary>
public sealed class VectorSceneFileTests
{
    /// <summary>One of everything a scene can hold: both gradients, strokes with dashes, groups in groups, ids, a shared path.</summary>
    private const string Everything =
        "<defs><linearGradient id=\"l\" x2=\"0.25\" spreadMethod=\"reflect\" gradientTransform=\"rotate(30)\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"0.5\" stop-color=\"#fc0\" stop-opacity=\"0.5\"/><stop offset=\"1\" stop-color=\"#f00\"/></linearGradient>" +
        "<radialGradient id=\"r\" fx=\"0.3\" fy=\"0.2\" fr=\"0.05\" spreadMethod=\"repeat\" gradientUnits=\"userSpaceOnUse\" cx=\"40\" cy=\"40\" r=\"30\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
        "<path id=\"shared\" d=\"M0 0 L10 0 Q 15 5 10 10 C 5 15 0 15 0 10 Z M2 2 h3\"/></defs>" +
        "<rect id=\"plain\" x=\"1\" y=\"2\" width=\"30\" height=\"20\" rx=\"4\" fill=\"url(#l)\" fill-rule=\"evenodd\" fill-opacity=\"0.75\"/>" +
        "<circle cx=\"50\" cy=\"50\" r=\"20\" fill=\"url(#r)\" stroke=\"#123456\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"bevel\" stroke-miterlimit=\"7\" stroke-dasharray=\"4 2 1\" stroke-dashoffset=\"1.5\" stroke-opacity=\"0.5\"/>" +
        "<g id=\"outer\" opacity=\"0.5\" transform=\"translate(10 20) rotate(15)\"><use href=\"#shared\" fill=\"rgba(10,20,30,0.4)\"/>" +
        "<g id=\"inner\" opacity=\"0.25\"><use href=\"#shared\" x=\"20\" stroke=\"url(#l)\" stroke-linecap=\"square\"/><line x2=\"5\" y2=\"5\" stroke=\"red\"/></g></g>" +
        "<use href=\"#shared\" x=\"60\" y=\"5\" opacity=\"0.5\" stroke=\"#0f0\"/>";

    /// <summary>Everything about a scene, as text, so that two scenes can be compared whole.</summary>
    private static string Dump(VectorScene scene)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"scene {scene.Width:R} x {scene.Height:R}\n"));
        Nodes(scene.Nodes, 1);
        return text.ToString();

        void Nodes(ImmutableArray<VectorNode> nodes, int indent)
        {
            foreach (VectorNode node in nodes)
            {
                text.Append(' ', indent * 2);
                if (node is VectorGroup group)
                {
                    text.Append(CultureInfo.InvariantCulture, $"group id={group.Id ?? "(none)"} opacity={group.Opacity:R}\n");
                    Nodes(group.Children, indent + 1);
                    continue;
                }
                var shape = (VectorShape)node;
                text.Append(CultureInfo.InvariantCulture, $"shape id={shape.Id ?? "(none)"} transform={shape.Transform} path=[{Describe(shape.Path)}]");
                if (shape.Fill is { } fill) text.Append(CultureInfo.InvariantCulture, $" fill({Paint(fill.Paint)} {fill.Opacity:R} {fill.Rule})");
                if (shape.Stroke is { } s)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $" stroke({Paint(s.Paint)} {s.Opacity:R} w={s.Width:R} {s.Cap} {s.Join} m={s.MiterLimit:R} dashes=[{string.Join(' ', s.Dashes.Select(d => d.ToString("R", CultureInfo.InvariantCulture)))}] off={s.DashOffset:R})");
                }
                text.Append('\n');
            }
        }

        static string Paint(VectorPaint paint) => paint switch
        {
            SolidPaint solid => $"solid {solid.Color}",
            LinearGradientPaint l => string.Create(CultureInfo.InvariantCulture, $"linear {l.X1:R},{l.Y1:R} {l.X2:R},{l.Y2:R} {l.Extend} {l.Transform} [{Stops(l.Stops)}]"),
            RadialGradientPaint r => string.Create(CultureInfo.InvariantCulture, $"radial {r.StartX:R},{r.StartY:R},{r.StartRadius:R} {r.EndX:R},{r.EndY:R},{r.EndRadius:R} {r.Extend} {r.Transform} [{Stops(r.Stops)}]"),
            _ => paint.GetType().Name,
        };

        static string Stops(ImmutableArray<GradientStop> stops) =>
            string.Join("; ", stops.Select(stop => string.Create(CultureInfo.InvariantCulture, $"{stop.Offset:R} {stop.Color}")));
    }

    [Fact]
    public void AScene_ComesBackExactlyAsItWasWritten()
    {
        SvgDocument document = Doc(Everything);
        document.Issues.ShouldBeEmpty();
        VectorScene scene = document.Scene;
        // The document really holds what the name says, or this test proves less than it claims.
        string dump = Dump(scene);
        foreach (string needed in (string[])["linear ", "radial ", "Reflect", "Repeat", "group id=outer", "group id=inner", "dashes=[4 2 1 4 2 1]", "EvenOdd", "Round", "Bevel", "Square", " Q ", " C "])
        {
            dump.ShouldContain(needed);
        }

        byte[] bytes = VectorSceneFile.Write(scene);
        VectorSceneFile.IsSceneFile(bytes).ShouldBeTrue();
        VectorScene loaded = VectorSceneFile.Read(bytes);
        Dump(loaded).ShouldBe(dump);
        // And writing what was loaded gives the same bytes: nothing was normalised on the way in or out.
        VectorSceneFile.Write(loaded).ShouldBe(bytes);
    }

    [Fact]
    public void APathManyShapesShare_IsStoredOnce_AndIsOneObjectAgain()
    {
        VectorScene scene = Doc("<defs><path id=\"p\" d=\"M0 0 C 1 2 3 4 5 6 C 7 8 9 10 11 12 Z\"/></defs>" +
            string.Concat(Enumerable.Range(0, 50).Select(i => $"<use href=\"#p\" x=\"{i}\"/>"))).Scene;
        byte[] bytes = VectorSceneFile.Write(scene);
        // 50 shapes of 45 bytes and one path of 4 verbs and 14 coordinates (60 bytes): the path is there once, not 50 times.
        bytes.Length.ShouldBeLessThan(50 * 45 + 100);

        VectorShape[] shapes = [.. VectorSceneFile.Read(bytes).Nodes.Cast<VectorShape>()];
        shapes.Length.ShouldBe(50);
        shapes.ShouldAllBe(shape => ReferenceEquals(shape.Path, shapes[0].Path));
        shapes[7].Transform.M31.ShouldBe(7f);
    }

    [Fact]
    public void AnEmptyScene_AndOneWithNothingButEmptyGroups_ComeBack()
    {
        VectorScene empty = VectorSceneFile.Read(VectorSceneFile.Write(new VectorScene(12.5f, 7, [])));
        (empty.Width, empty.Height, empty.Nodes.Length).ShouldBe((12.5f, 7f, 0));

        var nested = new VectorScene(1, 1, [new VectorGroup { Opacity = 0.5f, Children = [new VectorGroup { Opacity = 1, Children = [], Id = "" }] }]);
        Dump(VectorSceneFile.Read(VectorSceneFile.Write(nested))).ShouldBe(Dump(nested));
    }

    // ---- Bytes that are not to be trusted ------------------------------------------------------------------------

    [Fact]
    public void EveryTruncation_IsRefused_AndNeverByAnythingButInvalidData()
    {
        byte[] bytes = VectorSceneFile.Write(Doc(Everything).Scene);
        for (int length = 0; length < bytes.Length; length++)
        {
            Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(bytes.AsSpan(0, length)), $"cut to {length} of {bytes.Length}");
        }
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. bytes, 0])).Message.ShouldContain("after its last node");
    }

    [Fact]
    public void EverySingleByteChanged_GivesASceneOrIsRefused_AndNothingElse()
    {
        byte[] bytes = VectorSceneFile.Write(Doc(Everything).Scene);
        int refused = 0;
        foreach (byte flip in (byte[])[0xFF, 0x01, 0x80])
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte[] damaged = [.. bytes];
                damaged[i] ^= flip;
                try
                {
                    VectorScene scene = VectorSceneFile.Read(damaged);
                    Dump(scene).ShouldNotBeNull();   // whatever it is, it can be walked
                }
                catch (InvalidDataException)
                {
                    refused++;
                }
            }
        }
        // Most changes land in a coordinate or a colour and are just another scene; the structural ones are refused.
        refused.ShouldBeGreaterThan(50);
    }

    [Fact]
    public void AFileThatClaimsMoreThanItHolds_IsRefusedBeforeAnythingIsAllocatedForIt()
    {
        // The header, then "two thousand million paths".
        byte[] header = [.. VectorSceneFile.Magic, 1, 0, 0, 0, 0, 0, 0x80, 0x3F, 0, 0, 0x80, 0x3F];
        byte[] bomb = [.. header, 0xFF, 0xFF, 0xFF, 0x7F];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(bomb)).Message.ShouldContain("more than it has bytes for");
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(16 << 10);

        // One path that claims a huge number of verbs, and one whose coordinates do not match its verbs.
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. header, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0]));
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. header, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, (byte)PathVerb.Line, 0, 0, 0, 0]))
            .Message.ShouldContain("need 2 coordinates");
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. header, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 9, 0, 0, 0, 0]))
            .Message.ShouldContain("path verb 9");
    }

    [Theory]
    [InlineData(new byte[0], "does not start with")]
    [InlineData(new byte[] { (byte)'S', (byte)'A', (byte)'V' }, "does not start with")]
    [InlineData(new byte[] { (byte)'<', (byte)'s', (byte)'v', (byte)'g', 0, 0, 0, 0 }, "does not start with")]
    [InlineData(new byte[] { (byte)'S', (byte)'A', (byte)'V', (byte)'S', 2, 0, 0, 0 }, "version 2")]
    public void WhatIsNotASceneFileOfThisVersion_IsRefused(byte[] data, string message)
    {
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(data)).Message.ShouldContain(message);
        VectorSceneFile.IsSceneFile(data).ShouldBe(data.Length >= 4 && data[0] == (byte)'S');
    }

    [Fact]
    public void GroupsNestedTooDeep_AreRefusedBothWays()
    {
        VectorNode node = new VectorGroup { Opacity = 1, Children = [] };
        for (int i = 0; i < VectorSceneFile.MaxDepth + 5; i++) node = new VectorGroup { Opacity = 1, Children = [node] };
        Should.Throw<NotSupportedException>(() => VectorSceneFile.Write(new VectorScene(1, 1, [node])));

        // Written by hand: the header, no paths, then groups each holding one group, deeper than the limit.
        var bytes = new List<byte>([.. VectorSceneFile.Magic, 1, 0, 0, 0, 0, 0, 0x80, 0x3F, 0, 0, 0x80, 0x3F, 0, 0, 0, 0]);
        for (int i = 0; i < VectorSceneFile.MaxDepth + 5; i++) bytes.AddRange([1, 0, 0, 0, 2, 0xFF, 0xFF, 0, 0, 0x80, 0x3F]);
        bytes.AddRange([0, 0, 0, 0]);
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. bytes])).Message.ShouldContain("deep");
    }

    private sealed record StripePaint : VectorPaint;

    [Fact]
    public void APaintThisLayoutHasNoPlaceFor_IsRefusedWhenWriting_NotWrittenAsSomethingElse()
    {
        var path = new PathBuilder();
        path.MoveTo(0, 0);
        path.LineTo(1, 1);
        var scene = new VectorScene(1, 1, [new VectorShape { Path = path.ToPath(), Fill = new VectorFill(new StripePaint(), 1, FillRule.NonZero) }]);
        Should.Throw<NotSupportedException>(() => VectorSceneFile.Write(scene)).Message.ShouldContain("StripePaint");
    }
}
