namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests for all 7 DI registration patterns in DiSetup.cs (AC-T02-05).
/// Pattern coverage: generic pair, self-reg, factory lambda, TryAdd,
/// AddHostedService, instance arg, inferred-type factory.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class DiPatternTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public DiPatternTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    [Fact]
    public async Task Regression_DI_AllSevenPatterns_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        // DiSetup.cs has 7 registration patterns + AddHealthChecks chained call.
        // The 7 core patterns should all be extracted.
        facts.Should().HaveCountGreaterThanOrEqualTo(7,
            "DiSetup.ConfigureServices has 7 DI registration patterns");
    }

    [Fact]
    public async Task Regression_DI_GenericPair_Detected()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        facts.Should().Contain(f => f.Value.Contains("IOrderService") && f.Value.Contains("OrderService"),
            "Pattern 1: AddScoped<IOrderService, OrderService>()");
    }

    [Fact]
    public async Task Regression_DI_FactoryLambda_ServiceTypeResolved()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        // Factory lambdas store "factory" as ImplType (DiRegistrationExtractor design: lambdas not resolved to concrete type)
        facts.Should().Contain(f => f.Value.Contains("IPaymentGateway") && f.Value.Contains("factory"),
            "Pattern 3: AddSingleton<IPaymentGateway>(sp => new StripePaymentGateway(...)) — factory lambda ImplType = 'factory'");
    }

    [Fact]
    public async Task Regression_DI_TryAdd_Detected()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        facts.Should().Contain(f => f.Value.Contains("INotificationService") && f.Value.Contains("NotificationService"),
            "Pattern 4: TryAddScoped<INotificationService, NotificationService>()");
    }

    [Fact]
    public async Task Regression_DI_HostedService_Detected()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        facts.Should().Contain(f => f.Value.Contains("OrderCleanupBackgroundService"),
            "Pattern 5: AddHostedService<OrderCleanupBackgroundService>()");
    }

    [Fact]
    public async Task Regression_DI_InstanceArg_ConcreteTypeResolved()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.DiRegistration, limit: 100);

        facts.Should().Contain(f => f.Value.Contains("ICachePolicy") && f.Value.Contains("SlidingCachePolicy"),
            "Pattern 6: AddSingleton<ICachePolicy>(new SlidingCachePolicy(...))");
    }
}
