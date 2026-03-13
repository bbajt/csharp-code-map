namespace SampleApp.Api.Services;

/// <summary>Contract for collecting application performance and usage metrics.</summary>
public interface IMetricsCollector
{
    /// <summary>Increments a named counter metric.</summary>
    void Increment(string metricName, long value = 1);

    /// <summary>Records a histogram observation (e.g., request latency in ms).</summary>
    void Record(string metricName, double value);
}
