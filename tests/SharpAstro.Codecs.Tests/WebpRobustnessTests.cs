using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class WebpRobustnessTests
{
    private const int ExhaustiveTruncationLimit = 4096;
    private const int MutationsPerFixture = 16;
    private const int MaximumMutationDecodePixels = 1_000_000;

    [Fact]
    public void TruncationsAndBitFlipsAcrossFixturesStayWithinDecodeContracts()
    {
        string[] fixtures = Directory.GetFiles(FixtureDirectory(), "*.webp")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixtures);

        var random = new Random(0x5eed_9649);
        foreach (string fixture in fixtures)
        {
            byte[] source = File.ReadAllBytes(fixture);
            IEnumerable<int> truncationLengths = source.Length <= ExhaustiveTruncationLimit
                ? Enumerable.Range(0, source.Length)
                : SampleTruncationLengths(source.Length);
            foreach (int length in truncationLengths)
            {
                CheckCandidate(source[..length], fixture, $"truncated at {length}");
            }

            for (int mutation = 0; mutation < MutationsPerFixture; mutation++)
            {
                byte[] changed = source.ToArray();
                int offset = random.Next(changed.Length);
                int bit = random.Next(8);
                changed[offset] ^= (byte)(1 << bit);
                CheckCandidate(changed, fixture, $"bit flip {mutation} at {offset}, bit {bit}");
            }
        }
    }

    private static IEnumerable<int> SampleTruncationLengths(int length) =>
        new HashSet<int>
        {
            0,
            1,
            11,
            12,
            16,
            length / 4,
            length / 2,
            length * 3 / 4,
            length - 1,
        }.Where(sample => sample >= 0 && sample < length).OrderBy(static sample => sample);

    private static void CheckCandidate(byte[] data, string fixture, string mutation)
    {
        string context = $"{Path.GetFileName(fixture)} {mutation}";
        bool infoSucceeded = AssertAllowedOutcome(context, () => WebpDecoder.ReadInfo(data));
        if (!infoSucceeded)
        {
            Assert.False(WebpDecoder.TryReadInfo(data, out _), context);
            Assert.False(WebpImageDecoder.TryReadInfo(data, out _), context);
        }

        bool decoded = AssertAllowedOutcome(context, () => WebpDecoder.Decode(data));
        _ = AssertAllowedOutcome(context, () => WebpDecoder.DecodeAnimation(data));

        bool hasInfo = WebpDecoder.TryReadInfo(data, out WebpInfo info);
        if (hasInfo && (long)info.Width * info.Height <= MaximumMutationDecodePixels)
        {
            byte[] destination = new byte[checked(info.Width * info.Height * 4)];
            bool decodeToSucceeded = AssertAllowedOutcome(context, () => WebpDecoder.DecodeTo(data, destination));
            Assert.True(decodeToSucceeded == WebpDecoder.TryDecodeTo(data, destination), context);
            Assert.True(
                decodeToSucceeded == WebpImageDecoder.TryDecodeIntoRgba8(data, destination),
                context);
        }
        else
        {
            Assert.False(WebpDecoder.TryDecodeTo(data, Span<byte>.Empty), context);
            Assert.False(WebpImageDecoder.TryDecodeIntoRgba8(data, Span<byte>.Empty), context);
        }

        bool adapterDecoded = WebpImageDecoder.TryDecode(data, out _);
        if (!decoded)
        {
            Assert.False(adapterDecoded, context);
        }
    }

    private static bool AssertAllowedOutcome(string context, Action decode)
    {
        try
        {
            decode();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception exception)
        {
            Assert.Fail($"{context} threw unexpected {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static string FixtureDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");
}
