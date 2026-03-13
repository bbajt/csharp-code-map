namespace SampleApp.Api.Services;

using SampleApp.Models;
using SampleApp.Services;
using SampleApp.Shared.SharedModels;

/// <summary>
/// Orchestrates cross-project order workflow from the API layer.
/// Depends on IOrderService and INotificationService (SampleApp) and PagedList (SampleApp.Shared).
/// AC-T01-05 — cross-project call chain: Api → App → Shared.
/// </summary>
public class OrderOrchestrator
{
    private readonly IOrderService _orderService;
    private readonly INotificationService _notifier;
    private readonly ILogger<OrderOrchestrator> _logger;

    public OrderOrchestrator(
        IOrderService orderService,
        INotificationService notifier,
        ILogger<OrderOrchestrator> logger)
    {
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Submits an order and sends a confirmation notification.</summary>
    public async Task<Result<OrderSummary>> ProcessOrderAsync(
        CreateOrderRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        _logger.LogInformation("Processing order for customer {CustomerId}", request.CustomerId);

        // Cross-project call: SampleApp.Api → SampleApp
        var result = await _orderService.SubmitAsync(request.CustomerId, ct);

        if (!result.IsSuccess || result.Value is null)
        {
            _logger.LogWarning("Order submission failed for customer {CustomerId}: {Error}",
                request.CustomerId, result.Error);
            return Result<OrderSummary>.Fail(result.Error ?? "Submission failed");
        }

        // Cross-project call: SampleApp.Api → SampleApp
        await _notifier.SendOrderConfirmationAsync(result.Value.Id, ct);

        _logger.LogInformation("Order {OrderId} submitted and confirmation sent", result.Value.Id);
        return Result<OrderSummary>.Ok(new OrderSummary(result.Value.Id, result.Value.Status));
    }

    /// <summary>Returns a paged list of pending requests (demonstrates PagedList from SampleApp.Shared).</summary>
    public Task<PagedList<CreateOrderRequest>> GetPendingRequestsAsync(CancellationToken ct = default)
    {
        // Cross-project reference to SampleApp.Shared
        var page = new PagedList<CreateOrderRequest>([], 0, 1, 20);
        return Task.FromResult(page);
    }
}

/// <summary>Request to create a new order.</summary>
public record CreateOrderRequest(string CustomerId, IReadOnlyList<string>? Items = null);

/// <summary>Summary of a submitted order returned to the API layer.</summary>
public record OrderSummary(int OrderId, OrderStatus Status);
