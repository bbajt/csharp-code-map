namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class MiddlewareExtractorTests
{
    // Minimal ASP.NET Core stubs for in-memory compilation.
    private const string AppBuilderStubs = """
        namespace Microsoft.AspNetCore.Builder
        {
            public sealed class WebApplication : IApplicationBuilder
            {
                public static WebApplicationBuilder CreateBuilder(string[] args) => new();
            }
            public interface IApplicationBuilder { }

            public static class WebApplicationExtensions
            {
                public static WebApplication UseHttpsRedirection(this WebApplication app) => app;
                public static WebApplication UseAuthentication(this WebApplication app) => app;
                public static WebApplication UseAuthorization(this WebApplication app) => app;
                public static WebApplication UseRateLimiter(this WebApplication app) => app;
                public static WebApplication UseCors(this WebApplication app) => app;
                public static WebApplication MapControllers(this WebApplication app) => app;
                public static WebApplication MapRazorPages(this WebApplication app) => app;
                public static WebApplication MapGet(this WebApplication app, string pattern, System.Delegate handler) => app;
                public static WebApplication MapPost(this WebApplication app, string pattern, System.Delegate handler) => app;
                public static WebApplication MapPut(this WebApplication app, string pattern, System.Delegate handler) => app;
                public static WebApplication MapDelete(this WebApplication app, string pattern, System.Delegate handler) => app;
                public static WebApplication MapPatch(this WebApplication app, string pattern, System.Delegate handler) => app;
            }

            public sealed class WebApplicationBuilder
            {
                public WebApplication Build() => new();
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(
        string source,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var compilation = CompilationBuilder.Create(AppBuilderStubs, source);
        return MiddlewareExtractor.ExtractAll(compilation, "/repo/", stableIdMap);
    }

    [Fact]
    public void Extract_UseAuthentication_ProducesMiddlewareFact()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.UseAuthentication();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Middleware);
        facts[0].Value.Should().Be("UseAuthentication|pos:1");
    }

    [Fact]
    public void Extract_MultipleUse_PositionsAreSequential()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.UseHttpsRedirection();
                    app.UseAuthentication();
                    app.UseAuthorization();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(3);
        facts[0].Value.Should().Be("UseHttpsRedirection|pos:1");
        facts[1].Value.Should().Be("UseAuthentication|pos:2");
        facts[2].Value.Should().Be("UseAuthorization|pos:3");
    }

    [Fact]
    public void Extract_MapControllers_MarkedAsTerminal()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.MapControllers();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("terminal");
        facts[0].Value.Should().Be("MapControllers|pos:1|terminal");
    }

    [Fact]
    public void Extract_MapGet_SkippedAsEndpoint()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.MapGet("/test", () => "hello");
                }
            }
            """;

        var facts = Extract(source);

        // MapGet is handled by EndpointExtractor — MiddlewareExtractor skips it
        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MapPost_MapPut_MapDelete_MapPatch_Skipped()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.MapPost("/orders", () => "ok");
                    app.MapPut("/orders/1", () => "ok");
                    app.MapDelete("/orders/1", () => "ok");
                    app.MapPatch("/orders/1", () => "ok");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_NonAppBuilderReceiver_Ignored()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class OtherService {
                public void UseAuthentication() { }
            }
            public class Setup {
                public void Configure() {
                    var other = new OtherService();
                    other.UseAuthentication();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MultipleMethodBodies_PositionsResetPerMethod()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void ConfigureFirst(WebApplication app) {
                    app.UseHttpsRedirection();
                    app.UseAuthentication();
                    app.UseAuthorization();
                }
                public void ConfigureSecond(WebApplication app) {
                    app.UseCors();
                    app.UseRateLimiter();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(5);

        var firstMethod = facts.Where(f => f.Value.StartsWith("Use") && !f.Value.Contains("Cors") && !f.Value.Contains("Limiter")).ToList();
        var secondMethod = facts.Where(f => f.Value.StartsWith("UseCors") || f.Value.StartsWith("UseRateLimiter")).ToList();

        // First method: pos:1, pos:2, pos:3
        facts[0].Value.Should().Be("UseHttpsRedirection|pos:1");
        facts[1].Value.Should().Be("UseAuthentication|pos:2");
        facts[2].Value.Should().Be("UseAuthorization|pos:3");
        // Second method: pos:1, pos:2 (resets)
        facts[3].Value.Should().Be("UseCors|pos:1");
        facts[4].Value.Should().Be("UseRateLimiter|pos:2");
    }

    [Fact]
    public void Extract_ContainingSymbol_PointsToMethod()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.UseAuthentication();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Value.Should().Contain("Configure");
    }

    [Fact]
    public void Extract_StableId_Populated()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.UseAuthentication();
                }
            }
            """;

        // Pre-compute what FQN the extractor will use
        var compilation = CompilationBuilder.Create(AppBuilderStubs, source);
        var rawFacts = MiddlewareExtractor.ExtractAll(compilation, "/repo/");
        rawFacts.Should().HaveCount(1);

        var symIdStr = rawFacts[0].SymbolId.Value;
        var expectedStableId = new StableId("sym_aabbccdd11223344");
        var stableIdMap = new Dictionary<string, StableId> { [symIdStr] = expectedStableId };

        var facts = MiddlewareExtractor.ExtractAll(compilation, "/repo/", stableIdMap);

        facts.Should().HaveCount(1);
        facts[0].StableId.Should().Be(expectedStableId);
    }

    [Fact]
    public void Extract_Confidence_High()
    {
        var source = """
            using Microsoft.AspNetCore.Builder;
            public class Setup {
                public void Configure(WebApplication app) {
                    app.UseAuthentication();
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Confidence.Should().Be(Confidence.High);
    }
}
