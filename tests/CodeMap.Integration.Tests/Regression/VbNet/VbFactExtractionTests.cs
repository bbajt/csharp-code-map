namespace CodeMap.Integration.Tests.Regression.VbNet;

using CodeMap.Core.Enums;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// VB.NET regression: all 8 FactKind extractors end-to-end.
/// Facts are read directly from the BaselineStore to avoid query-layer caching.
/// </summary>
[Trait("Category", "Integration")]
[Collection("VbRegression")]
public sealed class VbFactExtractionTests(IndexedSampleVbSolutionFixture fixture)
{
    // ── Route facts ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsRouteFactsFromOrdersController()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.Route, limit: 100);

        facts.Should().NotBeEmpty("OrdersController.vb has [HttpGet], [HttpPost], [HttpDelete]");
        facts.Should().Contain(f => f.Value.Contains("GET"),
            "GetOrder is decorated with [HttpGet(\"{id}\")]");
        facts.Should().Contain(f => f.Value.Contains("POST"),
            "SubmitOrder is decorated with [HttpPost]");
        facts.Should().Contain(f => f.Value.Contains("DELETE"),
            "CancelOrder is decorated with [HttpDelete(\"{id}\")]");
    }

    // ── DI registration facts ─────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsDiRegistrationFactsFromDiSetup()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.DiRegistration, limit: 100);

        facts.Should().NotBeEmpty("DiSetup.vb calls AddScoped + AddSingleton");
        facts.Should().Contain(f => f.Value.Contains("IOrderService"),
            "AddScoped(Of IOrderService, OrderService) registers IOrderService");
    }

    // ── Config key facts ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsConfigFactsFromConfigConsumer()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.Config, limit: 100);

        facts.Should().NotBeEmpty("ConfigConsumer.vb uses GetValue + GetSection");
        // GetValue<Integer>("App:MaxRetries") → "App:MaxRetries|GetValue"
        facts.Should().Contain(f => f.Value.Contains("App:MaxRetries"),
            "ConfigConsumer.GetMaxRetries calls GetValue<Integer>(\"App:MaxRetries\")");
        // GetSection("Features") → "Features|GetSection"
        facts.Should().Contain(f => f.Value.Contains("Features"),
            "ConfigConsumer.GetFeatureSection calls GetSection(\"Features\")");
    }

    // ── DbTable facts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsDbTableFactsFromAppDbContext()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.DbTable, limit: 100);

        facts.Should().NotBeEmpty(
            "AppDbContext.vb has DbSet(Of Order) and DbSet(Of Customer)");
        facts.Should().Contain(f => f.Value.Contains("DbSet<Order>"),
            "Public Property Orders As DbSet(Of Order)");
        facts.Should().Contain(f => f.Value.Contains("DbSet<Customer>"),
            "Public Property Customers As DbSet(Of Customer)");
    }

    // ── Log facts ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsLogFactsFromLoggingExample()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.Log, limit: 100);

        facts.Should().NotBeEmpty(
            "LoggingExample.vb has LogDebug + LogInformation + LogError");
        // VbLogExtractor value format: "Level|template"
        facts.Should().Contain(f => f.Value.StartsWith("Information"),
            "LoggingExample.ProcessAsync calls LogInformation");
        facts.Should().Contain(f => f.Value.StartsWith("Error"),
            "LoggingExample.ProcessAsync calls LogError in catch block");
    }

    // ── Exception facts ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsExceptionFactsFromLoggingExample()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.Exception, limit: 100);

        facts.Should().NotBeEmpty(
            "LoggingExample.vb throws InvalidOperationException");
        facts.Should().Contain(f => f.Value.Contains("InvalidOperationException"),
            "Throw New InvalidOperationException(\"Processing error\", ex)");
    }

    // ── Middleware facts ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExtractsMiddlewareFactsFromMiddlewareSetup()
    {
        var facts = await fixture.BaselineStore.GetFactsByKindAsync(
            fixture.RepoId, fixture.Sha, FactKind.Middleware, limit: 100);

        facts.Should().NotBeEmpty("MiddlewareSetup.vb registers UseRouting + UseAuthentication + UseAuthorization");

        var methodNames = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();
        methodNames.Should().Contain("UseRouting");
        methodNames.Should().Contain("UseAuthentication");
        methodNames.Should().Contain("UseAuthorization");
    }
}
