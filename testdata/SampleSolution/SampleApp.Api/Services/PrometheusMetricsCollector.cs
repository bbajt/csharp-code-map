namespace SampleApp.Api.Services;

/// <summary>Prometheus-based metrics collector implementation.</summary>
public class PrometheusMetricsCollector : IMetricsCollector
{
    public void Increment(string metricName, long value = 1)
    {
        // In a real implementation: prometheus_counter.WithLabels(metricName).Inc(value)
    }

    public void Record(string metricName, double value)
    {
        // In a real implementation: prometheus_histogram.WithLabels(metricName).Observe(value)
    }
}
