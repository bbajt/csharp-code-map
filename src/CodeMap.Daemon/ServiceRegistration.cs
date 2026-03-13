namespace CodeMap.Daemon;

using CodeMap.Core.Interfaces;
using CodeMap.Git;
using CodeMap.Mcp;
using CodeMap.Mcp.Handlers;
using CodeMap.Query;
using CodeMap.Roslyn;
using CodeMap.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// DI composition root for CodeMap.
/// Registers all components in the correct dependency order.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>
    /// Registers all CodeMap services into the DI container.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="baseDir">
    /// Base directory for data storage. Use <c>~/.codemap</c> to resolve
    /// to the user's home directory at runtime.
    /// </param>
    /// <remarks>
    /// Registration order is significant:
    /// <list type="number">
    /// <item><b>Git</b> — IGitService singleton (stateless, no deps)</item>
    /// <item><b>Roslyn</b> — IRoslynCompiler + IResolutionWorker (MSBuildWorkspace, expensive, singleton)</item>
    /// <item><b>Storage</b> — BaselineDbFactory → BaselineStore (registered as both concrete and ISymbolStore per ADR-012)</item>
    /// <item><b>Cache</b> — IBaselineCacheManager (reads CODEMAP_CACHE_DIR env var; null = disabled)</item>
    /// <item><b>Overlay</b> — OverlayDbFactory → OverlayStore (registered as both concrete and IOverlayStore)</item>
    /// <item><b>IncrementalCompiler</b> — singleton to reuse cached MSBuildWorkspace across RefreshOverlay calls</item>
    /// <item><b>Query</b> — ICacheService + ITokenSavingsTracker (tracker loads savings from disk at startup)</item>
    /// <item><b>WorkspaceManager</b> — singleton registry; CreatedAt is in-memory only (lost on daemon restart)</item>
    /// <item><b>Support</b> — ExcerptReader, GraphTraverser, FeatureTracer (stateless singletons)</item>
    /// <item><b>QueryEngine</b> — concrete inner engine; MergedQueryEngine wraps it as IQueryEngine (decorator pattern)</item>
    /// <item><b>MCP</b> — ToolRegistry + McpServer + all 9 handler singletons</item>
    /// </list>
    /// Call <see cref="RegisterMcpTools"/> after building the container to bind handlers to the ToolRegistry.
    /// </remarks>
    public static IServiceCollection AddCodeMapServices(
        this IServiceCollection services,
        string baseDir = "~/.codemap")
    {
        var resolvedBaseDir = baseDir.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                baseDir[2..])
            : baseDir;

        // ── Git ───────────────────────────────────────────────────────────────
        services.AddSingleton<IGitService, GitService>();

        // ── Roslyn ────────────────────────────────────────────────────────────
        services.AddSingleton<IRoslynCompiler, RoslynCompiler>();
        services.AddSingleton<IResolutionWorker, ResolutionWorker>();

        // ── Storage ────────────────────────────────────────────────────────────
        // BaselineStore registered as both concrete type and ISymbolStore (ADR-012):
        // handlers that need repoRootPath inject ISymbolStore; DI provides BaselineStore.
        services.AddSingleton(sp => new BaselineDbFactory(
            Path.Combine(resolvedBaseDir, "baselines"),
            sp.GetRequiredService<ILogger<BaselineDbFactory>>()));
        services.AddSingleton<BaselineStore>();
        services.AddSingleton<ISymbolStore>(sp => sp.GetRequiredService<BaselineStore>());

        // ── Shared baseline cache ─────────────────────────────────────────────
        // CODEMAP_CACHE_DIR env var sets the shared cache directory (null = disabled).
        var sharedCacheDir = Environment.GetEnvironmentVariable("CODEMAP_CACHE_DIR");
        services.AddSingleton<IBaselineCacheManager>(sp =>
            new BaselineCacheManager(
                sp.GetRequiredService<BaselineDbFactory>(),
                sharedCacheDir,
                sp.GetRequiredService<ILogger<BaselineCacheManager>>()));

        // ── Overlay storage ───────────────────────────────────────────────────
        services.AddSingleton(sp => new OverlayDbFactory(
            Path.Combine(resolvedBaseDir, "overlays"),
            sp.GetRequiredService<ILogger<OverlayDbFactory>>()));
        services.AddSingleton<OverlayStore>();
        services.AddSingleton<IOverlayStore>(sp => sp.GetRequiredService<OverlayStore>());

        // ── Incremental compiler ──────────────────────────────────────────────
        services.AddSingleton<SymbolDiffer>();
        services.AddSingleton<IIncrementalCompiler, IncrementalCompiler>();

        // ── Query ─────────────────────────────────────────────────────────────
        services.AddSingleton<ICacheService, InMemoryCacheService>();
        // Pass codeMapDir so the tracker can persist totals across restarts.
        services.AddSingleton<ITokenSavingsTracker>(new TokenSavingsTracker(resolvedBaseDir));

        // ── Workspace manager ─────────────────────────────────────────────────
        services.AddSingleton<WorkspaceManager>();

        // ExcerptReader + GraphTraverser + FeatureTracer
        services.AddSingleton<ExcerptReader>();
        services.AddSingleton<GraphTraverser>();
        services.AddSingleton<FeatureTracer>();

        // QueryEngine as concrete inner engine; MergedQueryEngine as IQueryEngine
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

        // ── MCP server + handlers ─────────────────────────────────────────────
        services.AddMcpServer();
        services.AddSingleton<RepoStatusHandler>();
        services.AddSingleton<IBaselineScanner>(sp => sp.GetRequiredService<BaselineDbFactory>());
        services.AddSingleton<IndexHandler>(sp => new IndexHandler(
            sp.GetRequiredService<IGitService>(),
            sp.GetRequiredService<IRoslynCompiler>(),
            sp.GetRequiredService<ISymbolStore>(),
            sp.GetRequiredService<IBaselineCacheManager>(),
            sp.GetRequiredService<ILogger<IndexHandler>>(),
            sp.GetRequiredService<IBaselineScanner>(),
            sp.GetRequiredService<WorkspaceManager>()));
        services.AddSingleton<McpToolHandlers>();
        services.AddSingleton<WorkspaceHandler>();
        services.AddSingleton<OverlayRefreshHandler>();
        services.AddSingleton<RefsHandler>();
        services.AddSingleton<GraphHandler>();
        services.AddSingleton<TypeHierarchyHandler>();
        services.AddSingleton<SurfacesHandler>();
        services.AddSingleton<SummaryHandler>();
        services.AddSingleton<ExportHandler>();
        services.AddSingleton<DiffHandler>();
        services.AddSingleton<ContextHandler>();

        return services;
    }

    /// <summary>
    /// Registers all 25 MCP tools into the ToolRegistry.
    /// Must be called after the DI container is built.
    /// </summary>
    public static void RegisterMcpTools(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<ToolRegistry>();
        sp.GetRequiredService<RepoStatusHandler>().Register(registry);
        sp.GetRequiredService<IndexHandler>().Register(registry);
        sp.GetRequiredService<McpToolHandlers>().RegisterQueryTools(registry);
        sp.GetRequiredService<WorkspaceHandler>().Register(registry);
        sp.GetRequiredService<OverlayRefreshHandler>().Register(registry);
        sp.GetRequiredService<RefsHandler>().Register(registry);
        sp.GetRequiredService<GraphHandler>().Register(registry);
        sp.GetRequiredService<TypeHierarchyHandler>().Register(registry);
        sp.GetRequiredService<SurfacesHandler>().Register(registry);
        sp.GetRequiredService<SummaryHandler>().Register(registry);
        sp.GetRequiredService<ExportHandler>().Register(registry);
        sp.GetRequiredService<DiffHandler>().Register(registry);
        sp.GetRequiredService<ContextHandler>().Register(registry);
    }
}
