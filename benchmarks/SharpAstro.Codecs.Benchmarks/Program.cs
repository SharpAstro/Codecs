using BenchmarkDotNet.Running;

namespace SharpAstro.Codecs.Benchmarks;

internal static class Program
{
    // dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --filter '*'
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--profile-webp-lossy")
        {
            WebpProfileRunner.RunLossyOpaque();
            return;
        }

        // dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --webp-encode-report
        // (optional: --efforts=0,3,6,9  --only=name  --no-corpus  --no-1080p)
        if (args.Length > 0 && args[0] == "--webp-encode-report")
        {
            Environment.Exit(WebpEncodeReport.Run(args));
            return;
        }

        // dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --jpeg-scan-report
        if (args.Length > 0 && args[0] == "--jpeg-scan-report")
        {
            JpegScanReport.Run();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
