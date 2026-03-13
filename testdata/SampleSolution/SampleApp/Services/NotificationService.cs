namespace SampleApp.Services;

/// <summary>Default implementation of <see cref="INotificationService"/>.</summary>
public class NotificationService : INotificationService
{
    public Task SendOrderConfirmationAsync(int orderId, CancellationToken ct = default)
    {
        // In a real implementation, this would send an email or push notification.
        return Task.CompletedTask;
    }

    public Task SendOrderCancelledAsync(int orderId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SendShippingUpdateAsync(int orderId, string trackingNumber, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
