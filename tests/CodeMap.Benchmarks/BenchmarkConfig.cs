namespace CodeMap.Benchmarks;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

/// <summary>
/// Short-run BenchmarkDotNet config for CI:
/// 3 warmup + 10 iterations, P95 column, memory diagnoser, JSON export.
/// </summary>
public class CodeMapBenchmarkConfig : ManualConfig
{
    public CodeMapBenchmarkConfig()
    {
        AddJob(Job.ShortRun
            .WithWarmupCount(3)
            .WithIterationCount(10)
            .WithLaunchCount(1));

        AddColumn(StatisticColumn.P95);
        AddColumn(StatisticColumn.Median);
        AddExporter(JsonExporter.Default);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}

/// <summary>
/// Extraction config for slow benchmarks (Roslyn compilation, ~10-30s per iteration).
/// Fewer iterations to keep wall time reasonable.
/// </summary>
public class ExtractionBenchmarkConfig : ManualConfig
{
    public ExtractionBenchmarkConfig()
    {
        AddJob(Job.ShortRun
            .WithWarmupCount(1)
            .WithIterationCount(3)
            .WithLaunchCount(1));

        AddColumn(StatisticColumn.Median);
        AddExporter(JsonExporter.Default);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
