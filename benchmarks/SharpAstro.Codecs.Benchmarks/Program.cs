using BenchmarkDotNet.Running;

namespace SharpAstro.Codecs.Benchmarks;

internal static class Program
{
    // dotnet run -c Release --project benchmarks/SharpAstro.Codecs.Benchmarks -- --filter '*'
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
