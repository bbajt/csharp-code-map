namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class DiRegistrationExtractorTests
{
    // Minimal DI stubs for in-memory compilation.
    // Provides IServiceCollection interface + common Add* extension methods.
    private const string DiStubs = """
        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceCollection { }

            public static class ServiceCollectionExtensions
            {
                public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection s)
                    where TService : class => s;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection s,
                    System.Func<System.IServiceProvider, TService> factory)
                    where TService : class => s;
                public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s)
                    where TService : class => s;
                public static IServiceCollection AddTransient<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection AddTransient<TService>(this IServiceCollection s)
                    where TService : class => s;
                public static IServiceCollection TryAddScoped<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection TryAddSingleton<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection TryAddTransient<TService, TImpl>(this IServiceCollection s)
                    where TService : class where TImpl : class => s;
                public static IServiceCollection AddHostedService<TService>(this IServiceCollection s)
                    where TService : class => s;
                // Pattern 6: instance argument overloads
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s,
                    TService instance) where TService : class => s;
                public static IServiceCollection AddScoped<TService>(this IServiceCollection s,
                    TService instance) where TService : class => s;
                // Pattern 7: AddSingleton factory with inferred type (AddScoped factory already above)
                public static IServiceCollection AddSingleton<TService>(this IServiceCollection s,
                    System.Func<System.IServiceProvider, TService> factory) where TService : class => s;
            }
        }
        """;

    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(
        string source,
        IReadOnlyDictionary<string, StableId>? stableIdMap = null)
    {
        var compilation = CompilationBuilder.Create(DiStubs, source);
        return DiRegistrationExtractor.ExtractAll(compilation, "/repo/", stableIdMap);
    }

    // ── Pattern 1: Generic pair ───────────────────────────────────────────────

    [Fact]
    public void Extract_AddScoped_GenericPair_ProducesRegistrationFact()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped<IOrderService, OrderService>();
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("IOrderService \u2192 OrderService|Scoped");
    }

    [Fact]
    public void Extract_AddScoped_ValueContainsArrow()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped<IOrderService, OrderService>();
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            """;

        var facts = Extract(source);

        facts[0].Value.Should().Contain("\u2192");
        facts[0].Value.Should().Contain("Scoped");
    }

    // ── Pattern 2: Self-registration ─────────────────────────────────────────

    [Fact]
    public void Extract_AddSingleton_SelfRegistration_ProducesFact()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddSingleton<CacheService>();
                }
            }
            public class CacheService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("CacheService \u2192 CacheService|Singleton");
    }

    // ── Pattern: AddTransient ─────────────────────────────────────────────────

    [Fact]
    public void Extract_AddTransient_ProducesFact()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddTransient<IValidator, OrderValidator>();
                }
            }
            public interface IValidator {}
            public class OrderValidator : IValidator {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("Transient");
    }

    // ── Pattern 4: TryAdd variants ────────────────────────────────────────────

    [Fact]
    public void Extract_TryAddScoped_ProducesFact()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.TryAddScoped<IOrderService, OrderService>();
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("Scoped");
    }

    // ── Pattern 5: AddHostedService ──────────────────────────────────────────

    [Fact]
    public void Extract_AddHostedService_ProducesFactWithHostedTag()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddHostedService<BackgroundWorker>();
                }
            }
            public class BackgroundWorker {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("Singleton|HostedService");
    }

    // ── Pattern 3: Factory lambda ─────────────────────────────────────────────

    [Fact]
    public void Extract_FactoryRegistration_MarkedAsFactory()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped<IOrderService>(sp => new OrderService());
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Contain("factory");
    }

    // ── Non-IServiceCollection receiver ──────────────────────────────────────

    [Fact]
    public void Extract_NonServiceCollectionReceiver_Ignored()
    {
        var source = """
            public class FakeContainer {
                public void AddScoped<TService, TImpl>() where TService : class where TImpl : class {}
            }
            public class Startup {
                public void Configure(FakeContainer container) {
                    container.AddScoped<IFakeService, FakeService>();
                }
            }
            public interface IFakeService {}
            public class FakeService : IFakeService {}
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    // ── Multiple registrations ────────────────────────────────────────────────

    [Fact]
    public void Extract_MultipleRegistrations_ProducesMultipleFacts()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped<IOrderService, OrderService>();
                    services.AddSingleton<CacheService>();
                    services.AddTransient<IValidator, OrderValidator>();
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            public class CacheService {}
            public interface IValidator {}
            public class OrderValidator : IValidator {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(3);
    }

    // ── Containing symbol ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_ContainingSymbol_PointsToMethod()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            namespace App {
                public class Startup {
                    public void ConfigureServices(IServiceCollection services) {
                        services.AddScoped<IOrderService, OrderService>();
                    }
                }
                public interface IOrderService {}
                public class OrderService : IOrderService {}
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Value.Should().Contain("ConfigureServices");
    }

    // ── StableId ──────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_StableId_Populated()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            namespace App {
                public class Startup {
                    public void ConfigureServices(IServiceCollection services) {
                        services.AddScoped<IOrderService, OrderService>();
                    }
                }
                public interface IOrderService {}
                public class OrderService : IOrderService {}
            }
            """;

        var compilation = CompilationBuilder.Create(DiStubs, source);
        var allFacts = DiRegistrationExtractor.ExtractAll(compilation, "/repo/", null);

        // Build stableIdMap from the symbolId
        var symbolId = allFacts[0].SymbolId.Value;
        var expectedStable = new StableId("sym_" + new string('f', 16));
        var stableMap = new Dictionary<string, StableId> { [symbolId] = expectedStable };

        var facts = DiRegistrationExtractor.ExtractAll(compilation, "/repo/", stableMap);

        facts.Should().HaveCount(1);
        facts[0].StableId.Should().NotBeNull();
        facts[0].StableId!.Value.Should().Be(expectedStable);
    }

    // ── Confidence ────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_Confidence_High()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped<IOrderService, OrderService>();
                }
            }
            public interface IOrderService {}
            public class OrderService : IOrderService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.DiRegistration);
        facts[0].Confidence.Should().Be(Confidence.High);
    }

    // ── Pattern 6: Instance argument ─────────────────────────────────────────

    [Fact]
    public void Extract_InstanceArg_ResolvesConcreteType()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddSingleton<IFooService>(new FooService());
                }
            }
            public interface IFooService {}
            public class FooService : IFooService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("IFooService \u2192 FooService|Singleton",
            "concrete type should be resolved from the instance argument, not the service interface");
    }

    [Fact]
    public void Extract_InstanceArg_WithCtorParams_ResolvesConcreteType()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services, string dir) {
                    services.AddSingleton<ILogger>(new ConsoleLogger(dir));
                }
            }
            public interface ILogger {}
            public class ConsoleLogger : ILogger {
                public ConsoleLogger(string dir) {}
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("ILogger \u2192 ConsoleLogger|Singleton",
            "constructor arguments must not prevent concrete type resolution");
    }

    // ── Pattern 7: Inferred-type factory ─────────────────────────────────────

    [Fact]
    public void Extract_NonGenericFactory_InfersTypeFromMethodSymbol()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddSingleton(sp => new FooImpl());
                }
            }
            public class FooImpl {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("FooImpl \u2192 factory|Singleton",
            "service type should be inferred from the bound method's type argument");
    }

    [Fact]
    public void Extract_NonGenericFactory_Scoped_InfersType()
    {
        var source = """
            using Microsoft.Extensions.DependencyInjection;
            public class Startup {
                public void Configure(IServiceCollection services) {
                    services.AddScoped(sp => new BarService());
                }
            }
            public class BarService {}
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("BarService \u2192 factory|Scoped",
            "inferred-type factory should work for AddScoped too");
    }
}
