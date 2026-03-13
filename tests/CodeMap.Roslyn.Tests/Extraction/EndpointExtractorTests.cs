namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class EndpointExtractorTests
{
    // Stub ASP.NET Core attributes for in-memory compilation.
    // EndpointExtractor checks attribute class names (strings), so stubs work perfectly.
    private const string AttributeStubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public class ApiControllerAttribute : System.Attribute {}

            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method)]
            public class RouteAttribute : System.Attribute
            {
                public RouteAttribute(string template) { }
            }

            public class ControllerBase {}

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpGetAttribute : System.Attribute
            {
                public HttpGetAttribute() {}
                public HttpGetAttribute(string template) {}
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPostAttribute : System.Attribute
            {
                public HttpPostAttribute() {}
                public HttpPostAttribute(string template) {}
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPutAttribute : System.Attribute
            {
                public HttpPutAttribute() {}
                public HttpPutAttribute(string template) {}
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpDeleteAttribute : System.Attribute
            {
                public HttpDeleteAttribute() {}
                public HttpDeleteAttribute(string template) {}
            }

            [System.AttributeUsage(System.AttributeTargets.Method)]
            public class HttpPatchAttribute : System.Attribute
            {
                public HttpPatchAttribute() {}
                public HttpPatchAttribute(string template) {}
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(
        string source,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var compilation = CompilationBuilder.Create(AttributeStubs, source);
        return EndpointExtractor.ExtractAll(compilation, "/repo/", stableIdMap);
    }

    // ── Controller-based endpoint tests ──────────────────────────────────────

    [Fact]
    public void Extract_ControllerWithHttpGet_ProducesRouteFact()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpGet]
                public object GetAll() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "GET /api/items");
    }

    [Fact]
    public void Extract_ControllerWithHttpPost_ProducesPostRoute()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class ItemsController : ControllerBase
            {
                [HttpPost]
                public object Create() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "POST /api/items");
    }

    [Fact]
    public void Extract_RouteTemplate_CombinedWithClassRoute()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")]
                public object GetById(int id) => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "GET /api/orders/{id}");
    }

    [Fact]
    public void Extract_ControllerToken_Resolved()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public object GetAll() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "GET /api/orders");
    }

    [Fact]
    public void Extract_MultipleEndpoints_ProducesMultipleFacts()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class ProductsController : ControllerBase
            {
                [HttpGet]       public object GetAll()        => null!;
                [HttpGet("{id}")] public object GetById(int id) => null!;
                [HttpPost]      public object Create()        => null!;
                [HttpDelete("{id}")] public object Delete(int id) => null!;
            }
            """;

        var facts = Extract(source);

        facts.Where(f => f.Kind == FactKind.Route).Should().HaveCount(4);
    }

    [Fact]
    public void Extract_NoHttpAttribute_Skipped()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class FooController : ControllerBase
            {
                public object NotAnEndpoint() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    // ── Minimal API tests ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_MinimalApi_MapGet_ProducesRouteFact()
    {
        const string source = """
            public class Program
            {
                public static void Main()
                {
                    var app = new FakeApp();
                    app.MapGet("/api/test", () => "hello");
                }
            }

            public class FakeApp
            {
                public void MapGet(string path, System.Func<string> handler) {}
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "GET /api/test");
    }

    [Fact]
    public void Extract_MinimalApi_MapPost_ProducesPostRoute()
    {
        const string source = """
            public class Program
            {
                public static void Main()
                {
                    var app = new FakeApp();
                    app.MapPost("/api/orders", () => "created");
                }
            }

            public class FakeApp
            {
                public void MapPost(string path, System.Func<string> handler) {}
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Route &&
            f.Value == "POST /api/orders");
    }

    [Fact]
    public void Extract_HandlerSymbol_PointsToMethod()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class TestController : ControllerBase
            {
                [HttpGet]
                public object Get() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle();
        facts[0].SymbolId.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Extract_StableId_Populated()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public object GetAll() => null!;
            }
            """;

        var compilation = CompilationBuilder.Create(AttributeStubs, source);
        var facts = EndpointExtractor.ExtractAll(compilation, "/repo/");

        // Get the symbolId of the extracted fact to build a stableIdMap
        facts.Should().ContainSingle();
        var symbolId = facts[0].SymbolId.Value;

        var expectedStable = new StableId("sym_" + new string('a', 16));
        var stableIdMap = new Dictionary<string, StableId> { [symbolId] = expectedStable };

        var factsWithStable = EndpointExtractor.ExtractAll(compilation, "/repo/", stableIdMap);

        factsWithStable.Should().ContainSingle(f =>
            f.StableId.HasValue &&
            f.StableId!.Value == expectedStable);
    }

    [Fact]
    public void Extract_Confidence_High_ForSemanticExtraction()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;

            namespace MyApp;

            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public object GetAll() => null!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f => f.Confidence == Confidence.High);
    }
}
