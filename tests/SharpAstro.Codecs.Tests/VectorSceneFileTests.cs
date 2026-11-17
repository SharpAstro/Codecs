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
    /// <summary>
    /// One of everything a scene can hold: both gradients, strokes with dashes, groups in groups, ids, a shared path, and
    /// since version 2 a clip (with a clip on one outline and one it intersects), both kinds of mask, a pattern with a
    /// pattern in it, an embedded image and one that is only named.
    /// </summary>
    private static readonly string Everything =
        "<defs><linearGradient id=\"l\" x2=\"0.25\" spreadMethod=\"reflect\" gradientTransform=\"rotate(30)\"><stop offset=\"0\" stop-color=\"#036\"/><stop offset=\"0.5\" stop-color=\"#fc0\" stop-opacity=\"0.5\"/><stop offset=\"1\" stop-color=\"#f00\"/></linearGradient>" +
        "<radialGradient id=\"r\" fx=\"0.3\" fy=\"0.2\" fr=\"0.05\" spreadMethod=\"repeat\" gradientUnits=\"userSpaceOnUse\" cx=\"40\" cy=\"40\" r=\"30\"><stop offset=\"0\" stop-color=\"#fff\"/><stop offset=\"1\" stop-color=\"#048\"/></radialGradient>" +
        "<path id=\"shared\" d=\"M0 0 L10 0 Q 15 5 10 10 C 5 15 0 15 0 10 Z M2 2 h3\"/></defs>" +
        "<rect id=\"plain\" x=\"1\" y=\"2\" width=\"30\" height=\"20\" rx=\"4\" fill=\"url(#l)\" fill-rule=\"evenodd\" fill-opacity=\"0.75\"/>" +
        "<circle cx=\"50\" cy=\"50\" r=\"20\" fill=\"url(#r)\" stroke=\"#123456\" stroke-width=\"2.5\" stroke-linecap=\"round\" stroke-linejoin=\"bevel\" stroke-miterlimit=\"7\" stroke-dasharray=\"4 2 1\" stroke-dashoffset=\"1.5\" stroke-opacity=\"0.5\"/>" +
        "<g id=\"outer\" opacity=\"0.5\" transform=\"translate(10 20) rotate(15)\"><use href=\"#shared\" fill=\"rgba(10,20,30,0.4)\"/>" +
        "<g id=\"inner\" opacity=\"0.25\"><use href=\"#shared\" x=\"20\" stroke=\"url(#l)\" stroke-linecap=\"square\"/><line x2=\"5\" y2=\"5\" stroke=\"red\"/></g></g>" +
        "<use href=\"#shared\" x=\"60\" y=\"5\" opacity=\"0.5\" stroke=\"#0f0\"/>" +
        "<defs><clipPath id=\"c\" clip-path=\"url(#c2)\"><rect width=\"30\" height=\"30\" clip-rule=\"evenodd\" clip-path=\"url(#c3)\"/><circle cx=\"40\" cy=\"40\" r=\"9\"/></clipPath>" +
        "<clipPath id=\"c2\"><rect width=\"70\" height=\"70\"/></clipPath><clipPath id=\"c3\"><circle cx=\"10\" cy=\"10\" r=\"20\"/></clipPath>" +
        "<mask id=\"lum\"><rect width=\"80\" height=\"80\" fill=\"#888\"/></mask><mask id=\"alpha\" mask-type=\"alpha\"><circle cx=\"20\" cy=\"20\" r=\"50\" fill-opacity=\"0.5\"/></mask>" +
        "<pattern id=\"dots\" patternUnits=\"userSpaceOnUse\" width=\"4\" height=\"4\"><circle cx=\"2\" cy=\"2\" r=\"1\" fill=\"#c00\"/></pattern>" +
        "<pattern id=\"tiles\" width=\"0.5\" height=\"0.25\" patternTransform=\"rotate(10)\"><rect width=\"9\" height=\"9\" fill=\"url(#dots)\"/><path d=\"M0 0 L5 5\" stroke=\"#00c\"/></pattern></defs>" +
        "<g id=\"clipped\" clip-path=\"url(#c)\" mask=\"url(#lum)\" opacity=\"0.9\"><rect width=\"60\" height=\"60\" fill=\"url(#tiles)\" stroke=\"url(#dots)\" stroke-width=\"3\"/></g>" +
        "<circle cx=\"30\" cy=\"30\" r=\"12\" mask=\"url(#alpha)\"/>" +
        $"<image id=\"embedded\" x=\"3\" y=\"4\" width=\"20\" height=\"10\" preserveAspectRatio=\"xMaxYMin slice\" opacity=\"0.5\" transform=\"rotate(5)\" href=\"{SvgBrowserOracleTests.Picture()}\"/>" +
        "<image id=\"named\" width=\"8\" height=\"8\" preserveAspectRatio=\"none\" href=\"https://example.org/a%20picture.png\"/>";

    [Fact]
    public void AScene_ComesBackExactlyAsItWasWritten()
    {
        SvgDocument document = Doc(Everything);
        document.Issues.ShouldBeEmpty();
        VectorScene scene = document.Scene;
        // The document really holds what the name says, or this test proves less than it claims.
        string dump = Dump(scene);
        foreach (string needed in (string[])
        [
            "linear ", "radial ", "Reflect", "Repeat", "group id=outer", "group id=inner", "dashes=[4 2 1 4 2 1]", "EvenOdd", "Round", "Bevel", "Square", " Q ", " C ",
            "group id=clipped opacity=0.9", "clip\n", "within\n", "intersect\n", "outline EvenOdd", "mask Luminance", "mask Alpha", "pattern tile=", "fill(pattern tile=", "stroke(pattern tile=",
            "<shape id=(none)", "image id=embedded", "Slice align=1,0 opacity=0.5 type=image/png source=(none) bytes=", "image id=named", "Stretch", "source=https://example.org/a%20picture.png bytes=0",
        ])
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
    public void AVersion1File_IsStillRead()
    {
        // Written by hand as version 1 wrote it: one path (M 1 2 L 3 4), then a group (no flags byte after its
        // opacity) holding one shape with a solid fill and no stroke.
        using var stream = new MemoryStream();
        long shapeTag;
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(VectorSceneFile.Magic);
            writer.Write((ushort)1);
            writer.Write((ushort)0);
            writer.Write(20f);
            writer.Write(10f);
            writer.Write(1);                                        // paths
            writer.Write(2);
            writer.Write(4);
            writer.Write([(byte)PathVerb.Move, (byte)PathVerb.Line]);
            foreach (float value in (float[])[1, 2, 3, 4]) writer.Write(value);
            writer.Write(1);                                        // nodes
            writer.Write((byte)2);                                  // a group
            writer.Write((ushort)1);
            writer.Write((byte)'g');
            writer.Write(0.5f);
            writer.Write(1);                                        // its children
            writer.Flush();
            shapeTag = stream.Position;
            writer.Write((byte)1);                                  // a shape
            writer.Write((ushort)0xFFFF);
            writer.Write(0);
            foreach (float value in (float[])[1, 0, 0, 1, 5, 6]) writer.Write(value);
            writer.Write(true);
            writer.Write((byte)1);
            writer.Write([(byte)10, (byte)20, (byte)30, (byte)255]);
            writer.Write(0.25f);
            writer.Write((byte)FillRule.EvenOdd);
            writer.Write(false);
        }

        VectorScene scene = VectorSceneFile.Read(stream.ToArray());
        (scene.Width, scene.Height).ShouldBe((20f, 10f));
        VectorGroup group = scene.Nodes.ShouldHaveSingleItem().ShouldBeOfType<VectorGroup>();
        (group.Id, group.Opacity).ShouldBe(("g", 0.5f));
        group.Clip.ShouldBeNull();
        group.Mask.ShouldBeNull();
        VectorShape shape = group.Children.ShouldHaveSingleItem().ShouldBeOfType<VectorShape>();
        Describe(shape.Path).ShouldBe("M 1 2 L 3 4");
        (shape.Transform.M31, shape.Transform.M32).ShouldBe((5f, 6f));
        shape.Fill.ShouldBe(new VectorFill(new SolidPaint(new Rgba32(10, 20, 30)), 0.25f, FillRule.EvenOdd));

        // What version 1 had no tag for is not something a version 1 file may hold.
        byte[] bytes = stream.ToArray();
        bytes[shapeTag] = 3;                                        // the shape's tag, as an image's
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(bytes)).Message.ShouldContain("node tag 3");
    }

    [Fact]
    public void AClipOrAnImage_ThatClaimsMoreThanTheFileHolds_IsRefusedBeforeAnythingIsAllocatedForIt()
    {
        // Version 2, no paths, one group with a clip that claims two thousand million outlines.
        byte[] header = [.. VectorSceneFile.Magic, 2, 0, 0, 0, 0, 0, 0x80, 0x3F, 0, 0, 0x80, 0x3F, 0, 0, 0, 0, 1, 0, 0, 0];
        byte[] clip = [.. header, 2, 0xFF, 0xFF, 0, 0, 0x80, 0x3F, 1, 0xFF, 0xFF, 0xFF, 0x7F];
        long before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(clip)).Message.ShouldContain("clip shapes");
        // And an image that claims two thousand million bytes of picture.
        byte[] image = [.. header, 3, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0, 0, 0, 0, 0, 0, 0, 0];
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read(image)).Message.ShouldContain("image bytes");
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(32 << 10);

        // Flags that are not flags, and kinds that are not kinds.
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. header, 2, 0xFF, 0xFF, 0, 0, 0x80, 0x3F, 4, 0, 0, 0, 0])).Message.ShouldContain("group flags");
        Should.Throw<InvalidDataException>(() => VectorSceneFile.Read([.. header, 2, 0xFF, 0xFF, 0, 0, 0x80, 0x3F, 2, 7, 0, 0, 0, 0, 0, 0, 0, 0])).Message.ShouldContain("mask kind");
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
    [InlineData(new byte[] { (byte)'S', (byte)'A', (byte)'V', (byte)'S', 3, 0, 0, 0 }, "version 3")]
    [InlineData(new byte[] { (byte)'S', (byte)'A', (byte)'V', (byte)'S', 0, 0, 0, 0 }, "version 0")]
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
