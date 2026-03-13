namespace SampleApp.Services;

/// <summary>Contract for sending order-related notifications to customers.</summary>
public interface INotificationService
{
    /// <summary>Sends an order confirmation notification to the customer.</summary>
    Task SendOrderConfirmationAsync(int orderId, CancellationToken ct = default);

    /// <summary>Sends an order cancellation notification to the customer.</summary>
    Task SendOrderCancelledAsync(int orderId, CancellationToken ct = default);

    /// <summary>Sends a shipping update notification.</summary>
    Task SendShippingUpdateAsync(int orderId, string trackingNumber, CancellationToken ct = default);
}
