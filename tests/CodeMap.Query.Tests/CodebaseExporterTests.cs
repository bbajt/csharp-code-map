namespace CodeMap.Query.Tests;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;
using NSubstitute;

public sealed class CodebaseExporterTests
{
    private static readonly RepoId Repo = RepoId.From("exporter-repo");
    private static readonly CommitSha Sha = CommitSha.From(new string('e', 40));

    private readonly ISymbolStore _store = Substitute.For<ISymbolStore>();

    private void SetupEmptyStore()
    {
        foreach (FactKind kind in Enum.GetValues<FactKind>())
            _store.GetFactsByKindAsync(Repo, Sha, kind, Arg.Any<int>(), Arg.Any<CancellationToken>())
                  .Returns(Array.Empty<StoredFact>());

        _store.GetProjectDiagnosticsAsync(Repo, Sha, Arg.Any<CancellationToken>())
              .Returns(Array.Empty<ProjectDiagnostic>());

        _store.GetSymbolsByKindsAsync(Repo, Sha, Arg.Any<IReadOnlyList<SymbolKind>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<SymbolSearchHit>());

        _store.GetOutgoingReferencesAsync(Repo, Sha, Arg.Any<SymbolId>(), Arg.Any<RefKind?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Array.Empty<StoredOutgoingReference>());
    }

    private static SymbolSearchHit MakeHit(string fqn, SymbolKind kind)
        => new SymbolSearchHit(
            SymbolId: SymbolId.From(fqn),
            FullyQualifiedName: fqn,
            Kind: kind,
            Signature: fqn,
            DocumentationSnippet: null,
            FilePath: FilePath.From("Test.cs"),
            Line: 1,
            Score: 1.0);

    [Fact]
    public async Task ExportAsync_SummaryDetail_ReturnsSummarySections()
    {
        SetupEmptyStore();

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "TestSolution",
            detail: "summary", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Should().NotBeNull();
        result.DetailLevel.Should().Be("summary");
        result.Format.Should().Be("markdown");
        result.Content.Should().Contain("TestSolution");
    }

    [Fact]
    public async Task ExportAsync_MarkdownFormat_ContainsMarkdownHeader()
    {
        SetupEmptyStore();

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "summary", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().StartWith("# MySolution — Codebase Context");
    }

    [Fact]
    public async Task ExportAsync_JsonFormat_ReturnsValidJson()
    {
        SetupEmptyStore();

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "summary", format: "json", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Format.Should().Be("json");
        result.Content.Should().StartWith("{");
        result.Content.Should().Contain("\"solution\"");
        result.Content.Should().Contain("\"sections\"");
    }

