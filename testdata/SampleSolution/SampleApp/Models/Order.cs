namespace SampleApp.Models;

/// <summary>Represents a customer order.</summary>
public class Order : AuditableEntity
{
    public string CustomerId { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
    public decimal Total { get; private set; }

    private readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

    public void AddItem(string item, decimal price)
    {
        _items.Add(item);
        Total += price;
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Shipped || Status == OrderStatus.Delivered)
            throw new InvalidOperationException("Cannot cancel a shipped or delivered order.");
        Status = OrderStatus.Cancelled;
    }
}
