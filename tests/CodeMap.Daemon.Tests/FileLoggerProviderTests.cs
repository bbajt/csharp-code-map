namespace CodeMap.Daemon.Tests;

using CodeMap.Daemon.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tests for FileLoggerProvider dispose safety (PHASE-09-07).
/// </summary>
public class FileLoggerProviderTests : IDisposable
{
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public FileLoggerProviderTests() => Directory.CreateDirectory(_logDir);

    public void Dispose()
    {
        if (Directory.Exists(_logDir))
            Directory.Delete(_logDir, recursive: true);
    }

    [Fact]
    public void WriteEntry_AfterDispose_DoesNotReopenFile()
    {
        var provider = new FileLoggerProvider(_logDir);
        var logger = provider.CreateLogger("Test");

        // Log before dispose to create the file
        logger.LogInformation("before dispose");
        var sizeBefore = Directory.GetFiles(_logDir).Sum(f => new FileInfo(f).Length);

        provider.Dispose();

        // Log after dispose — must not reopen/append to the file
        logger.LogInformation("after dispose");
        var sizeAfter = Directory.GetFiles(_logDir).Sum(f => new FileInfo(f).Length);

        sizeAfter.Should().Be(sizeBefore, "no bytes should be written after Dispose");
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var provider = new FileLoggerProvider(_logDir);
        provider.CreateLogger("Test").LogInformation("hello");

        var act = () => { provider.Dispose(); provider.Dispose(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void WriteEntry_BeforeDispose_WritesToFile()
    {
        var provider = new FileLoggerProvider(_logDir);
        var logger = provider.CreateLogger("Test");

        logger.LogInformation("hello world");
        provider.Dispose();

        var files = Directory.GetFiles(_logDir, "*.log");
        files.Should().HaveCount(1);
        var content = File.ReadAllText(files[0]);
        content.Should().Contain("hello world");
    }
}
