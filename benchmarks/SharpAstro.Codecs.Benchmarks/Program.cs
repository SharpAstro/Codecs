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

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
