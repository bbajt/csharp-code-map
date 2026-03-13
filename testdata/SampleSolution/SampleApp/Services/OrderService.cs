namespace SampleApp.Services;

using SampleApp.Models;
using SampleApp.Repositories;
using SampleApp.Shared.SharedModels;

/// <summary>Default implementation of <see cref="IOrderService"/>.</summary>
public partial class OrderService : IOrderService
{
    private readonly IRepository<Order> _repository;

    public event EventHandler<OrderStatusChangedEventArgs>? StatusChanged;

    public OrderService(IRepository<Order> repository)
    {
        _repository = repository;
    }

    public async Task<Result<Order>> SubmitAsync(string customerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerId))
            throw new ArgumentException("Customer ID cannot be empty.", nameof(customerId));

        var order = new Order { CustomerId = customerId, Status = OrderStatus.Pending };
        await _repository.SaveAsync(order, ct);

        StatusChanged?.Invoke(this, new OrderStatusChangedEventArgs(order.Id, OrderStatus.Pending));
        return Result<Order>.Ok(order);
    }

    public async Task<Order?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await _repository.FindByIdAsync(id, ct);
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var order = await _repository.FindByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Order {id} not found.");

        order.Cancel();
        await _repository.SaveAsync(order, ct);
        StatusChanged?.Invoke(this, new OrderStatusChangedEventArgs(id, OrderStatus.Cancelled));
    }
}
