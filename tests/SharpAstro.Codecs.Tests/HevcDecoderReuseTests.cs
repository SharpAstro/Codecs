using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// A decode through a <see cref="HevcDecodeWorkspace"/> (the damaged-slice tests use one to stop allocating the
/// planes and block maps ~21,000 times) must be the decode of a fresh decoder: the same planes, and the same
/// refusal, whatever the workspace held before. The fixtures differ in size, chroma format and tools, so one
/// workspace is driven through all of them interleaved, which is what makes a stale array show.
/// </summary>
public class HevcDecoderReuseTests
{
    private const int DamagedPerFixture = 8;

    private sealed record Outcome(string Kind, HevcPicture? Picture);

    private static Outcome Run(HevcDecoderConfiguration configuration, byte[] data, HevcDecodeWorkspace? workspace)
    {
        try { return new("decoded", HevcDecoder.Decode(configuration, data, null, workspace)); }
        catch (InvalidDataException) { return new("invalid", null); }
        catch (NotSupportedException) { return new("not supported", null); }
    }

    private static void ShouldMatch(Outcome actual, Outcome expected, string what)
    {
        actual.Kind.ShouldBe(expected.Kind, what);
        if (expected.Picture is null) return;
        var (a, e) = (actual.Picture!, expected.Picture);
        (a.Width, a.Height, a.ChromaFormatIdc, a.ChromaWidth, a.ChromaHeight).ShouldBe((e.Width, e.Height, e.ChromaFormatIdc, e.ChromaWidth, e.ChromaHeight), what);
        a.Luma.AsSpan().SequenceEqual(e.Luma).ShouldBeTrue($"{what}: luma differs");
        (a.Cb is null).ShouldBe(e.Cb is null, what);
        if (e.Cb is not null) a.Cb!.AsSpan().SequenceEqual(e.Cb).ShouldBeTrue($"{what}: cb differs");
        if (e.Cr is not null) a.Cr!.AsSpan().SequenceEqual(e.Cr).ShouldBeTrue($"{what}: cr differs");
    }

    [Fact]
    public void AReusedWorkspace_DecodesAsAFreshOneDoes_IntactAndDamaged_FixturesInterleaved()
    {
        var names = HevcFixtureTests.Names.Select(d => d.Data).ToList();
        var inputs = new List<(string What, HevcDecoderConfiguration Configuration, byte[] Data)>[DamagedPerFixture + 2];
        for (var round = 0; round < inputs.Length; round++) inputs[round] = [];

        foreach (var name in names)
        {
            var (configuration, item) = HevcFixtureTests.Load(name);
            var random = new Random(HevcFixtureTests.StableSeed(name));
            inputs[0].Add(($"{name} intact", configuration, item));
            for (var round = 1; round <= DamagedPerFixture; round++)
            {
                var damaged = (byte[])item.Clone();
                var position = 6 + random.Next(item.Length - 6);
                damaged[position] ^= (byte)(1 + random.Next(255));
                inputs[round].Add(($"{name} byte {position}", configuration, damaged));
            }
            // A truncation, the length prefix kept honest as the fuzz test does.
            var cut = item.Length * 2 / 3;
            var truncated = item[..cut];
            var length = cut - 4;
            truncated[0] = (byte)(length >> 24); truncated[1] = (byte)(length >> 16); truncated[2] = (byte)(length >> 8); truncated[3] = (byte)length;
            inputs[^1].Add(($"{name} cut at {cut}", configuration, truncated));
        }

        // Reference: every input decoded fresh, no workspace. Reused: the same inputs round by round, so fixture
        // A's arrays are in the workspace when fixture B (another size or chroma format) comes next. A workspace
        // outlives every picture it returned, so those are checked again at the end for being left alone.
        var workspace = new HevcDecodeWorkspace();
        var reused = new List<(string What, Outcome Fresh, Outcome Reused)>();
        foreach (var round in inputs)
            foreach (var (what, configuration, data) in round)
                reused.Add((what, Run(configuration, data, null), Run(configuration, data, workspace)));

        reused.Count(r => r.Fresh.Kind == "decoded").ShouldBeGreaterThanOrEqualTo(names.Count, "every intact fixture decodes");
        reused.Count(r => r.Fresh.Kind == "invalid").ShouldBeGreaterThan(0, "the damage is refused for some inputs");
        foreach (var (what, fresh, reuse) in reused) ShouldMatch(reuse, fresh, what);
    }

    [Fact]
    public void ADecodeThatFinished_LeavesItsPlanesToItsPicture_NotToTheNextDecode()
    {
        var (configuration, item) = HevcFixtureTests.Load("x265-420p8-deblock");
        var workspace = new HevcDecodeWorkspace();
        var first = HevcDecoder.Decode(configuration, item, null, workspace);
        var snapshot = first.Luma.ToArray();
        var second = HevcDecoder.Decode(configuration, item, null, workspace);

        second.Luma.ShouldNotBeSameAs(first.Luma);
        first.Luma.AsSpan().SequenceEqual(snapshot).ShouldBeTrue("a later decode wrote into a returned picture");
        second.Luma.AsSpan().SequenceEqual(snapshot).ShouldBeTrue();
    }
}
