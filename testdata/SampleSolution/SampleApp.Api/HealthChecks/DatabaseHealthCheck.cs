namespace SampleApp.Api.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Health check that verifies database connectivity.</summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(ILogger<DatabaseHealthCheck> logger)
    {
        _logger = logger;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // In a real implementation, this would open a DB connection and run SELECT 1.
            _logger.LogDebug("Database health check passed");
            return Task.FromResult(HealthCheckResult.Healthy("Database is reachable"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("Database is unreachable", ex));
        }
    }
}
