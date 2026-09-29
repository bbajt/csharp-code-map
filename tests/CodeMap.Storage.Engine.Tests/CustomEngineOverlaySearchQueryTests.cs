namespace CodeMap.Storage.Engine.Tests;

using CodeMap.Core.Types;
using FluentAssertions;
using Xunit;

/// <summary>
/// PHASE-21-08 T01: workspace-mode search (<see cref="CustomEngineOverlayStore.SearchOverlaySymbolsAsync"/>)
/// must parse a query like the baseline does (<c>OR</c>, quotes, trailing <c>*</c>). Before the fix it had
/// its own tokenizer: split on space and dot only, every token ANDed, quotes and <c>*</c> kept in the tokens.
/// </summary>
public sealed class CustomEngineOverlaySearchQueryTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"codemap-ovl-query-{Guid.NewGuid():N}");
    private CustomSymbolStore _symbolStore = null!;
    private CustomEngineOverlayStore _overlayStore = null!;

    private const string RepoName = "q-repo";
    private static readonly RepoId Repo = RepoId.From(RepoName);
    private static readonly WorkspaceId Ws = WorkspaceId.From("q-ws");
    private static readonly CommitSha Sha = CommitSha.From("abcdef0123456789abcdef0123456789abcdef01");

    /// <summary>Builds the TestData baseline and an overlay holding two new symbols.</summary>
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        var storeBaseDir = Path.Combine(_tempDir, "store");
        var builder = new EngineBaselineBuilder(Path.Combine(storeBaseDir, RepoName));
        (await builder.BuildAsync(TestData.CreateTestInput(), CancellationToken.None)).Success.Should().BeTrue();

        _symbolStore = new CustomSymbolStore(storeBaseDir);
        _overlayStore = new CustomEngineOverlayStore(_symbolStore, storeBaseDir);
        await _overlayStore.CreateOverlayAsync(Repo, Ws, Sha);

        var overlay = _symbolStore.TryGetOverlay(Repo.Value, Ws.Value)!;
        using var batch = overlay.BeginBatch();
        Add(batch.UpsertSymbol, overlay.InternStringInternal, -1, "T:MyApp.OrderService", "OrderService");
        Add(batch.UpsertSymbol, overlay.InternStringInternal, -2, "T:MyApp.PaymentGateway", "PaymentGateway");
        await batch.CommitAsync();
    }

    /// <summary>Releases the stores and removes the temp dir.</summary>
    public ValueTask DisposeAsync()
    {
        _symbolStore?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SearchOverlay_OrOfTwoNewSymbols_ReturnsBoth()
    {
        var hits = await _overlayStore.SearchOverlaySymbolsAsync(
            Repo, Ws, "OrderService OR PaymentGateway", null, 10, TestContext.Current.CancellationToken);

        hits.Select(h => h.FullyQualifiedName).Should().Contain(f => f.EndsWith("OrderService")).And.Contain(f => f.EndsWith("PaymentGateway"));
    }

    [Fact]
    public async Task SearchOverlay_TrailingStar_MatchesLikeBaseline()
    {
        // The baseline strips a trailing '*' (prefix matching is native); the overlay kept it in the token.
        var hits = await _overlayStore.SearchOverlaySymbolsAsync(
            Repo, Ws, "OrderServ*", null, 10, TestContext.Current.CancellationToken);

        hits.Select(h => h.FullyQualifiedName).Should().Contain(f => f.EndsWith("OrderService"));
    }

    [Fact]
    public async Task SearchOverlay_QuotedName_Matches()
    {
        var hits = await _overlayStore.SearchOverlaySymbolsAsync(
            Repo, Ws, "\"PaymentGateway\"", null, 10, TestContext.Current.CancellationToken);

        hits.Select(h => h.FullyQualifiedName).Should().Contain(f => f.EndsWith("PaymentGateway"));
    }

    [Fact]
    public async Task SearchOverlay_CamelCasePart_Matches()
    {
        var hits = await _overlayStore.SearchOverlaySymbolsAsync(
            Repo, Ws, "gateway", null, 10, TestContext.Current.CancellationToken);

        hits.Select(h => h.FullyQualifiedName).Should().Contain(f => f.EndsWith("PaymentGateway"));
    }

    private static void Add(
        Action<SymbolRecord, string[]> upsert, Func<string, int> intern, int intId, string fqn, string displayName)
    {
        var tokens = SearchIndexBuilder.Tokenize(fqn, displayName, "MyApp");
        var rec = new SymbolRecord(
            symbolIntId: intId,
            stableIdStringId: intern("sym_" + displayName),
            fqnStringId: intern(fqn),
            displayNameStringId: intern(displayName),
            namespaceStringId: intern("MyApp"),
            containerIntId: 0, fileIntId: 0, projectIntId: 0,
            kind: 1, accessibility: 7, flags: 0, spanStart: 1, spanEnd: 10,
            nameTokensStringId: intern(string.Join(' ', tokens)),
            signatureHash: 0);
        upsert(rec, [.. tokens]);
    }
}
