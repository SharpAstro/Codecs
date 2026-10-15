using System.Diagnostics;
using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

public sealed class WebpOracleTests
{
    [Theory]
    [InlineData("flat-gray-16x16.webp", true)]
    [InlineData("flat-gray-16x16.webp", false)]
    [InlineData("gradient-16x16.webp", true)]
    [InlineData("gradient-16x16.webp", false)]
    [InlineData("noise-32x32.webp", true)]
    [InlineData("noise-32x32.webp", false)]
    [InlineData("noise-32x32-segments4.webp", true)]
    [InlineData("noise-32x32-segments4.webp", false)]
    [InlineData("noise-17x17.webp", true)]
    [InlineData("noise-17x17.webp", false)]
    [InlineData("noise-17x17-f30-strong-s0.webp", true)]
    [InlineData("noise-17x17-f30-strong-s0.webp", false)]
    [InlineData("noise-32x32-f30-simple-s0.webp", true)]
    [InlineData("noise-32x32-f30-simple-s0.webp", false)]
    [InlineData("noise-32x32-f30-strong-s0.webp", true)]
    [InlineData("noise-32x32-f30-strong-s0.webp", false)]
    [InlineData("noise-32x32-f30-strong-s7.webp", true)]
    [InlineData("noise-32x32-f30-strong-s7.webp", false)]
    [InlineData("noise-32x32-q0.webp", true)]
    [InlineData("noise-32x32-q0.webp", false)]
    [InlineData("noise-32x32-q5.webp", true)]
    [InlineData("noise-32x32-q5.webp", false)]
    [InlineData("noise-32x32-q50.webp", true)]
    [InlineData("noise-32x32-q50.webp", false)]
    [InlineData("noise-32x32-q95.webp", true)]
    [InlineData("noise-32x32-q95.webp", false)]
    [InlineData("noise-32x32-q100.webp", true)]
    [InlineData("noise-32x32-q100.webp", false)]
    [InlineData("noise-64x48-vp8-partitions2.webp", true)]
    [InlineData("noise-64x48-vp8-partitions2.webp", false)]
    [InlineData("noise-64x48-vp8-partitions4.webp", true)]
    [InlineData("noise-64x48-vp8-partitions4.webp", false)]
    [InlineData("noise-64x48-vp8-partitions8.webp", true)]
    [InlineData("noise-64x48-vp8-partitions8.webp", false)]
    [InlineData("odd-33x19-libvpx.webp", true)]
    [InlineData("odd-33x19-libvpx.webp", false)]
    [InlineData("testsrc2-320x240-q75.webp", true)]
    [InlineData("testsrc2-320x240-q75.webp", false)]
    [InlineData("testsrc2-320x240-f0.webp", true)]
    [InlineData("testsrc2-320x240-f0.webp", false)]
    [InlineData("testsrc2-320x240-q75-f100-s3.webp", true)]
    [InlineData("testsrc2-320x240-q75-f100-s3.webp", false)]
    [InlineData("testsrc2-320x240-q60-segments4-sns100-f60.webp", true)]
    [InlineData("testsrc2-320x240-q60-segments4-sns100-f60.webp", false)]
    public void RgbaMatchesDwebp(string fixtureName, bool noFancy)
    {
        string? dwebp = FindDwebp();
        OracleGate.RequireOrSkip(dwebp is not null, "dwebp", "Install the WebP command-line tools.");

        string input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixtureName);
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pam");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(dwebp!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (noFancy)
            {
                process.StartInfo.ArgumentList.Add("-nofancy");
            }
            process.StartInfo.ArgumentList.Add("-pam");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(output);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            byte[] expected = ReadPamPixels(File.ReadAllBytes(output));
            byte[] source = File.ReadAllBytes(input);
            WebpInfo info = WebpDecoder.ReadInfo(source);
            var actual = new byte[checked(info.Width * info.Height * 4)];
            if (noFancy)
            {
                WebpDecoder.DecodeToNoFancy(source, actual);
            }
            else
            {
                WebpDecoder.DecodeTo(source, actual);
            }

