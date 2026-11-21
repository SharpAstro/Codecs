namespace SharpAstro.Heif.Hevc;

/// <summary>
/// The arrays a <see cref="HevcPictureDecoder"/> builds, kept between decodes so a caller that decodes the same
/// picture shape many times (the damaged-slice fuzz tests) clears them instead of allocating them again.
/// Cleared, not reallocated: a reused array is zeroed (or refilled with its initial value) before use, so a
/// decode through a workspace is the decode of a fresh one.
/// <para>
/// The three sample planes are the exception to "kept": a decode that finishes hands them to the returned
/// <see cref="HevcPicture"/>, which owns them from then on, so the workspace forgets them and the next decode
/// allocates new ones. Only a decode that threw (and so returned no picture) leaves its planes to be reused.
/// The plain <see cref="HevcDecoder.Decode(HevcDecoderConfiguration, ReadOnlySpan{byte}, HevcDecodeStatistics?)"/>
/// uses no workspace and hands out its own arrays as before. Not thread safe: one workspace per thread.
/// </para>
/// </summary>
internal sealed class HevcDecodeWorkspace
{
    /// <summary>Every array a picture decoder takes; a name the compiler checks, where a string literal would only fail at run time.</summary>
    internal enum Slot { Luma, Cb, Cr, CtDepth, LumaMode, ChromaMode, QpMap, Edges, Bypass, SaoType, SaoBand, SaoOffsets, CtbSlice }

    private readonly Dictionary<Slot, Array> _arrays = [];

    /// <summary>An array of exactly <paramref name="length"/> filled with <paramref name="fill"/>, reused when one of that size is held.</summary>
    internal T[] Take<T>(Slot slot, int length, T fill = default!)
    {
        var zero = EqualityComparer<T>.Default.Equals(fill, default!);
        if (_arrays.TryGetValue(slot, out var held) && held is T[] array && array.Length == length)
        {
            if (zero) Array.Clear(array); else Array.Fill(array, fill);
            return array;
        }
        var fresh = new T[length];
        if (!zero) Array.Fill(fresh, fill);
        _arrays[slot] = fresh;
        return fresh;
    }

    /// <summary>The picture owns these arrays now: forget them, so no later decode writes into a returned picture.</summary>
    internal void Release(Slot a, Slot b, Slot c)
    {
        _arrays.Remove(a);
        _arrays.Remove(b);
        _arrays.Remove(c);
    }
}
