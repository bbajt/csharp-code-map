using CodeMap.Core.Interfaces;
using CodeMap.Git;
using CodeMap.Harness.Reports;
using CodeMap.Harness.Repos;
using CodeMap.Harness.Runners;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Roslyn.Extraction;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ── DI ────────────────────────────────────────────────────────────────────────

// Use isolated directory for harness data — never write to production ~/.codemap/
var baseDir = Environment.GetEnvironmentVariable("CODEMAP_HARNESS_DATA_DIR")
    ?? Path.Combine(Path.GetTempPath(), "codemap-harness");

// ── CLI parse (early — need mode before DI to decide if compare) ─────────────
var args2 = args.ToList();
var mode = args2.Count > 0 ? args2[0].ToLowerInvariant() : "";
var repoTier = GetArg(args2, "--repo") ?? "micro";
var outputFormat = GetArg(args2, "--output") ?? "console";
var outFile = GetArg(args2, "--out-file");
var force = args2.Contains("--force");
var confirm = args2.Contains("--confirm");

if (string.IsNullOrEmpty(mode))
{
    PrintUsage();
    return (int)HarnessExitCode.ConfigurationError;
}

IHarnessReporter reporter = outputFormat switch
{
    "json" => new JsonReporter(outFile),
    _ => new ConsoleReporter(),
};

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var goldenBaseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "golden");
goldenBaseDir = Path.GetFullPath(goldenBaseDir);

int exitCode;

// ── Compare mode: removed (SQLite engine removed in v2.1.0) ─────────────────
if (mode == "compare")
{
    Console.WriteLine("Compare mode is no longer available. SQLite engine was removed in v2.1.0.");
    Console.WriteLine("Use 'smoke' or 'golden check' to validate the v2 custom engine.");
    return (int)HarnessExitCode.ConfigurationError;
}

// ── Standard modes: v2 custom engine only ────────────────────────────────────
var sp = BuildServiceProvider(baseDir);

var engine = sp.GetRequiredService<IQueryEngine>();
var indexer = new HarnessIndexer(
    sp.GetRequiredService<IGitService>(),
    sp.GetRequiredService<IRoslynCompiler>(),
    sp.GetRequiredService<ISymbolStore>(),
    sp.GetRequiredService<IBaselineCacheManager>());

switch (mode)
{
    case "golden":
    {
        var subCmd = args2.Count > 1 ? args2[1].ToLowerInvariant() : "";
        var repos = KnownRepos.ForTierName(repoTier) ?? [KnownRepos.SampleSolution];
        var goldenRunner = new GoldenRunner(engine, indexer, goldenBaseDir);
        exitCode = (int)HarnessExitCode.Success;
        foreach (var repo in repos)
        {
            var code = subCmd == "save"
                ? await goldenRunner.SaveAsync(repo, force, confirm, reporter, cts.Token)
                : await goldenRunner.CheckAsync(repo, reporter, cts.Token);
            if (code != 0) exitCode = code;
        }
        break;
    }
    case "smoke":
    {
        var smokeRunner = new SmokeRunner(engine, indexer, goldenBaseDir);
        exitCode = await smokeRunner.RunAsync(reporter, cts.Token);
        break;
    }
    case "correctness":
    {
        var repos = KnownRepos.ForTierName(repoTier) ?? [KnownRepos.SampleSolution];
        var correctnessRunner = new CorrectnessRunner();
        exitCode = (int)HarnessExitCode.Success;
        foreach (var repo in repos)
        {
            var code = await correctnessRunner.RunAsync(repo, reporter, cts.Token);
            if (code != 0) exitCode = code;
        }
        break;
    }
    default:
        PrintUsage();
        exitCode = (int)HarnessExitCode.ConfigurationError;
        break;
}

return exitCode;

// ── Helper: builds a complete DI container for the v2 custom engine ──────────
static ServiceProvider BuildServiceProvider(string baseDir)
{
    var services = new ServiceCollection();
    services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));

    services.AddSingleton<IGitService, GitService>();
    services.AddSingleton<IRoslynCompiler, RoslynCompiler>();
    services.AddSingleton<IResolutionWorker, ResolutionWorker>();

    var storeDir = Path.Combine(baseDir, "store");
    var customStore = new CustomSymbolStore(storeDir);
    services.AddSingleton<ISymbolStore>(customStore);
    services.AddSingleton<IOverlayStore>(new CustomEngineOverlayStore(customStore, storeDir));

    var sharedCacheDir = Environment.GetEnvironmentVariable("CODEMAP_CACHE_DIR");
    services.AddSingleton<IBaselineCacheManager>(
        new EngineBaselineCacheManager(storeDir, sharedCacheDir));

    services.AddSingleton<SymbolDiffer>();
    services.AddSingleton<IncrementalCompiler>();
    services.AddSingleton<IIncrementalCompiler>(sp => sp.GetRequiredService<IncrementalCompiler>());
    services.AddSingleton<IMetadataResolver, MetadataResolver>();
    services.AddSingleton<ICacheService, InMemoryCacheService>();
    services.AddSingleton<ITokenSavingsTracker>(new TokenSavingsTracker(baseDir));
    services.AddSingleton<WorkspaceManager>();
    services.AddSingleton<ExcerptReader>();
    services.AddSingleton<GraphTraverser>(sp =>
        new GraphTraverser(sp.GetRequiredService<IMetadataResolver>()));
    services.AddSingleton<FeatureTracer>();
    services.AddSingleton<QueryEngine>();
    services.AddSingleton<IQueryEngine>(sp =>
        new MergedQueryEngine(
            sp.GetRequiredService<QueryEngine>(),
            sp.GetRequiredService<IOverlayStore>(),
            sp.GetRequiredService<WorkspaceManager>(),
            sp.GetRequiredService<ICacheService>(),
            sp.GetRequiredService<ITokenSavingsTracker>(),
            sp.GetRequiredService<ExcerptReader>(),
            sp.GetRequiredService<GraphTraverser>(),
            sp.GetRequiredService<ILogger<MergedQueryEngine>>()));

    return services.BuildServiceProvider();
}

static string? GetArg(List<string> args, string name)
{
    var idx = args.IndexOf(name);
    return idx >= 0 && idx + 1 < args.Count ? args[idx + 1] : null;
}

static void PrintUsage()
{
    Console.WriteLine("CodeMap Query Parity Harness");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  golden save  --repo micro|small|all [--force [--confirm]]");
    Console.WriteLine("  golden check --repo micro|small|all");
    Console.WriteLine("  smoke");
    Console.WriteLine("  correctness  --repo micro|small|medium|large");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --output  console|json  (default: console)");
    Console.WriteLine("  --out-file <path>       write report to file");
}
