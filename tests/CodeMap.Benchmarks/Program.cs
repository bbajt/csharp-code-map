// BenchmarkDotNet entry point — run via:
//   dotnet run --project tests/CodeMap.Benchmarks -c Release -- --filter "QueryBenchmarks*"
//   dotnet run --project tests/CodeMap.Benchmarks -c Release -- --filter "ExtractionBenchmarks*"
//   dotnet run --project tests/CodeMap.Benchmarks -c Release -- --filter "*"
//
// Token savings xUnit tests — run via:
//   dotnet test --filter "Category=Benchmark" -v normal

namespace CodeMap.Benchmarks;

using BenchmarkDotNet.Running;

internal static class Program
{
    private static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
