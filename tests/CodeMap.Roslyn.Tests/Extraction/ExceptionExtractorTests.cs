namespace CodeMap.Roslyn.Tests.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using CodeMap.Roslyn.Tests.Helpers;
using FluentAssertions;

public class ExceptionExtractorTests
{
    private static IReadOnlyList<Core.Models.ExtractedFact> Extract(string source)
    {
        var compilation = CompilationBuilder.Create(source);
        return ExceptionExtractor.ExtractAll(compilation, "/repo/");
    }

    [Fact]
    public void Extract_ThrowNew_ProducesExceptionFact()
    {
        var source = """
            public class OrderNotFoundException : System.Exception {
                public OrderNotFoundException(string msg) : base(msg) { }
            }
            public class OrderService {
                public void Cancel(int id) {
                    throw new OrderNotFoundException($"Order {id} not found");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Exception);
        facts[0].Value.Should().Be("OrderNotFoundException|throw new");
    }

    [Fact]
    public void Extract_ThrowNew_WithNameof_MarkedAsGuard()
    {
        var source = """
            public class OrderService {
                public void Submit(string customerId) {
                    if (customerId == null)
                        throw new System.ArgumentNullException(nameof(customerId));
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Exception);
        facts[0].Value.Should().Be("ArgumentNullException|throw new (nameof guard)");
    }

    [Fact]
    public void Extract_BareThrow_InCatch_ExtractsExceptionType()
    {
        var source = """
            public class DbException : System.Exception { }
            public class OrderService {
                public void Save() {
                    try { }
                    catch (DbException ex) {
                        System.Console.Write(ex);
                        throw;
                    }
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Exception);
        facts[0].Value.Should().Be("DbException|re-throw");
    }

    [Fact]
    public void Extract_BareThrow_UntypedCatch_DefaultsToException()
    {
        var source = """
            public class OrderService {
                public void Save() {
                    try { }
                    catch {
                        throw;
                    }
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Value.Should().Be("Exception|re-throw");
    }

    [Fact]
    public void Extract_ThrowExpression_NullCoalescing()
    {
        var source = """
            public class OrderNotFoundException : System.Exception {
                public OrderNotFoundException(int id) : base(id.ToString()) { }
            }
            public class Repo {
                public object? Find(int id) => null;
            }
            public class OrderService {
                private readonly Repo _repo = new();
                public object Get(int id) =>
                    _repo.Find(id) ?? throw new OrderNotFoundException(id);
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Kind.Should().Be(FactKind.Exception);
        facts[0].Value.Should().Be("OrderNotFoundException|throw expression");
    }

    [Fact]
    public void Extract_MultipleThrows_ProducesMultipleFacts()
    {
        var source = """
            public class OrderService {
                public void Process(string? id, int count) {
                    if (id == null)
                        throw new System.ArgumentNullException(nameof(id));
                    if (count <= 0)
                        throw new System.ArgumentException("Count must be positive", nameof(count));
                    try { }
                    catch (System.InvalidOperationException ex) {
                        System.Console.Write(ex);
                        throw;
                    }
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(3);
    }

    [Fact]
    public void Extract_NoThrows_ReturnsEmpty()
    {
        var source = """
            public class OrderService {
                public int Add(int a, int b) => a + b;
            }
            """;

        var facts = Extract(source);

        facts.Should().BeEmpty();
    }

    [Fact]
    public void Extract_ContainingSymbol_PointsToMethod()
    {
        var source = """
            public class OrderService {
                public void CancelOrder(int id) {
                    throw new System.InvalidOperationException("Cannot cancel");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].SymbolId.Value.Should().Contain("CancelOrder");
    }

    [Fact]
    public void Extract_StableId_NotPopulated_WhenNoMap()
    {
        var source = """
            public class OrderService {
                public void Cancel() {
                    throw new System.InvalidOperationException("err");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].StableId.Should().BeNull();
    }

    [Fact]
    public void Extract_Confidence_High()
    {
        var source = """
            public class OrderService {
                public void Cancel() {
                    throw new System.InvalidOperationException("err");
                }
            }
            """;

        var facts = Extract(source);

        facts.Should().HaveCount(1);
        facts[0].Confidence.Should().Be(Confidence.High);
    }
}
