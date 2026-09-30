namespace CodeMap.Daemon;

using System.Reflection;
using System.Text.Json;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Daemon.Logging;
using CodeMap.Mcp;
using CodeMap.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// CodeMap daemon entry point.
/// Startup sequence: --version check → config.json load → MSBuild init →
/// logging setup (stderr + file) → DI container build → MCP tool registration →
/// shutdown hook (token savings) → MCP stdio loop.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the CodeMap MCP server.
    /// Handles <c>--version</c> / <c>-v</c> flags, resolves the data root via
    /// <see cref="CodeMapHome"/> (exit code 2 on an invalid <c>CODEMAP_HOME</c>), loads <c>config.json</c>,
    /// then runs the MCP JSON-RPC server over stdin/stdout until EOF or Ctrl-C.
    /// </summary>
    internal static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            var version = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "1.0.0";
            Console.WriteLine($"codemap-mcp {version}");
            return;
        }

        // Resolve the data root: CODEMAP_HOME override, else ~/.codemap (ADR-039).
        // Logging isn't configured yet, so a bad value goes to stderr — never stdout,
        // which belongs to the JSON-RPC stream.
        var home = CodeMapHome.ResolveFromEnvironment();
        if (home.IsFailure)
        {
            await Console.Error.WriteLineAsync($"codemap-mcp: {home.Error.Message}");
            Environment.ExitCode = 2;
            return;
        }
        var codeMapDir = home.Value;

        // Load config.json (missing or corrupt → defaults; unknown keys are warned about once the
        // logger exists).
        var (config, unknownConfigKeys) = LoadConfig(Path.Combine(codeMapDir, "config.json"));

        // Shared cache: CODEMAP_CACHE_DIR wins whenever it is set, else config.json's shared_cache_dir
        // (ADR-059). Same rules for both (F11): blank = disabled; relative would resolve against the
        // client's working directory — usually the user's repo — so refuse it at startup.
        var cacheDir = CodeMapHome.ResolveCacheDirFromEnvironment(config.SharedCacheDir);
        if (cacheDir.IsFailure)
        {
            await Console.Error.WriteLineAsync($"codemap-mcp: {cacheDir.Error.Message}");
            Environment.ExitCode = 2;
            return;
        }

        // Resolve log level: config.json, then default
        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        // MSBuild registration MUST happen before any Roslyn workspace use.
        MsBuildInitializer.EnsureRegistered();

        var builder = Host.CreateDefaultBuilder(args);

        // Logging: stderr (real-time) + structured JSON file (persistent)
        var logsDir = Path.Combine(codeMapDir, "logs");
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(logLevel);
            logging.AddConsole(options =>
                options.LogToStandardErrorThreshold = LogLevel.Trace);
            logging.AddProvider(new FileLoggerProvider(logsDir, logLevel));
        });

        builder.ConfigureServices(services =>
            services.AddCodeMapServices(baseDir: codeMapDir, sharedCacheDir: cacheDir.Value));

        var host = builder.Build();

        if (unknownConfigKeys.Count > 0)
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CodeMap.Daemon.Config").LogWarning(
                "config.json: ignoring unknown key(s) {Keys}. Known keys: log_level, shared_cache_dir",
                string.Join(", ", unknownConfigKeys));

        // Register all MCP tools after DI container is ready
        ServiceRegistration.RegisterMcpTools(host.Services);

        // Return idle memory after heavy work (PHASE-21-12, ADR-061).
        await using var reclaimer = host.Services.GetRequiredService<IdleMemoryReclaimer>();
        reclaimer.Start();

        // Save token savings on process exit (graceful shutdown only)
        if (host.Services.GetRequiredService<ITokenSavingsTracker>() is CodeMap.Query.TokenSavingsTracker tracker)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => tracker.SaveToDisk();

        // Run MCP server over stdin/stdout until the client disconnects
        var mcpServer = host.Services.GetRequiredService<McpServer>();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await mcpServer.RunAsync(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            cts.Token);
    }

    /// <summary>
    /// Loads <c>config.json</c> from <paramref name="configPath"/>. Returns defaults if the file is missing or
    /// contains invalid JSON. Also returns the top-level keys the daemon does not know (typos, or the
    /// removed <c>budget_overrides</c>), in file order, so startup can log one warning (ADR-059).
    /// Keys match case-insensitively, as the deserializer does.
    /// </summary>
    internal static (CodeMapConfig Config, IReadOnlyList<string> UnknownKeys) LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return (new CodeMapConfig(), []);

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<CodeMapConfig>(json, ConfigJsonOptions) ?? new CodeMapConfig();

            using var doc = JsonDocument.Parse(json);
            var unknown = doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject()
                    .Select(p => p.Name)
                    .Where(name => !KnownConfigKeys.Contains(name))
                    .ToList()
                : [];
            return (config, unknown);
        }
        catch
        {
            // Corrupt config — use defaults
            return (new CodeMapConfig(), []);
        }
    }

    /// <summary>The <c>config.json</c> keys <see cref="CodeMapConfig"/> binds (snake_case, case-insensitive).</summary>
    private static readonly HashSet<string> KnownConfigKeys =
        new(["log_level", CodeMapHome.CacheConfigKey], StringComparer.OrdinalIgnoreCase);

    /// <summary>Deserializer options for <c>config.json</c>: snake_case names, case-insensitive.</summary>
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
