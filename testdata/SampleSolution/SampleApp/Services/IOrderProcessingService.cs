namespace SampleApp.Services;

using SampleApp.Models;

/// <summary>Contract for batch order processing operations.</summary>
public interface IOrderProcessingService
{
    /// <summary>Processes a batch of orders and returns aggregated results.</summary>
    Task<OrderProcessingResult> ProcessBatchAsync(IReadOnlyList<Order> orders, CancellationToken ct = default);

    /// <summary>Determines whether an order is eligible for processing.</summary>
    bool IsEligibleForProcessing(Order order);
}
