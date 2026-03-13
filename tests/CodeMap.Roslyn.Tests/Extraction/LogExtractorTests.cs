namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class LogExtractorTests
{
    // Stub ILogger<T> for in-memory compilation (no real package reference needed)
    private const string LoggerStub = """
        namespace Microsoft.Extensions.Logging {
            public interface ILogger<T> {
                void Log(LogLevel logLevel, string message, params object[] args);
                void LogTrace(string message, params object[] args);
                void LogDebug(string message, params object[] args);
                void LogInformation(string message, params object[] args);
                void LogWarning(string message, params object[] args);
                void LogError(string message, params object[] args);
                void LogError(System.Exception ex, string message, params object[] args);
                void LogCritical(string message, params object[] args);
                void LogCritical(System.Exception ex, string message, params object[] args);
            }
            public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical, None }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(string source)
    {
        var compilation = CompilationBuilder.Create(LoggerStub, source);
        return LogExtractor.ExtractAll(compilation, "/repo/");
    }

    [Fact]
    public void Extract_LogInformation_ProducesLogFact()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void Create(int id) {
                    _logger.LogInformation("Order {Id} created", id);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Log);
        facts[0].Value.Should().Be("Order {Id} created|Information");
    }

    [Fact]
    public void Extract_LogWarning_ProducesWarningLevel()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void Check(int id) {
                    _logger.LogWarning("Order {Id} has no items", id);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Order {Id} has no items|Warning");
    }

    [Fact]
    public void Extract_LogError_WithException_ExtractsMessage()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void Handle(System.Exception ex, int id) {
                    _logger.LogError(ex, "Failed for {Id}", id);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Failed for {Id}|Error");
    }

    [Fact]
    public void Extract_LogDebug_ProducesDebugLevel()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class CacheService {
                private readonly ILogger<CacheService> _logger;
                public CacheService(ILogger<CacheService> logger) { _logger = logger; }
                public void Get(string key) {
                    _logger.LogDebug("Cache hit for key {Key}", key);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Cache hit for key {Key}|Debug");
    }

    [Fact]
    public void Extract_LogCritical_ProducesCriticalLevel()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class DbService {
                private readonly ILogger<DbService> _logger;
                public DbService(ILogger<DbService> logger) { _logger = logger; }
                public void Connect() {
                    _logger.LogCritical("Database connection lost");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Database connection lost|Critical");
    }

    [Fact]
    public void Extract_Log_WithExplicitLevel_ExtractsLevel()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void Retry(int attempt) {
                    _logger.Log(LogLevel.Warning, "Retrying operation {Attempt}", attempt);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Retrying operation {Attempt}|Warning");
    }

    [Fact]
    public void Extract_NonLoggerReceiver_Ignored()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class SomeService {
                public void LogInformation(string msg, int id) { }
                public void DoWork(int id) {
                    LogInformation("msg {Id}", id);  // not on ILogger
                }
            }
            """;

        // No ILogger field — the call is on 'this' which is SomeService, not ILogger
        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DynamicMessage_Skipped()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                private string GetMessage() => "dynamic";
                public void DoWork() {
                    _logger.LogInformation(GetMessage());  // dynamic — can't extract template
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MultipleLogCalls_ProducesMultipleFacts()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void Process(int id) {
                    _logger.LogInformation("Starting {Id}", id);
                    _logger.LogWarning("Slow path {Id}", id);
                    _logger.LogDebug("Done {Id}", id);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(3);
    }

    [Fact]
    public void Extract_ContainingSymbol_PointsToMethod()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void ProcessOrder(int id) {
                    _logger.LogInformation("Processing {Id}", id);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Value.Should().Contain("ProcessOrder");
    }

    [Fact]
    public void Extract_Confidence_High()
    {
        var source = """
            using Microsoft.Extensions.Logging;
            public class OrderService {
                private readonly ILogger<OrderService> _logger;
                public OrderService(ILogger<OrderService> logger) { _logger = logger; }
                public void DoWork() {
                    _logger.LogInformation("Done");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Confidence.Should().Be(Confidence.High);
    }
}
