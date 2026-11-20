namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Element-for-element equality of two spans, naming the first value that differs. For checks inside a loop:
/// a Shouldly call per element, with an interpolated message built whether or not it fails, made round-trip
/// tests allocate a gigabyte or more each (Codecs#92). The message is built only on failure.
/// </summary>
internal static class SpanAssert
{
    public static void Same<T>(ReadOnlySpan<T> actual, ReadOnlySpan<T> expected, string what)
        where T : IEquatable<T>
    {
        if (actual.Length != expected.Length)
        {
            Assert.Fail($"{what}: {actual.Length} values, expected {expected.Length}");
        }

        var at = actual.CommonPrefixLength(expected);
        if (at != actual.Length)
        {
            Assert.Fail($"{what}[{at}] is {actual[at]}, expected {expected[at]}");
        }
    }
}
