namespace SampleApp.Models;

/// <summary>Thrown when an order cannot be found by its identifier.</summary>
public class OrderNotFoundException : Exception
{
    /// <summary>The ID of the order that was not found.</summary>
    public int OrderId { get; }

    public OrderNotFoundException(int orderId)
        : base($"Order {orderId} was not found.")
    {
        OrderId = orderId;
    }

    public OrderNotFoundException(int orderId, string message)
        : base(message)
    {
        OrderId = orderId;
    }
}
