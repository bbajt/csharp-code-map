namespace SampleApp.Services;

using SampleApp.Models;
using SampleApp.Shared.SharedModels;

/// <summary>Contract for order management operations.</summary>
public interface IOrderService
{
    /// <summary>Submits a new order for the given customer.</summary>
    /// <param name="customerId">The customer placing the order.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Result<Order>> SubmitAsync(string customerId, CancellationToken ct = default);

    /// <summary>Retrieves an order by its identifier.</summary>
    Task<Order?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>Cancels an existing order.</summary>
    Task CancelAsync(int id, CancellationToken ct = default);

    /// <summary>Raised when an order status changes.</summary>
    event EventHandler<OrderStatusChangedEventArgs> StatusChanged;
}

/// <summary>Event arguments for order status change events.</summary>
public class OrderStatusChangedEventArgs : EventArgs
{
    public int OrderId { get; }
    public OrderStatus NewStatus { get; }

    public OrderStatusChangedEventArgs(int orderId, OrderStatus newStatus)
    {
        OrderId = orderId;
        NewStatus = newStatus;
    }
}

/// <summary>Delegate for order processing callbacks.</summary>
public delegate Task OrderProcessedDelegate(Order order);
