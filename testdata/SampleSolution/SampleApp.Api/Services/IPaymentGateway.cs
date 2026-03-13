namespace SampleApp.Api.Services;

/// <summary>Contract for external payment processing.</summary>
public interface IPaymentGateway
{
    /// <summary>Charges the specified amount for the given customer.</summary>
    Task<bool> ChargeAsync(string customerId, decimal amount, CancellationToken ct = default);

    /// <summary>Issues a refund for a previous charge.</summary>
    Task<bool> RefundAsync(string chargeId, CancellationToken ct = default);
}