            if (!expected.AsSpan().SequenceEqual(actual))
            {
                int mismatchCount = 0;
                int maximumDifference = 0;
                int first = -1;
                for (int i = 0; i < Math.Min(expected.Length, actual.Length); i++)
                {
                    int difference = Math.Abs(actual[i] - expected[i]);
                    if (difference != 0)
                    {
                        first = first < 0 ? i : first;
                        mismatchCount++;
                        maximumDifference = Math.Max(maximumDifference, difference);
                    }
                }

                int pixel = Math.Max(first, 0) / 4;
                Assert.Fail(
                    $"{fixtureName} {(noFancy ? "nofancy" : "fancy")}: " +
                    $"{mismatchCount} channel mismatches, max |diff| {maximumDifference}; " +
                    $"first pixel ({pixel % info.Width},{pixel / info.Width}) channel " +
                    $"{Math.Max(first, 0) % 4}, expected " +
                    $"{(first >= 0 ? expected[first] : -1)}, actual " +
                    $"{(first >= 0 ? actual[first] : -1)}.");
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData("flat-gray-16x16.webp")]
    [InlineData("gradient-16x16.webp")]
    [InlineData("noise-32x32.webp")]
    [InlineData("noise-32x32-segments4.webp")]
    [InlineData("noise-17x17.webp")]
    [InlineData("noise-17x17-f30-strong-s0.webp")]
    [InlineData("noise-32x32-f30-simple-s0.webp")]
    [InlineData("noise-32x32-f30-strong-s0.webp")]
    [InlineData("noise-32x32-f30-strong-s7.webp")]
    [InlineData("noise-32x32-q0.webp")]
    [InlineData("noise-32x32-q5.webp")]
    [InlineData("noise-32x32-q50.webp")]
    [InlineData("noise-32x32-q95.webp")]
    [InlineData("noise-32x32-q100.webp")]
    [InlineData("noise-64x48-vp8-partitions2.webp")]
    [InlineData("noise-64x48-vp8-partitions4.webp")]
    [InlineData("noise-64x48-vp8-partitions8.webp")]
    [InlineData("odd-33x19-libvpx.webp")]
    [InlineData("testsrc2-320x240-q75.webp")]
    [InlineData("testsrc2-320x240-f0.webp")]
    [InlineData("testsrc2-320x240-q75-f100-s3.webp")]
    [InlineData("testsrc2-320x240-q60-segments4-sns100-f60.webp")]
    public void YuvMatchesDwebp(string fixtureName)
    {
        string? dwebp = FindDwebp();
        OracleGate.RequireOrSkip(dwebp is not null, "dwebp", "Install the WebP command-line tools.");

        string input = Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp", fixtureName);
        string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yuv");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(dwebp!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-quiet");
            process.StartInfo.ArgumentList.Add(input);
            process.StartInfo.ArgumentList.Add("-yuv");
            process.StartInfo.ArgumentList.Add("-o");
            process.StartInfo.ArgumentList.Add(output);
            process.Start();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            byte[] expected = File.ReadAllBytes(output);
            byte[] source = File.ReadAllBytes(input);
            Vp8Yuv yuv = WebpDecoder.DecodeYuv(source);
            int chromaWidth = (yuv.Width + 1) / 2;
            int chromaHeight = (yuv.Height + 1) / 2;
            int yLength = yuv.Width * yuv.Height;
            int chromaLength = chromaWidth * chromaHeight;
            var actual = new byte[yLength + 2 * chromaLength];
            yuv.Y.CopyTo(actual, 0);
            yuv.U.CopyTo(actual, yLength);
            yuv.V.CopyTo(actual, yLength + chromaLength);

            if (!expected.AsSpan().SequenceEqual(actual))
            {
                int commonLength = Math.Min(expected.Length, actual.Length);
                int difference = 0;
                while (difference < commonLength &&
                       expected[difference] == actual[difference])
                {
                    difference++;
                }

                int mismatchCount = Math.Abs(expected.Length - actual.Length);
                int negativeOneCount = 0;
                int positiveOneCount = 0;
                int maxAbsoluteDifference = 0;
                int[] planeMismatchCounts = new int[3];
                int[] planeMaxAbsoluteDifferences = new int[3];
                for (int i = 0; i < commonLength; i++)
                {
                    int delta = actual[i] - expected[i];
                    if (delta != 0)
                    {
                        mismatchCount++;
                        int planeIndex = i < yLength ? 0 : i < yLength + chromaLength ? 1 : 2;
                        planeMismatchCounts[planeIndex]++;
                        planeMaxAbsoluteDifferences[planeIndex] =
                            Math.Max(planeMaxAbsoluteDifferences[planeIndex], Math.Abs(delta));
                        if (delta == -1)
                        {
                            negativeOneCount++;
                        }
                        else if (delta == 1)
                        {
                            positiveOneCount++;
                        }

                        maxAbsoluteDifference = Math.Max(maxAbsoluteDifference, Math.Abs(delta));
                    }
                }

                string plane;
                int planeOffset;
                int blockSize;
                if (difference < yLength)
                {
                    plane = "Y";
                    planeOffset = difference;
                    blockSize = 16;
                }
                else if (difference < yLength + chromaLength)
                {
                    plane = "U";
                    planeOffset = difference - yLength;
                    blockSize = 8;
                }
                else
                {
                    plane = "V";
                    planeOffset = difference - yLength - chromaLength;
                    blockSize = 8;
                }
                int planeWidth = plane == "Y" ? yuv.Width : chromaWidth;
                int x = planeOffset % planeWidth;
                int row = planeOffset / planeWidth;
                Assert.Fail(
                    $"{fixtureName}: first YUV mismatch at byte {difference}, {plane}({x},{row}), " +
                    $"MB=({x / blockSize},{row / blockSize}), " +
                    $"mismatches {mismatchCount}, max |diff| {maxAbsoluteDifference}, " +
                    $"planes Y/U/V={planeMismatchCounts[0]}/{planeMismatchCounts[1]}/{planeMismatchCounts[2]} " +
                    $"(max {planeMaxAbsoluteDifferences[0]}/{planeMaxAbsoluteDifferences[1]}/{planeMaxAbsoluteDifferences[2]}), " +
                    $"diff -1/+1={negativeOneCount}/{positiveOneCount}, " +
                    $"expected length {expected.Length}, actual length {actual.Length}, " +
                    $"expected {(difference < expected.Length ? expected[difference] : -1)}, " +
                    $"actual {(difference < actual.Length ? actual[difference] : -1)}.");
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string? FindDwebp()
    {
        string? configured = Environment.GetEnvironmentVariable("DWEBP");
        if (configured is not null && File.Exists(configured))
        {
            return configured;
        }

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "dwebp.exe" : "dwebp");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static byte[] ReadPamPixels(byte[] pam)
    {
        int lineStart = 0;
        while (lineStart < pam.Length)
        {
            int lineEnd = Array.IndexOf(pam, (byte)'\n', lineStart);
            if (lineEnd < 0)
            {
                break;
            }

            string line = System.Text.Encoding.ASCII.GetString(pam, lineStart, lineEnd - lineStart).TrimEnd('\r');
            lineStart = lineEnd + 1;
            if (line == "ENDHDR")
            {
                return pam.AsSpan(lineStart).ToArray();
            }
        }

        throw new InvalidDataException("The dwebp output does not contain a PAM header.");
    }
}
