using System.Text;
using ImageMagick;
using SharpAstro.Heif;
using SharpAstro.Heif.Hevc;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

public class HeifPhoneFileTests(ITestOutputHelper output)
{
    [Fact]
    public void Dump()
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "apple-iphone13pro-hdr-sample.heic", "apple-iphone13pro-portrait-pug.heic", "samsung-200mp-hdrgainmap.heic", "samsung-tmap-aux-ycbcr.heic", "xiaomi-android.heic" })
        {
            var file = HeifCanonFileTests.Load(name);
            sb.AppendLine($"=== {name} {file.Length}");
            var heif = HeifReader.Read(file);
            sb.AppendLine($"brand {heif.MajorBrand} compat [{string.Join(",", heif.CompatibleBrands)}] primary {heif.PrimaryItemId}");
            foreach (var item in heif.Items)
            {
                sb.AppendLine($" item {item.Id} {item.Type} hidden={item.Hidden} ct={item.ContentType} props=[{string.Join("; ", item.Properties.Select(p => (p.Essential ? "!" : "") + Describe(p.Property)))}] refs=[{string.Join("; ", item.References.Select(r => r.Type + ":" + string.Join(",", r.ToItemIds)))}]");
            }
            try
            {
                var image = heif.DescribePrimary(file);
                sb.AppendLine($" primary {image.Width}x{image.Height} grid={(image.Grid is null ? "none" : $"{image.Grid.Rows}x{image.Grid.Columns} tiles {image.Grid.TileItemIds.Count}")} tile {image.TileWidth}x{image.TileHeight} depth {image.BitDepth} chroma {image.ChromaFormat} nclx {image.Nclx} icc {image.Icc?.Profile.Length} rot {image.Rotation} mir {image.Mirror} clap {image.CleanAperture}");
                var firstTile = image.Grid is null ? image.Item : heif.GetItem(image.Grid.TileItemIds[0])!;
                sb.AppendLine(HevcSurvey.Of(image.Coding, heif.ReadItemData(file, firstTile)).Report());
            }
            catch (Exception e) { sb.AppendLine($" primary FAILED {e.GetType().Name}: {e.Message}"); }
            try
            {
                using var magick = new MagickImage(file, MagickFormat.Heic);
                sb.AppendLine($" magick {magick.Width}x{magick.Height} depth {magick.Depth}");
            }
            catch (Exception e) { sb.AppendLine($" magick FAILED {e.GetType().Name}: {e.Message}"); }
        }
        File.WriteAllText(@"C:\Users\SebastianGodelet\.claude\jobs\41ce32ef\tmp\dump.txt", sb.ToString());
    }

    private static string Describe(HeifProperty p) => p switch
    {
        HeifUnknownProperty u => $"?{u.BoxType}({u.Length})",
        HeifIccColour i => $"icc({i.Profile.Length},{i.Restricted})",
        HeifPixelInformation x => $"pixi({string.Join(",", x.BitsPerChannel)})",
        HeifAuxiliaryType a => $"auxC({a.Urn}|{Convert.ToHexString(a.Subtype)})",
        HevcDecoderConfiguration h => $"hvcC({h.ChromaFormat},{h.BitDepthLuma})",
        _ => p.ToString()!,
    };
}
