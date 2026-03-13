namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class ConfigKeyExtractorTests
{
    // Stub IConfiguration interfaces for in-memory compilation.
    // ConfigKeyExtractor checks type names via GetTypeInfo(), so stubs work perfectly.
    private const string ConfigStubs = """
        namespace Microsoft.Extensions.Configuration
        {
            public interface IConfiguration
            {
                string? this[string key] { get; set; }
                IConfigurationSection GetSection(string key);
                T GetValue<T>(string key);
                T GetValue<T>(string key, T defaultValue);
            }

            public interface IConfigurationSection : IConfiguration
            {
                string? Value { get; set; }
            }
        }

        namespace TestSupport
        {
            public class ServiceBuilder
            {
                public void Configure<T>(Microsoft.Extensions.Configuration.IConfigurationSection section) { }
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(
        string source,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var compilation = CompilationBuilder.Create(ConfigStubs, source);
        return ConfigKeyExtractor.ExtractAll(compilation, "/repo/", stableIdMap);
    }

    // ── Pattern 1: IConfiguration indexer ────────────────────────────────────

    [Fact]
    public void Extract_ConfigIndexer_ProducesConfigFact()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class OrderService
            {
                private readonly IConfiguration _config;
                public OrderService(IConfiguration config) { _config = config; }

                public string GetConnectionString() =>
                    _config["ConnectionStrings:DefaultDB"]!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Config &&
            f.Value == "ConnectionStrings:DefaultDB|IConfiguration indexer");
    }

    // ── Pattern 2: GetValue<T> ────────────────────────────────────────────────

    [Fact]
    public void Extract_GetValue_ProducesConfigFact()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class RetryService
            {
                private readonly IConfiguration _config;
                public RetryService(IConfiguration config) { _config = config; }

                public int GetMaxRetries() =>
                    _config.GetValue<int>("App:MaxRetries");
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Config &&
            f.Value == "App:MaxRetries|GetValue");
    }

    // ── Pattern 3: GetSection ─────────────────────────────────────────────────

    [Fact]
    public void Extract_GetSection_ProducesConfigFact()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class LogService
            {
                private readonly IConfiguration _config;
                public LogService(IConfiguration config) { _config = config; }

                public string? GetLogLevel() =>
                    _config.GetSection("Logging:LogLevel:Default").Value;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Config &&
            f.Value == "Logging:LogLevel:Default|GetSection");
    }

    // ── Pattern 4: Configure<T>(GetSection("key")) ────────────────────────────

    [Fact]
    public void Extract_Configure_WithGetSection_ProducesConfigFact()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;
            using TestSupport;

            namespace TestApp;

            public class SmtpSettings { }

            public class Startup
            {
                private readonly IConfiguration _config;
                public Startup(IConfiguration config) { _config = config; }

                public void Setup(ServiceBuilder builder)
                {
                    builder.Configure<SmtpSettings>(_config.GetSection("Smtp"));
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f =>
            f.Kind == FactKind.Config &&
            f.Value == "Smtp|Options Configure");
    }

    // ── Multiple keys ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_MultipleKeys_ProducesMultipleFacts()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class AppService
            {
                private readonly IConfiguration _config;
                public AppService(IConfiguration config) { _config = config; }

                public string GetDb()     => _config["ConnectionStrings:DB"]!;
                public int    GetRetries() => _config.GetValue<int>("App:Retries");
                public string? GetLevel() => _config.GetSection("Logging").Value;
            }
            """;

        var facts = Extract(source);

        facts.Where(f => f.Kind == FactKind.Config).Should().HaveCount(3);
    }

    // ── Non-config indexer is ignored ─────────────────────────────────────────

    [Fact]
    public void Extract_NonConfigIndexer_Ignored()
    {
        const string source = """
            using System.Collections.Generic;

            namespace TestApp;

            public class DictService
            {
                private readonly Dictionary<string, string> _dict = new();

                public string? Get(string key) => _dict[key];
            }
            """;

        var facts = Extract(source);

        facts.Where(f => f.Kind == FactKind.Config).Should().BeEmpty();
    }

    // ── Dynamic (non-constant) keys are skipped ───────────────────────────────

    [Fact]
    public void Extract_DynamicKey_Ignored()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class DynamicService
            {
                private readonly IConfiguration _config;
                public DynamicService(IConfiguration config) { _config = config; }

                public string? Get(string keyName) => _config[keyName];
            }
            """;

        var facts = Extract(source);

        facts.Where(f => f.Kind == FactKind.Config).Should().BeEmpty();
    }

    // ── Handler symbol points to containing method ────────────────────────────

    [Fact]
    public void Extract_HandlerSymbol_PointsToContainingMethod()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class OrderService
            {
                private readonly IConfiguration _config;
                public OrderService(IConfiguration config) { _config = config; }

                public string GetDb() => _config["Db:Connection"]!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle();
        facts[0].SymbolId.Value.Should().NotBeNullOrEmpty();
        facts[0].SymbolId.Value.Should().Contain("GetDb");
    }

    // ── StableId populated from map ───────────────────────────────────────────

    [Fact]
    public void Extract_StableId_Populated()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class OrderService
            {
                private readonly IConfiguration _config;
                public OrderService(IConfiguration config) { _config = config; }

                public string GetDb() => _config["Db:Connection"]!;
            }
            """;

        var compilation = CompilationBuilder.Create(ConfigStubs, source);
        var facts = ConfigKeyExtractor.ExtractAll(compilation, "/repo/");

        facts.Should().ContainSingle();
        var symbolId = facts[0].SymbolId.Value;

        var expectedStable = new StableId("sym_" + new string('a', 16));
        var stableIdMap = new Dictionary<string, StableId> { [symbolId] = expectedStable };

        var factsWithStable = ConfigKeyExtractor.ExtractAll(compilation, "/repo/", stableIdMap);

        factsWithStable.Should().ContainSingle(f =>
            f.StableId.HasValue &&
            f.StableId!.Value == expectedStable);
    }

    // ── Confidence is High for semantic extraction ────────────────────────────

    [Fact]
    public void Extract_Confidence_High_ForSemanticExtraction()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;

            namespace TestApp;

            public class OrderService
            {
                private readonly IConfiguration _config;
                public OrderService(IConfiguration config) { _config = config; }

                public string GetDb() => _config["Db:Connection"]!;
            }
            """;

        var facts = Extract(source);

        facts.Should().ContainSingle(f => f.Confidence == Confidence.High);
    }

    // ── GetSection inside Configure<T> not double-counted ────────────────────

    [Fact]
    public void Extract_Configure_GetSection_NotDoubleExtracted()
    {
        const string source = """
            using Microsoft.Extensions.Configuration;
            using TestSupport;

            namespace TestApp;

            public class SmtpSettings { }

            public class Startup
            {
                private readonly IConfiguration _config;
                public Startup(IConfiguration config) { _config = config; }

                public void Setup(ServiceBuilder builder)
                {
                    builder.Configure<SmtpSettings>(_config.GetSection("Smtp"));
                }
            }
            """;

        var facts = Extract(source);

        // Should produce exactly 1 fact with "Options Configure", not 2 ("Options Configure" + "GetSection")
        facts.Where(f => f.Kind == FactKind.Config).Should().HaveCount(1);
        facts[0].Value.Should().Be("Smtp|Options Configure");
    }
}
