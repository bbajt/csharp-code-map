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

// ── Compare mode: needs two separate engine stacks ───────────────────────────
if (mode == "compare")
{
    var repos = KnownRepos.ForTierName(repoTier) ?? [KnownRepos.SampleSolution];

    var sqliteSp = BuildServiceProvider(baseDir, useSqlite: true);
    var customSp = BuildServiceProvider(baseDir, useSqlite: false);

    var sqliteEngine = sqliteSp.GetRequiredService<IQueryEngine>();
    var customEngine = customSp.GetRequiredService<IQueryEngine>();

    var sqliteIndexer = new HarnessIndexer(
        sqliteSp.GetRequiredService<IGitService>(),
        sqliteSp.GetRequiredService<IRoslynCompiler>(),
        sqliteSp.GetRequiredService<ISymbolStore>(),
        sqliteSp.GetRequiredService<IBaselineCacheManager>());

    var customIndexer = new HarnessIndexer(
        customSp.GetRequiredService<IGitService>(),
        customSp.GetRequiredService<IRoslynCompiler>(),
        customSp.GetRequiredService<ISymbolStore>(),
        customSp.GetRequiredService<IBaselineCacheManager>());

    var compareRunner = new CompareRunner(sqliteEngine, sqliteIndexer, customEngine, customIndexer);
    exitCode = await compareRunner.RunAsync(repos, reporter, cts.Token);
    return exitCode;
}

// ── Standard modes: single engine ────────────────────────────���───────────────
// Default is v2 custom engine. Set CODEMAP_ENGINE=sqlite for SQLite fallback.
var engineEnv = Environment.GetEnvironmentVariable("CODEMAP_ENGINE");
var useSqliteForStandard = string.Equals(engineEnv, "sqlite", StringComparison.OrdinalIgnoreCase);
var sp = BuildServiceProvider(baseDir, useSqliteForStandard);

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

// ── Helper: builds a complete DI container for the v2 engine ────────────────
static ServiceProvider BuildServiceProvider(string baseDir, bool useSqlite)
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
    Console.WriteLine("  compare      --repo micro|small|medium|large|all");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --output  console|json  (default: console)");
    Console.WriteLine("  --out-file <path>       write report to file");
    Console.WriteLine();
    Console.WriteLine("Compare mode indexes the same repo with both SQLite and v2 engines,");
    Console.WriteLine("runs every query through both, and reports any differences.");
}
