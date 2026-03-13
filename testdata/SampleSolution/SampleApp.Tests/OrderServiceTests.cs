namespace SampleApp.Tests;

using SampleApp.Models;
using Xunit;
using SampleApp.Repositories;
using SampleApp.Services;

public class OrderServiceTests
{
    [Fact]
    public async Task SubmitAsync_ValidCustomer_ReturnsSuccess()
    {
        var repo = new Repository<Order>();
        var svc = new OrderService(repo);

        var result = await svc.SubmitAsync("customer-123");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("customer-123", result.Value!.CustomerId);
    }

    [Fact]
    public async Task SubmitAsync_EmptyCustomerId_Throws()
    {
        var repo = new Repository<Order>();
        var svc = new OrderService(repo);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.SubmitAsync(string.Empty));
    }

    [Fact]
    public async Task CancelAsync_ExistingOrder_ChangesStatus()
    {
        var repo = new Repository<Order>();
        var svc = new OrderService(repo);

        var submitResult = await svc.SubmitAsync("customer-456");
        int orderId = submitResult.Value!.Id;

        await svc.CancelAsync(orderId);
        var order = await repo.FindByIdAsync(orderId);

        Assert.Equal(OrderStatus.Cancelled, order!.Status);
    }
}
