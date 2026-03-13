namespace SampleApp.Api.Services;

/// <summary>Stripe-based payment gateway implementation.</summary>
public class StripePaymentGateway : IPaymentGateway
{
    private readonly string _apiKey;

    public StripePaymentGateway(IConfiguration configuration)
    {
        _apiKey = configuration["Stripe:ApiKey"] ?? string.Empty;
    }

    public Task<bool> ChargeAsync(string customerId, decimal amount, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrEmpty(_apiKey) && amount > 0);

    public Task<bool> RefundAsync(string chargeId, CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrEmpty(chargeId));
}
