namespace SampleApp.Services;

using System.Diagnostics;
using SampleApp.Models;

/// <summary>Default batch order processing implementation.</summary>
public class OrderProcessingService : IOrderProcessingService
{
    private readonly IOrderService _orderService;

    public OrderProcessingService(IOrderService orderService)
    {
        // nameof guard — AC-T01-07 exception pattern 2
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
    }

    public async Task<OrderProcessingResult> ProcessBatchAsync(
        IReadOnlyList<Order> orders, CancellationToken ct = default)
    {
        if (orders is null)
            throw new ArgumentNullException(nameof(orders));

        var sw = Stopwatch.StartNew();
        int processed = 0, failed = 0;

        foreach (var order in orders)
        {
            try
            {
                // throw expression (null coalescing) — AC-T01-07 exception pattern 3
                var existing = await _orderService.GetByIdAsync(order.Id, ct)
                    ?? throw new OrderNotFoundException(order.Id);

                if (!IsEligibleForProcessing(existing))
                {
                    failed++;
                    continue;
                }

                processed++;
            }
            catch (OrderNotFoundException)
            {
                failed++;
                // bare re-throw in catch — AC-T01-07 exception pattern 4
                throw;
            }
        }

        sw.Stop();
        return new OrderProcessingResult(processed, failed, sw.Elapsed);
    }

    public bool IsEligibleForProcessing(Order order)
        => order.Status is OrderStatus.Pending or OrderStatus.Processing;
}
