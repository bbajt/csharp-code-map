namespace SampleApp.Api.Services;

using Microsoft.Extensions.Logging;

/// <summary>Sample class demonstrating ILogger structured log calls for fact extraction tests.</summary>
public class LoggingExample
{
    private readonly ILogger<LoggingExample> _logger;

    public LoggingExample(ILogger<LoggingExample> logger)
    {
        _logger = logger;
    }

    public void DoWork(int orderId)
    {
        _logger.LogInformation("Processing order {OrderId}", orderId);
        _logger.LogWarning("Order {OrderId} has no items", orderId);
    }

    public void HandleError(int orderId, Exception ex)
    {
        _logger.LogError(ex, "Failed to process order {OrderId}", orderId);
        _logger.LogCritical("Database connection lost");
    }

    public void Debug(string cacheKey)
    {
        _logger.LogDebug("Cache hit for key {Key}", cacheKey);
    }

    // AC-T01-08 — Log with explicit LogLevel + LogTrace for full severity coverage
    public void Retry(int attempt)
    {
        _logger.LogTrace("Beginning retry attempt {Attempt}", attempt);
        _logger.Log(LogLevel.Warning, "Retrying operation {Attempt}", attempt);
    }
}
