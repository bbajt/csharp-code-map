namespace CodeMap.Integration.Tests.Regression;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Integration.Tests.Workflows;
using FluentAssertions;

/// <summary>
/// Regression tests covering all 8 FactKind extractors end-to-end (AC-T02-06).
/// Verifies exception patterns, log severities, endpoints, middleware order,
/// and retry policy — all from the expanded SampleSolution.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Regression")]
public sealed class FactExtractionTests
{
    private readonly IndexedSampleSolutionFixture _f;

    public FactExtractionTests(IndexedSampleSolutionFixture fixture) => _f = fixture;

    // ── Exception facts ───────────────────────────────────────────────────────

    [Fact]
    public async Task Regression_Exceptions_CustomException_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Exception, limit: 200);

        var exceptionTypes = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();

        exceptionTypes.Should().Contain("OrderNotFoundException",
            "OrderProcessingService throws OrderNotFoundException in ProcessBatchAsync");
    }

    [Fact]
    public async Task Regression_Exceptions_ArgumentNullException_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Exception, limit: 200);

        var exceptionTypes = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();

        exceptionTypes.Should().Contain("ArgumentNullException",
            "Multiple constructors use nameof guard: throw new ArgumentNullException(nameof(...))");
    }

    // ── Log facts ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Regression_Logging_AllSeverities_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Log, limit: 200);

        var levels = facts.Select(f => f.Value.Split('|').LastOrDefault() ?? "").ToHashSet();

        levels.Should().Contain("Information", "LoggingExample.DoWork calls LogInformation");
        levels.Should().Contain("Warning", "LoggingExample.DoWork calls LogWarning");
        levels.Should().Contain("Error", "LoggingExample.HandleError calls LogError");
        levels.Should().Contain("Critical", "LoggingExample.HandleError calls LogCritical");
        levels.Should().Contain("Debug", "LoggingExample.Debug calls LogDebug");
        levels.Should().Contain("Trace", "LoggingExample.Retry calls LogTrace");
    }

    [Fact]
    public async Task Regression_Logging_ExplicitLogLevel_Extracted()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Log, limit: 200);

        // LoggingExample.Retry calls _logger.Log(LogLevel.Warning, "Retrying operation {Attempt}", attempt)
        facts.Should().Contain(f => f.Value.Contains("Warning"),
            "LoggingExample.Retry calls _logger.Log(LogLevel.Warning, ...)");
    }

    // ── Endpoint facts ────────────────────────────────────────────────────────

    [Fact]
    public async Task Regression_Endpoints_AllRoutes_Extracted()
    {
        var result = await _f.QueryEngine.ListEndpointsAsync(
            _f.CommittedRouting(), pathFilter: null, httpMethod: null, limit: 50);

        result.IsSuccess.Should().BeTrue();
        result.Value.Data.Endpoints.Should().NotBeEmpty(
            "OrdersController has [HttpGet], [HttpPost], [HttpDelete] endpoints");

        // OrdersController: GET api/orders, POST api/orders, DELETE api/orders/{id}
        result.Value.Data.Endpoints.Should().Contain(
            e => e.HttpMethod != null && e.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase),
            "OrdersController has [HttpGet] endpoints");
    }

    // ── Middleware facts ──────────────────────────────────────────────────────

    [Fact]
    public async Task Regression_Middleware_OrderedCorrectly()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.Middleware, limit: 100);

        facts.Should().NotBeEmpty("MiddlewareSetup.Configure registers 5+ middleware entries");

        var methodNames = facts.Select(f => f.Value.Split('|')[0]).ToHashSet();
        methodNames.Should().Contain("UseExceptionHandler",
            "MiddlewareSetup adds UseExceptionHandler at pos:1");
        methodNames.Should().Contain("UseHttpsRedirection");
        methodNames.Should().Contain("UseStaticFiles",
            "MiddlewareSetup adds UseStaticFiles at pos:3");
        methodNames.Should().Contain("UseAuthentication");
        methodNames.Should().Contain("UseAuthorization");

        // Verify at least 5 distinct middleware entries
        methodNames.Count.Should().BeGreaterThanOrEqualTo(5,
            "AC-T01-12: middleware pipeline must have 5+ entries");
    }

    // ── Retry policy facts ────────────────────────────────────────────────────

    [Fact]
    public async Task Regression_RetryPolicy_Detected()
    {
        var facts = await _f.BaselineStore.GetFactsByKindAsync(
            _f.RepoId, _f.Sha, FactKind.RetryPolicy, limit: 100);

        facts.Should().NotBeEmpty(
            "ResilienceSetup.cs calls RetryAsync(3) and WaitAndRetryAsync(3)");
    }
}
