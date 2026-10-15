namespace SharpAstro.Webp;

internal static class WebpBufferPool<T>
{
    [ThreadStatic]
    private static List<T[]>? _buffers;

    public static T[] Rent(int minimumLength)
    {
        List<T[]> buffers = _buffers ??= new List<T[]>(8);
        int bestIndex = -1;
        int bestLength = int.MaxValue;
        for (int i = buffers.Count - 1; i >= 0; i--)
        {
            int length = buffers[i].Length;
            if (length >= minimumLength && length < bestLength)
            {
                bestIndex = i;
                bestLength = length;
            }
        }

        if (bestIndex >= 0)
        {
            T[] buffer = buffers[bestIndex];
            buffers.RemoveAt(bestIndex);
            return buffer;
        }

        return new T[minimumLength];
    }

    public static void Return(T[] buffer)
    {
        List<T[]> buffers = _buffers ??= new List<T[]>(8);
        if (buffers.Count < 32)
        {
            buffers.Add(buffer);
        }
    }
}
