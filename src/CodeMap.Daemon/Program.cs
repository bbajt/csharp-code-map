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
    /// Handles <c>--version</c> / <c>-v</c> flags, loads <c>~/.codemap/config.json</c>,
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

        // Resolve ~/.codemap directory
        var codeMapDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codemap");

        // Load config.json (missing or corrupt → defaults)
        var config = LoadConfig(Path.Combine(codeMapDir, "config.json"));

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
            services.AddCodeMapServices(baseDir: "~/.codemap"));

        var host = builder.Build();

        // Register all MCP tools after DI container is ready
        ServiceRegistration.RegisterMcpTools(host.Services);

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
    /// Loads <c>config.json</c> from <paramref name="configPath"/>.
    /// Returns defaults if the file is missing or contains invalid JSON.
    /// </summary>
    private static CodeMapConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return new CodeMapConfig();

        try
        {
            var json = File.ReadAllText(configPath);
            return JsonSerializer.Deserialize<CodeMapConfig>(json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                })
                ?? new CodeMapConfig();
        }
        catch
        {
            // Corrupt config — use defaults
            return new CodeMapConfig();
        }
    }
}
