namespace SampleApp.Services;

using SampleApp.Models;

/// <summary>Validation logic for OrderService (partial class).</summary>
public partial class OrderService
{
    private static void ValidateOrder(Order order)
    {
        if (order.CustomerId.Length > 100)
            throw new ArgumentException("Customer ID exceeds maximum length.");

        if (order.Total < 0)
            throw new InvalidOperationException("Order total cannot be negative.");
    }

    internal bool IsEligibleForDiscount(Order order)
    {
        return order.Items.Count >= 5 && order.Total > 100m;
    }
}
