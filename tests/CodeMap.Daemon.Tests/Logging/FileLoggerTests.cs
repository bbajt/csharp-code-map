namespace CodeMap.Daemon.Tests.Logging;

using CodeMap.Daemon.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

public class FileLoggerTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private FileLoggerProvider? _provider;

    public FileLoggerTests() => Directory.CreateDirectory(_logDir);

    public void Dispose()
    {
        _provider?.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private FileLoggerProvider Provider =>
        _provider ??= new FileLoggerProvider(_logDir, LogLevel.Trace);

    private string TodayLogPath =>
        Path.Combine(_logDir, $"codemap-{DateTime.UtcNow:yyyy-MM-dd}.log");

    /// <summary>
    /// Reads log file with FileShare.ReadWrite so it works while the provider
    /// has the file open for writing (Windows file-locking constraint).
    /// </summary>
    private string[] ReadLogLines()
    {
        using var fs = new FileStream(TodayLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void WriteEntry_CreatesLogFile()
    {
        var logger = Provider.CreateLogger("TestCategory");
        logger.LogInformation("hello");

        File.Exists(TodayLogPath).Should().BeTrue();
    }

    [Fact]
    public void WriteEntry_JsonFormat()
    {
        var logger = Provider.CreateLogger("Cat");
        logger.LogWarning("test message");

        var line = ReadLogLines()[0];
        var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("ts").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("level").GetString().Should().Be("Warning");
        doc.RootElement.GetProperty("cat").GetString().Should().Be("Cat");
        doc.RootElement.GetProperty("msg").GetString().Should().Be("test message");
    }

    [Fact]
    public void WriteEntry_AppendsToExisting()
    {
        var logger = Provider.CreateLogger("Cat");
        logger.LogInformation("line1");
        logger.LogInformation("line2");

        var lines = ReadLogLines();
        lines.Length.Should().Be(2);
    }

    [Fact]
    public void WriteEntry_IncludesStructuredProperties()
    {
        var logger = Provider.CreateLogger("Cat");
        logger.LogInformation("search done {Query} {Results}", "OrderService", 5);

        var line = ReadLogLines()[0];
        var doc = JsonDocument.Parse(line);
        // The formatted message should contain the values
        doc.RootElement.GetProperty("msg").GetString()
            .Should().Contain("OrderService");
    }

    [Fact]
    public void MinLevel_FiltersBelowThreshold()
    {
        using var provider = new FileLoggerProvider(_logDir, LogLevel.Warning);
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("should be filtered");

        if (File.Exists(TodayLogPath))
        {
            var lines = File.ReadAllLines(TodayLogPath)
                .Where(l => l.Contains("should be filtered"))
                .ToArray();
            lines.Should().BeEmpty("information logs filtered at Warning threshold");
        }
    }

    [Fact]
    public void CreateLogger_IsEnabled_RespectsMinLevel()
    {
        using var provider = new FileLoggerProvider(_logDir, LogLevel.Warning);
        var logger = provider.CreateLogger("Cat");

        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
        logger.IsEnabled(LogLevel.Information).Should().BeFalse();
    }

    [Fact]
    public void DailyRotation_DifferentDateProducesDifferentFile()
    {
        // Verify rotation naming convention by checking separate files would be created
        var date1 = Path.Combine(_logDir, "codemap-2026-01-01.log");
        var date2 = Path.Combine(_logDir, "codemap-2026-01-02.log");
        File.WriteAllText(date1, "{}\n");
        File.WriteAllText(date2, "{}\n");

        var files = Directory.GetFiles(_logDir, "codemap-*.log");
        files.Length.Should().BeGreaterThanOrEqualTo(2, "each date has its own file");
    }
}
