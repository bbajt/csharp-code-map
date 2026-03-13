namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class RetryPolicyExtractorTests
{
    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(string source)
    {
        var compilation = CompilationBuilder.Create(source);
        return RetryPolicyExtractor.ExtractAll(compilation, "/repo/");
    }

    [Fact]
    public void Extract_RetryAsync_ProducesRetryFact()
    {
        var source = """
            public class PolicyBuilder {
                public object RetryAsync(int retryCount) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    policy.RetryAsync(3);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.RetryPolicy);
        facts[0].Value.Should().Be("RetryAsync(3)|Polly");
    }

    [Fact]
    public void Extract_WaitAndRetryAsync_ProducesRetryFact()
    {
        var source = """
            public class PolicyBuilder {
                public object WaitAndRetryAsync(int retryCount, System.Func<int, System.TimeSpan> sleepProvider) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    policy.WaitAndRetryAsync(3, attempt => System.TimeSpan.FromSeconds(attempt));
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.RetryPolicy);
        facts[0].Value.Should().Contain("WaitAndRetryAsync(3)");
        facts[0].Value.Should().Contain("Polly");
    }

    [Fact]
    public void Extract_CircuitBreakerAsync_ProducesFact()
    {
        var source = """
            public class PolicyBuilder {
                public object CircuitBreakerAsync(int handledEventsAllowedBeforeBreaking, System.TimeSpan durationOfBreak) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    policy.CircuitBreakerAsync(5, System.TimeSpan.FromSeconds(30));
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("CircuitBreaker");
        facts[0].Value.Should().Contain("Polly");
    }

    [Fact]
    public void Extract_AddResilienceHandler_ProducesFact()
    {
        var source = """
            public class ResilienceBuilder {
                public object AddResilienceHandler(string name, System.Action<object> configure) => new();
            }
            public class Setup {
                public void Configure(ResilienceBuilder builder) {
                    builder.AddResilienceHandler("retry", b => { });
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("ResilienceHandler|Resilience");
    }

    [Fact]
    public void Extract_AddRetry_ProducesFact()
    {
        var source = """
            public class ResilienceBuilder {
                public object AddRetry(object options) => new();
            }
            public class Setup {
                public void Configure(ResilienceBuilder builder) {
                    builder.AddRetry(new object());
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("AddRetry");
        facts[0].Value.Should().Contain("Resilience");
    }

    [Fact]
    public void Extract_RetryAsync_WithoutLiteral_ProducesFactWithoutCount()
    {
        var source = """
            public class PolicyBuilder {
                public object RetryAsync(int retryCount) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    int count = 3;
                    policy.RetryAsync(count);  // variable, not literal
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        // No literal arg — falls back to method name only
        facts[0].Value.Should().Be("RetryAsync|Polly");
    }

    [Fact]
    public void Extract_MultipleRetryPolicies_ProducesMultipleFacts()
    {
        var source = """
            public class PolicyBuilder {
                public object RetryAsync(int retryCount) => new();
                public object WaitAndRetryAsync(int retryCount, System.Func<int, System.TimeSpan> sleepProvider) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    policy.RetryAsync(3);
                    policy.WaitAndRetryAsync(3, attempt => System.TimeSpan.FromSeconds(attempt));
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(2);
    }

    [Fact]
    public void Extract_ContainingSymbol_PointsToMethod()
    {
        var source = """
            public class PolicyBuilder {
                public object RetryAsync(int retryCount) => new();
            }
            public class Setup {
                public void ConfigureResilience(PolicyBuilder policy) {
                    policy.RetryAsync(3);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Value.Should().Contain("ConfigureResilience");
    }

    [Fact]
    public void Extract_Confidence_Medium()
    {
        var source = """
            public class PolicyBuilder {
                public object RetryAsync(int retryCount) => new();
            }
            public class Setup {
                public void Configure(PolicyBuilder policy) {
                    policy.RetryAsync(3);
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Confidence.Should().Be(Confidence.Medium);
    }
}
