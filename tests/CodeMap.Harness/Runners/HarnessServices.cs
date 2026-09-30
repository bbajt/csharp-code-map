namespace CodeMap.Harness.Runners;

using CodeMap.Core.Interfaces;
using CodeMap.Git;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Roslyn.Extraction;
using CodeMap.Storage.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Builds the harness's in-process DI container for the v2 engine (moved out of <c>Program.cs</c> in
/// PHASE-21-12 T01 so tests can run <see cref="GoldenRunner"/> in-process).
/// </summary>
public static class HarnessServices
{
    /// <summary>
    /// Builds a complete DI container for the v2 custom engine with its store under
    /// <paramref name="baseDir"/> (never the production <c>~/.codemap</c>). <paramref name="sharedCacheDir"/>: the
    /// shared baseline cache, or <c>null</c> for none (tests always pass <c>null</c>).
    /// </summary>
    public static ServiceProvider Build(string baseDir, string? sharedCacheDir)
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

    /// <summary>Creates the <see cref="HarnessIndexer"/> for a container built by <see cref="Build"/>.</summary>
    public static HarnessIndexer CreateIndexer(IServiceProvider sp) =>
        new(sp.GetRequiredService<IGitService>(),
            sp.GetRequiredService<IRoslynCompiler>(),
            sp.GetRequiredService<ISymbolStore>(),
            sp.GetRequiredService<IBaselineCacheManager>());
}
