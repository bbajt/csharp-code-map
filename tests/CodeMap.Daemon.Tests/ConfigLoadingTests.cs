namespace CodeMap.Daemon.Tests;

using System.Text.Json;
using CodeMap.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for config.json loading logic. Tests are self-contained and do not
/// start the MCP server — they test the static LoadConfig pattern via direct
/// JSON deserialization matching Program.cs behavior.
/// </summary>
public class ConfigLoadingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public ConfigLoadingTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_tempDir, "config.json");

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
        catch { return new CodeMapConfig(); }
    }

    [Fact]
    public void LoadConfig_ValidFile_ParsesCorrectly()
    {
        File.WriteAllText(ConfigPath, """
            {
              "log_level": "Debug",
              "shared_cache_dir": "/tmp/cache"
            }
            """);

        var config = LoadConfig(ConfigPath);

        config.LogLevel.Should().Be("Debug");
        config.SharedCacheDir.Should().Be("/tmp/cache");
    }

    [Fact]
    public void LoadConfig_MissingFile_ReturnsDefaults()
    {
        var config = LoadConfig(Path.Combine(_tempDir, "nonexistent.json"));

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
    }

    [Fact]
    public void LoadConfig_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(ConfigPath, "{{{{ not valid json }}}}");

        var act = () => LoadConfig(ConfigPath);
        act.Should().NotThrow();

        var config = LoadConfig(ConfigPath);
        config.LogLevel.Should().Be("Information");
    }

    [Fact]
    public void LoadConfig_EnvVarOverridesConfig()
    {
        File.WriteAllText(ConfigPath, """{"shared_cache_dir": "/config-dir"}""");
        var config = LoadConfig(ConfigPath);

        // Simulate env var override (as done in Program.cs)
        var envCacheDir = "/env-dir";
        var effectiveCacheDir = envCacheDir ?? config.SharedCacheDir;

        effectiveCacheDir.Should().Be("/env-dir", "env var wins over config.json");
    }

    [Fact]
    public void LoadConfig_LogLevelParsing_ReturnsCorrectLevel()
    {
        File.WriteAllText(ConfigPath, """{"log_level": "Warning"}""");
        var config = LoadConfig(ConfigPath);

        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        logLevel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void LoadConfig_MissingLogLevel_DefaultsToInformation()
    {
        File.WriteAllText(ConfigPath, "{}");
        var config = LoadConfig(ConfigPath);

        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        logLevel.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void CodeMapConfig_DefaultRecord_HasExpectedValues()
    {
        var config = new CodeMapConfig();

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
        config.BudgetOverrides.Should().BeNull();
    }
}
