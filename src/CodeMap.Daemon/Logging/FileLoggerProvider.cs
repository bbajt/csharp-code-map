namespace CodeMap.Daemon.Logging;

using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Minimal ILoggerProvider that writes structured JSON lines to a daily-rotating
/// log file under the configured log directory. Thread-safe via a shared lock.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider, IDisposable
{
    private readonly string _logDir;
    private readonly LogLevel _minLevel;
    private StreamWriter? _writer;
    private string? _currentDate;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Creates a file logger provider that writes to <paramref name="logDir"/>.
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <param name="logDir">Directory where log files are written.</param>
    /// <param name="minLevel">Minimum log level to write (default: Information).</param>
    public FileLoggerProvider(string logDir, LogLevel minLevel = LogLevel.Information)
    {
        _logDir = logDir;
        _minLevel = minLevel;
        Directory.CreateDirectory(logDir);
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName)
        => new FileLogger(this, categoryName, _minLevel);

    /// <summary>
    /// Writes one JSON-line entry. Called from FileLogger — thread-safe.
    /// </summary>
    internal void WriteEntry(
        string categoryName,
        LogLevel level,
        string message,
        Dictionary<string, object>? properties = null)
    {
        lock (_lock)
        {
            if (_disposed) return;
            EnsureWriter();

            var entry = new Dictionary<string, object>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                ["level"] = level.ToString(),
                ["cat"] = categoryName,
                ["msg"] = message
            };

            if (properties is not null)
                foreach (var (k, v) in properties)
                    entry[k] = v;

            _writer!.WriteLine(JsonSerializer.Serialize(entry));
            _writer.Flush();
        }
    }

    private void EnsureWriter()
    {
        if (_disposed) return;
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (_currentDate == today && _writer is not null)
            return;

        _writer?.Dispose();
        var path = Path.Combine(_logDir, $"codemap-{today}.log");
        // FileShare.ReadWrite allows concurrent readers (e.g., tests, tail -f) on Windows
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = false };
        _currentDate = today;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
