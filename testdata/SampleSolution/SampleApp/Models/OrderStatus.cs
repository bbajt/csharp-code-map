namespace SampleApp.Models;

/// <summary>Represents the lifecycle state of an order.</summary>
public enum OrderStatus
{
    Pending,
    Processing,
    Shipped,
    Delivered,
    Cancelled
}
