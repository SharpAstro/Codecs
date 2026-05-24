using SharpAstro.Jxr;
using Xunit;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// One-off diagnostic for a real-world JXR the user's astrophotography
/// pipeline produced that Windows Photo flagged as "invalid file format".
/// Walks the container then the codestream prologue and prints every
/// step's parsed values so we can pinpoint what's off.
/// </summary>
public sealed class JxrUserFixtureDiagnosticTests
{
    private readonly ITestOutputHelper _out;
    public JxrUserFixtureDiagnosticTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Probe_UserAstroJxr()
    {
        var path = @"C:\Users\SebastianGodelet\OneDrive\Dev\fits_tiff\Seagull_Nebula_1.5x_drz-RGB-session_1_sharpened.jxr";
        if (!File.Exists(path))
        {
            _out.WriteLine($"SKIP — file not present at {path}");
            return;
        }

        var bytes = File.ReadAllBytes(path);
        _out.WriteLine($"File size: {bytes.Length} bytes");

        JxrFile file;
        try { file = JxrContainer.Read(bytes); }
        catch (Exception ex)
        {
            _out.WriteLine($"JxrContainer.Read threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        _out.WriteLine($"Container:");
        _out.WriteLine($"  Width:           {file.Width}");
        _out.WriteLine($"  Height:          {file.Height}");
        _out.WriteLine($"  PixelFormat:     {file.PixelFormat}");
        _out.WriteLine($"  Codestream:      {file.Codestream.Length} bytes");
        _out.WriteLine($"  AlphaCodestream: {(file.AlphaCodestream?.Length.ToString() ?? "null")}");
        _out.WriteLine($"  IccProfile:      {(file.IccProfile?.Length.ToString() ?? "null")}");
        _out.WriteLine($"  XmpMetadata:     {(file.XmpMetadata?.Length.ToString() ?? "null")}");

        var reader = new BitReader(file.Codestream);
        ImageHeader img;
        try { img = ImageHeader.Read(ref reader); }
        catch (Exception ex)
        {
            _out.WriteLine($"ImageHeader.Read threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        _out.WriteLine($"ImageHeader:");
        _out.WriteLine($"  OutputClrFmt:    {img.OutputClrFmt}");
        _out.WriteLine($"  OutputBitDepth:  {img.OutputBitDepth}");
        _out.WriteLine($"  Width×Height:    {img.WidthMinus1 + 1} × {img.HeightMinus1 + 1}");
        _out.WriteLine($"  ShortHeader:     {img.ShortHeaderFlag}");
        _out.WriteLine($"  LongWord:        {img.LongWordFlag}");
        _out.WriteLine($"  TilingFlag:      {img.TilingFlag}");
        _out.WriteLine($"  FrequencyMode:   {img.FrequencyModeCodestreamFlag}");
        _out.WriteLine($"  OverlapMode:     {img.OverlapMode}");
        _out.WriteLine($"  AlphaPlane:      {img.AlphaImagePlaneFlag}");
        _out.WriteLine($"  IndexTable:      {img.IndexTablePresentFlag}");
        _out.WriteLine($"  TrimFlexBits:    {img.TrimFlexBitsFlag}");
        _out.WriteLine($"  PremultAlpha:    {img.PremultipliedAlphaFlag}");
        _out.WriteLine($"  RedBlueNotSwap:  {img.RedBlueNotSwappedFlag}");

        try
        {
            var plane = ImagePlaneHeader.Read(ref reader, img.OutputBitDepth);
            _out.WriteLine($"ImagePlaneHeader:");
            _out.WriteLine($"  InternalClrFmt:  {plane.InternalClrFmt}");
            _out.WriteLine($"  BandsPresent:    {plane.BandsPresent}");
            _out.WriteLine($"  NumComponents:   {plane.NumComponents}");
            _out.WriteLine($"  ShiftBits:       {plane.ShiftBits}");
            _out.WriteLine($"  LenMantissa:     {plane.LenMantissa}");
            _out.WriteLine($"  ExpBias:         {plane.ExpBias}");
            _out.WriteLine($"  DcQuant:         {plane.DcQuant}");
            _out.WriteLine($"  UseDcQpForLp:    {plane.UseDcQpForLp}");
            _out.WriteLine($"  LpQuant:         {plane.LpQuant}");
            _out.WriteLine($"  UseLpQpForHp:    {plane.UseLpQpForHp}");
            _out.WriteLine($"  HpQuant:         {plane.HpQuant}");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"ImagePlaneHeader.Read threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Try a full decode through CodedImage
        try
        {
            var coded = CodedImage.Decode(file.Codestream);
            _out.WriteLine($"CodedImage.Decode OK — {coded.Macroblocks.Length} macroblocks");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"CodedImage.Decode threw: {ex.GetType().Name}: {ex.Message}");
        }

        // Then attempt the file-level Half-array decode (the round-trip API)
        try
        {
            var halves = JxrFileFormatter.LoadBd16FRgbNoFlexbitsAsHalf(bytes, out var w, out var h, out _);
            _out.WriteLine($"LoadBd16FRgbNoFlexbitsAsHalf OK — {w}×{h}, {halves.Length} samples");
        }
        catch (Exception ex)
        {
            _out.WriteLine($"LoadBd16FRgbNoFlexbitsAsHalf threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
