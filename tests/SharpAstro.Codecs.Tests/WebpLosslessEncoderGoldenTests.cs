using System.Security.Cryptography;
using SharpAstro.Webp;

namespace SharpAstro.Codecs.Tests;

// Pins the lossless encoder's output byte for byte (Codecs#100). Allocation work on the encoder must not change a
// single output byte, and the round-trip tests would pass a different but valid stream. Modelled on the JPEG
// encoder golden: SHA-256 of 300 encodes (six image kinds x five sizes x efforts 0 to 9), committed in
// Fixtures/webp/webp-encoder-golden.tsv. Regenerate with REGEN_WEBP_ENCODER_GOLDEN=1, only after an intentional
// change of what the encoder writes, and review the diff.

/// <summary>The lossless encoder's committed output digests.</summary>
public sealed class WebpLosslessEncoderGoldenTests
{
    private const string GoldenFileName = "webp-encoder-golden.tsv";

    private static readonly string[] Kinds = ["noise", "gradient", "few-colours", "alpha", "blocks", "far-repeat"];

    private static readonly (int Width, int Height)[] Sizes = [(1, 1), (17, 9), (64, 33), (257, 13), (200, 150)];

    public static IEnumerable<object[]> Cases() =>
        Kinds.SelectMany(kind => Sizes.SelectMany(size => Enumerable.Range(0, 10)
            .Select(effort => new object[] { CaseName(kind, size.Width, size.Height, effort) })));

    [Fact]
    public void TheGoldenCoversThreeHundredEncodes()
    {
        Assert.Equal(300, Cases().Count());
        Assert.Equal(300, LoadGolden(FixtureDirectory()).Count);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EncodeMatchesTheGoldenDigest(string name)
    {
        Dictionary<string, string> golden = LoadGolden(FixtureDirectory());
        Assert.True(golden.TryGetValue(name, out string? expected), $"{name} has no golden row");
        Assert.Equal(expected, Digest(name));
    }

    [Fact]
    public void RegenerateGolden()
    {
        if (Environment.GetEnvironmentVariable("REGEN_WEBP_ENCODER_GOLDEN") != "1")
        {
            Assert.Skip("set REGEN_WEBP_ENCODER_GOLDEN=1 to rewrite the encoder golden baseline");
            return;
        }

        var lines = new List<string>
        {
            "# webp-encoder-golden.tsv - SHA-256 of WebpEncoder.EncodeLossless output, for WebpLosslessEncoderGoldenTests.",
            "# case (kind-WxH-eN)<TAB>sha256 of the encoded file.",
            "# Regenerate with REGEN_WEBP_ENCODER_GOLDEN=1, only after an intentional change of the encoder's output.",
        };
        foreach (object[] c in Cases())
        {
            string name = (string)c[0];
            lines.Add($"{name}\t{Digest(name)}");
        }

        File.WriteAllLines(Path.Combine(RepoFixtureDirectory(), GoldenFileName), lines);
    }

    private static string CaseName(string kind, int width, int height, int effort) => $"{kind}-{width}x{height}-e{effort}";

    private static string Digest(string name)
    {
        string[] parts = name.Split('-');
        string kind = string.Join('-', parts[..^2]);
        string[] dims = parts[^2].Split('x');
        int width = int.Parse(dims[0]);
        int height = int.Parse(dims[1]);
        int effort = int.Parse(parts[^1][1..]);
        byte[] rgba = Image(kind, width, height);
        byte[] encoded = WebpEncoder.EncodeLossless(rgba, width, height, 0, new WebpEncodeOptions { Effort = effort });
        return Convert.ToHexString(SHA256.HashData(encoded)).ToLowerInvariant();
    }

    private static byte[] Image(string kind, int w, int h)
    {
        uint s = 0x2545F491u ^ (uint)(w * 7919 + h * 104729);
        byte[] px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                switch (kind)
                {
                    case "noise":
                        px[i] = (byte)Next(ref s);
                        px[i + 1] = (byte)Next(ref s);
                        px[i + 2] = (byte)Next(ref s);
                        px[i + 3] = 255;
                        break;
                    case "gradient":
                        px[i] = (byte)(x * 255 / Math.Max(1, w - 1) + (int)(Next(ref s) % 5));
                        px[i + 1] = (byte)(y * 255 / Math.Max(1, h - 1) + (int)(Next(ref s) % 5));
                        px[i + 2] = (byte)(128 + (int)(Next(ref s) % 9));
                        px[i + 3] = 255;
                        break;
                    case "few-colours":
                        {
                            uint k = Next(ref s) % 4;
                            px[i] = (byte)(k * 80);
                            px[i + 1] = (byte)(255 - k * 60);
                            px[i + 2] = (byte)(k * k * 17);
                            px[i + 3] = 255;
                            break;
                        }
                    case "alpha":
                        px[i] = (byte)(x * 13 + y);
                        px[i + 1] = (byte)(y * 9);
                        px[i + 2] = (byte)Next(ref s);
                        px[i + 3] = (byte)(x * 255 / Math.Max(1, w - 1));
                        break;
                    case "blocks":
                        {
                            int k = ((x / 8) * 3 + (y / 8) * 5) % 6;
                            px[i] = (byte)(k * 40);
                            px[i + 1] = (byte)(200 - k * 30);
                            px[i + 2] = (byte)(k * 20 + 9);
                            px[i + 3] = 255;
                            break;
                        }
                    case "far-repeat":
                        {
                            int p = y * w + x;
                            int n = w * h;
                            int third = Math.Max(1, n / 3);
                            if (p >= 2 * third)
                            {
                                Array.Copy(px, (p - 2 * third) * 4, px, i, 4);
                            }
                            else if (p < third)
                            {
                                px[i] = (byte)Next(ref s);
                                px[i + 1] = (byte)Next(ref s);
                                px[i + 2] = (byte)Next(ref s);
                                px[i + 3] = 255;
                            }
                            else
                            {
                                px[i] = (byte)(x / 2);
                                px[i + 1] = (byte)y;
                                px[i + 2] = 40;
                                px[i + 3] = 255;
                            }

                            break;
                        }
                    default:
                        throw new ArgumentException(kind);
                }
            }
        }

        return px;
    }

    private static uint Next(ref uint state)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        return state;
    }

    private static string FixtureDirectory() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "webp");

    private static Dictionary<string, string> LoadGolden(string directory)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(directory, GoldenFileName)))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length != 2 || !map.TryAdd(parts[0], parts[1]))
            {
                throw new InvalidDataException($"Invalid or duplicate encoder golden row: {line}");
            }
        }

        return map;
    }

    private static string RepoFixtureDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "SharpAstro.Codecs.Tests", "Fixtures", "webp");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate the WebP fixture source directory");
    }
}
