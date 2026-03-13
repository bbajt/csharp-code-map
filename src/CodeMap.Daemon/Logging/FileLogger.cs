namespace CodeMap.Daemon.Logging;

using Microsoft.Extensions.Logging;

/// <summary>
/// ILogger implementation that delegates to <see cref="FileLoggerProvider"/>.
/// Extracts structured properties from the log state when available.
/// </summary>
internal sealed class FileLogger(
    FileLoggerProvider provider,
    string categoryName,
    LogLevel minLevel) : ILogger
{
    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var props = new Dictionary<string, object>();

        if (exception is not null)
            props["exception"] = exception.ToString();

        // Extract structured key-value pairs from the log state (structured logging)
        if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            foreach (var kv in structured)
                if (kv.Key != "{OriginalFormat}" && kv.Value is not null)
                    props[kv.Key] = kv.Value;

        provider.WriteEntry(categoryName, logLevel, message, props.Count > 0 ? props : null);
    }
}