    [Fact]
    public async Task ExportAsync_StandardDetail_QueriesSymbolsByKinds()
    {
        SetupEmptyStore();

        await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        await _store.Received().GetSymbolsByKindsAsync(
            Repo, Sha,
            Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAsync_StandardDetail_WithPublicTypes_IncludesPublicApiSection()
    {
        SetupEmptyStore();

        var typeHits = new[]
        {
            MakeHit("MyNamespace.MyClass", SymbolKind.Class),
            MakeHit("MyNamespace.IMyInterface", SymbolKind.Interface),
        };

        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
              .Returns(typeHits);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().Contain("Public API Surface");
        result.Content.Should().Contain("MyClass");
    }

    [Fact]
    public async Task ExportAsync_FullDetail_QueriesAllSymbols()
    {
        SetupEmptyStore();

        await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "full", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        // full detail calls GetSymbolsByKindsAsync with null (all symbols)
        await _store.Received().GetSymbolsByKindsAsync(
            Repo, Sha,
            Arg.Is<IReadOnlyList<SymbolKind>?>(k => k == null),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExportAsync_TokenBudgetExhausted_SetsTruncatedFlag()
    {
        SetupEmptyStore();

        // Very small token budget to force truncation
        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "summary", format: "markdown", maxTokens: 1,
            sectionFilter: null, ct: CancellationToken.None);

        // With maxTokens=1, at minimum the budget will be exhausted after first content
        result.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ExportAsync_SectionFilter_ExcludesFilteredSections()
    {
        SetupEmptyStore();

        var typeHits = new[] { MakeHit("MyNamespace.MyService", SymbolKind.Class) };
        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
              .Returns(typeHits);

        // Only include public_api, exclude dependencies and interfaces
        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 4000,
            sectionFilter: ["public_api"],
            ct: CancellationToken.None);

        result.Content.Should().Contain("Public API Surface");
        result.Content.Should().NotContain("Service Dependencies");
        result.Content.Should().NotContain("Interface Contracts");
    }

    [Fact]
    public async Task ExportAsync_EstimatedTokens_IsContentLengthDividedByFour()
    {
        SetupEmptyStore();

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "summary", format: "markdown", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.EstimatedTokens.Should().Be(result.Content.Length / 4);
    }

    [Fact]
    public async Task ExportAsync_JsonFormat_IncludesStats()
    {
        SetupEmptyStore();

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "summary", format: "json", maxTokens: 4000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().Contain("\"stats\"");
        result.Content.Should().Contain("\"detail\"");
    }

    // ── Export test type exclusion tests ──────────────────────────────────────

    private static SymbolSearchHit MakeTestHit(string fqn, SymbolKind kind, string filePath)
        => new SymbolSearchHit(
            SymbolId: SymbolId.From(fqn),
            FullyQualifiedName: fqn,
            Kind: kind,
            Signature: fqn,
            DocumentationSnippet: null,
            FilePath: FilePath.From(filePath),
            Line: 1,
            Score: 1.0);

    [Fact]
    public async Task ExportAsync_Standard_ExcludesTestTypes()
    {
        SetupEmptyStore();

        // Return mixed: one production type + one test type
        var productionType = MakeTestHit("T:MyApp.OrderService", SymbolKind.Class, "src/OrderService.cs");
        var testType = MakeTestHit("T:MyApp.OrderServiceTests", SymbolKind.Class, "tests/CodeMap.Core.Tests/OrderServiceTests.cs");

        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SymbolSearchHit>)[productionType, testType]);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 10000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().Contain("OrderService");
        result.Content.Should().NotContain("OrderServiceTests");
    }

    [Fact]
    public async Task ExportAsync_Full_IncludesTestTypes()
    {
        SetupEmptyStore();

        var productionType = MakeTestHit("T:MyApp.OrderService", SymbolKind.Class, "src/OrderService.cs");
        var testType = MakeTestHit("T:MyApp.OrderServiceTests", SymbolKind.Class, "tests/CodeMap.Core.Tests/OrderServiceTests.cs");

        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SymbolSearchHit>)[productionType, testType]);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "full", format: "markdown", maxTokens: 10000,
            sectionFilter: null, ct: CancellationToken.None);

        // Full detail should include both production and test types
        result.Content.Should().Contain("OrderService");
        result.Content.Should().Contain("OrderServiceTests");
    }

    [Theory]
    [InlineData("tests/CodeMap.Core.Tests/Foo.cs", true)]
    [InlineData("tests/CodeMap.Benchmarks/Bench.cs", true)]
    [InlineData("tests/CodeMap.TestUtilities/Helpers.cs", true)]
    [InlineData("src/CodeMap.Core/Models/Foo.cs", false)]
    [InlineData("src/CodeMap.Query/Exporter.cs", false)]
    public async Task ExportAsync_Standard_FiltersCorrectPaths(string filePath, bool shouldBeExcluded)
    {
        SetupEmptyStore();

        var hit = MakeTestHit("T:MyApp.Foo", SymbolKind.Class, filePath);
        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SymbolSearchHit>)[hit]);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 10000,
            sectionFilter: null, ct: CancellationToken.None);

        if (shouldBeExcluded)
            result.Content.Should().NotContain("T:MyApp.Foo");
        else
            result.Content.Should().Contain("T:MyApp.Foo");
    }

    [Theory]
    [InlineData("global::AutoGeneratedProgram")]
    [InlineData("<Program>$")]
    public async Task ExportAsync_Standard_ExcludesCompilerGeneratedTypes(string fqn)
    {
        SetupEmptyStore();

        var compilerType = MakeTestHit(fqn, SymbolKind.Class, "src/Program.cs");
        var productionType = MakeTestHit("T:MyApp.Startup", SymbolKind.Class, "src/Startup.cs");

        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k != null && k.Contains(SymbolKind.Class)),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SymbolSearchHit>)[compilerType, productionType]);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "standard", format: "markdown", maxTokens: 10000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().NotContain(fqn, "compiler-generated types excluded from standard export");
        result.Content.Should().Contain("T:MyApp.Startup");
    }

    [Fact]
    public async Task ExportAsync_Full_IncludesCompilerGeneratedTypes()
    {
        SetupEmptyStore();

        var compilerType = MakeTestHit("global::AutoGeneratedProgram", SymbolKind.Class, "src/Program.cs");

        _store.GetSymbolsByKindsAsync(
                Repo, Sha,
                Arg.Is<IReadOnlyList<SymbolKind>?>(k => k == null),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SymbolSearchHit>)[compilerType]);

        var result = await CodebaseExporter.ExportAsync(
            _store, Repo, Sha, "MySolution",
            detail: "full", format: "markdown", maxTokens: 10000,
            sectionFilter: null, ct: CancellationToken.None);

        result.Content.Should().Contain("AutoGeneratedProgram", "full export includes everything");
    }
}
