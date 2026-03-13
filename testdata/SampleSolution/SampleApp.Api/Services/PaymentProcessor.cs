namespace SampleApp.Api.Services;

using SampleApp.Models;
using SampleApp.Services;

/// <summary>
/// Handles payment charging for customer orders.
/// AC-T01-04 — interface call dispatch: calls IOrderService (interface-typed), not OrderService.
/// </summary>
public class PaymentProcessor
{
    private readonly IOrderService _service;
    private readonly IPaymentGateway _gateway;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        IOrderService service,
        IPaymentGateway gateway,
        ILogger<PaymentProcessor> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Charges the customer for the specified order.</summary>
    public async Task<bool> ChargeAsync(int orderId, CancellationToken ct = default)
    {
        // Interface-typed call — ref points to IOrderService.GetByIdAsync (not OrderService)
        var order = await _service.GetByIdAsync(orderId, ct)
            ?? throw new OrderNotFoundException(orderId);

        if (order.Total <= 0)
        {
            _logger.LogWarning("Order {OrderId} has zero or negative total; skipping charge", orderId);
            return false;
        }

        var success = await _gateway.ChargeAsync(order.CustomerId, order.Total, ct);
        if (success)
            _logger.LogInformation("Charged {Amount} for order {OrderId}", order.Total, orderId);
        else
            _logger.LogError("Payment failed for order {OrderId}", orderId);

        return success;
    }
}
