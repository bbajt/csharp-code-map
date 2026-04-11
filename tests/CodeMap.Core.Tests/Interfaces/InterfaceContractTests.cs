namespace CodeMap.Core.Tests.Interfaces;

using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class InterfaceContractTests
{
    // ─── IGitService ──────────────────────────────────────────────────────────

    [Fact]
    public void IGitService_HasGetRepoIdentityAsync_Method()
    {
        var method = typeof(IGitService).GetMethod("GetRepoIdentityAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<RepoId>));
        method.GetParameters().Should().Contain(p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void IGitService_HasGetCurrentCommitAsync_Method()
    {
        var method = typeof(IGitService).GetMethod("GetCurrentCommitAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<CommitSha>));
    }

    [Fact]
    public void IGitService_HasGetCurrentBranchAsync_Method()
    {
        var method = typeof(IGitService).GetMethod("GetCurrentBranchAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<string>));
    }

    [Fact]
    public void IGitService_HasGetChangedFilesAsync_Method()
    {
        var method = typeof(IGitService).GetMethod("GetChangedFilesAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<IReadOnlyList<FileChange>>));
    }

    [Fact]
    public void IGitService_HasIsCleanAsync_Method()
    {
        var method = typeof(IGitService).GetMethod("IsCleanAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<bool>));
    }

    [Fact]
    public void IGitService_AllMethodsAcceptCancellationToken()
    {
        var methods = typeof(IGitService).GetMethods();
        foreach (var method in methods)
            method.GetParameters().Should().Contain(p => p.ParameterType == typeof(CancellationToken),
                because: $"{method.Name} should accept CancellationToken");
    }

    [Fact]
    public void IGitService_Defines6Methods() =>
        typeof(IGitService).GetMethods().Should().HaveCount(6);

    // ─── IRoslynCompiler ──────────────────────────────────────────────────────

    [Fact]
    public void IRoslynCompiler_HasCompileAndExtractAsync_Method()
    {
        var method = typeof(IRoslynCompiler).GetMethod("CompileAndExtractAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<CompilationResult>));
    }

    [Fact]
    public void IRoslynCompiler_HasIncrementalExtractAsync_Method()
    {
        var method = typeof(IRoslynCompiler).GetMethod("IncrementalExtractAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<CompilationResult>));
    }

    [Fact]
    public void IRoslynCompiler_AllMethodsAcceptCancellationToken()
    {
        var methods = typeof(IRoslynCompiler).GetMethods();
        foreach (var method in methods)
            method.GetParameters().Should().Contain(p => p.ParameterType == typeof(CancellationToken),
                because: $"{method.Name} should accept CancellationToken");
    }

    [Fact]
    public void IRoslynCompiler_Defines2Methods() =>
        typeof(IRoslynCompiler).GetMethods().Should().HaveCount(2);

    // ─── ISymbolStore ─────────────────────────────────────────────────────────

    [Fact]
    public void ISymbolStore_HasCreateBaselineAsync_Method() =>
        typeof(ISymbolStore).GetMethod("CreateBaselineAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasBaselineExistsAsync_Method() =>
        typeof(ISymbolStore).GetMethod("BaselineExistsAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasGetSymbolAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetSymbolAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasSearchSymbolsAsync_Method() =>
        typeof(ISymbolStore).GetMethod("SearchSymbolsAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasGetReferencesAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetReferencesAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasGetFileSpanAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetFileSpanAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_AllMethodsAcceptCancellationToken()
    {
        var methods = typeof(ISymbolStore).GetMethods();
        foreach (var method in methods)
            method.GetParameters().Should().Contain(p => p.ParameterType == typeof(CancellationToken),
                because: $"{method.Name} should accept CancellationToken");
    }

    [Fact]
    public void ISymbolStore_HasGetOutgoingReferencesAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetOutgoingReferencesAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasGetTypeRelationsAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetTypeRelationsAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_HasGetDerivedTypesAsync_Method() =>
        typeof(ISymbolStore).GetMethod("GetDerivedTypesAsync").Should().NotBeNull();

    [Fact]
    public void ISymbolStore_Defines21Methods() =>
        typeof(ISymbolStore).GetMethods().Should().HaveCount(27); // +1 GetAllFileContentsAsync (FTS content indexing)

    // ─── IQueryEngine ─────────────────────────────────────────────────────────

    [Fact]
    public void IQueryEngine_HasSearchSymbolsAsync_Method() =>
        typeof(IQueryEngine).GetMethod("SearchSymbolsAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_HasGetSymbolCardAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetSymbolCardAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_HasGetSpanAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetSpanAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_HasGetDefinitionSpanAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetDefinitionSpanAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_AllMethodsReturnResultType()
    {
        var methods = typeof(IQueryEngine).GetMethods();
        foreach (var method in methods)
        {
            var retType = method.ReturnType;
            // Task<Result<..., CodeMapError>>
            retType.Should().Be(typeof(Task<>).MakeGenericType(
                typeof(Result<,>).MakeGenericType(retType.GetGenericArguments()[0].GetGenericArguments()[0],
                    typeof(CodeMapError))),
                because: $"{method.Name} should return Result<T, CodeMapError>");
        }
    }

    [Fact]
    public void IQueryEngine_AllMethodsAcceptCancellationToken()
    {
        var methods = typeof(IQueryEngine).GetMethods();
        foreach (var method in methods)
            method.GetParameters().Should().Contain(p => p.ParameterType == typeof(CancellationToken),
                because: $"{method.Name} should accept CancellationToken");
    }

    [Fact]
    public void IQueryEngine_HasGetCallersAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetCallersAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_HasGetCalleesAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetCalleesAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_HasGetTypeHierarchyAsync_Method() =>
        typeof(IQueryEngine).GetMethod("GetTypeHierarchyAsync").Should().NotBeNull();

    [Fact]
    public void IQueryEngine_Defines18Methods() =>
        typeof(IQueryEngine).GetMethods().Should().HaveCount(18); // +1 for SearchTextAsync (PHASE-09-02)

    // ─── ICacheService ────────────────────────────────────────────────────────

    [Fact]
    public void ICacheService_HasGetAsync_Method() =>
        typeof(ICacheService).GetMethod("GetAsync").Should().NotBeNull();

    [Fact]
    public void ICacheService_HasSetAsync_Method() =>
        typeof(ICacheService).GetMethod("SetAsync").Should().NotBeNull();

    [Fact]
    public void ICacheService_HasInvalidateAsync_Method() =>
        typeof(ICacheService).GetMethod("InvalidateAsync").Should().NotBeNull();

    [Fact]
    public void ICacheService_HasInvalidateAllAsync_Method() =>
        typeof(ICacheService).GetMethod("InvalidateAllAsync").Should().NotBeNull();

    [Fact]
    public void ICacheService_Defines4Methods() =>
        typeof(ICacheService).GetMethods().Should().HaveCount(4);

    // ─── ITokenSavingsTracker ─────────────────────────────────────────────────

    [Fact]
    public void ITokenSavingsTracker_HasRecordSaving_Method() =>
        typeof(ITokenSavingsTracker).GetMethod("RecordSaving").Should().NotBeNull();

    [Fact]
    public void ITokenSavingsTracker_HasTotalTokensSaved_Property() =>
        typeof(ITokenSavingsTracker).GetProperty("TotalTokensSaved").Should().NotBeNull();

    [Fact]
    public void ITokenSavingsTracker_HasTotalCostAvoided_Property() =>
        typeof(ITokenSavingsTracker).GetProperty("TotalCostAvoided").Should().NotBeNull();
}
