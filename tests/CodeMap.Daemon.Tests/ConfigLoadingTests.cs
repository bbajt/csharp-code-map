namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for <c>config.json</c> loading (<see cref="Program.LoadConfig"/>): known keys, defaults on a
/// missing or corrupt file, and the unknown keys reported for the startup warning (PHASE-21-11 T02).
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

    [Fact]
    public void LoadConfig_ValidFile_ParsesCorrectly()
    {
        File.WriteAllText(ConfigPath, """
            {
              "log_level": "Debug",
              "shared_cache_dir": "/tmp/cache"
            }
            """);

        var (config, _) = Program.LoadConfig(ConfigPath);

        config.LogLevel.Should().Be("Debug");
        config.SharedCacheDir.Should().Be("/tmp/cache");
    }

    [Fact]
    public void LoadConfig_MissingFile_ReturnsDefaults()
    {
        var (config, unknown) = Program.LoadConfig(Path.Combine(_tempDir, "nonexistent.json"));

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
        unknown.Should().BeEmpty();
    }

    [Fact]
    public void LoadConfig_CorruptFile_ReturnsDefaults()
    {
        File.WriteAllText(ConfigPath, "{{{{ not valid json }}}}");

        var act = () => Program.LoadConfig(ConfigPath);
        act.Should().NotThrow();

        var (config, _) = Program.LoadConfig(ConfigPath);
        config.LogLevel.Should().Be("Information");
    }

    [Fact]
    public void LoadConfig_LogLevelParsing_ReturnsCorrectLevel()
    {
        File.WriteAllText(ConfigPath, """{"log_level": "Warning"}""");
        var (config, _) = Program.LoadConfig(ConfigPath);

        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        logLevel.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public void LoadConfig_MissingLogLevel_DefaultsToInformation()
    {
        File.WriteAllText(ConfigPath, "{}");
        var (config, _) = Program.LoadConfig(ConfigPath);

        var logLevel = Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Information;

        logLevel.Should().Be(LogLevel.Information);
    }

    [Fact]
    public void LoadConfig_BudgetOverridesKey_ReportedAsUnknown()
    {
        File.WriteAllText(ConfigPath, """
            {
              "log_level": "Debug",
              "budget_overrides": { "max_results": 500 }
            }
            """);

        var (config, unknown) = Program.LoadConfig(ConfigPath);

        config.LogLevel.Should().Be("Debug", "the known keys still load");
        unknown.Should().Equal("budget_overrides");
    }

    [Fact]
    public void LoadConfig_TypoKey_ReportedAsUnknown()
    {
        File.WriteAllText(ConfigPath, """{"log_levle": "Debug", "shared_cache": "/tmp/c"}""");

        var (config, unknown) = Program.LoadConfig(ConfigPath);

        config.LogLevel.Should().Be("Information", "a misspelt key is not applied");
        unknown.Should().Equal("log_levle", "shared_cache");
    }

    [Fact]
    public void LoadConfig_KnownKeysOnly_NoUnknown()
    {
        File.WriteAllText(ConfigPath, """{"log_level": "Debug", "SHARED_CACHE_DIR": "/tmp/c"}""");

        var (config, unknown) = Program.LoadConfig(ConfigPath);

        config.SharedCacheDir.Should().Be("/tmp/c", "keys match case-insensitively, as before");
        unknown.Should().BeEmpty();
    }

    [Fact]
    public void CodeMapConfig_DefaultRecord_HasExpectedValues()
    {
        var config = new CodeMapConfig();

        config.LogLevel.Should().Be("Information");
        config.SharedCacheDir.Should().BeNull();
    }
}
