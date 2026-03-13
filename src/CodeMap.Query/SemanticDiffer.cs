namespace CodeMap.Query;

using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Compares two committed baselines and produces a structured semantic diff.
/// Matches symbols by stable_id first (rename-aware), then falls back to FQN for
/// old baselines that predate PHASE-03-01.
/// </summary>
internal static class SemanticDiffer
{
    /// <summary>
    /// Loads symbols and facts from both baselines, computes changes, and renders markdown.
    /// </summary>
    public static async Task<DiffResponse> DiffAsync(
        ISymbolStore store,
        RepoId repoId,
        CommitSha fromCommit,
        CommitSha toCommit,
        IReadOnlyList<SymbolKind>? kinds,
        bool includeFacts,
        CancellationToken ct)
    {
        // 1. Load symbols from both baselines
        var fromSymbols = await store.GetAllSymbolSummariesAsync(repoId, fromCommit, ct);
        var toSymbols   = await store.GetAllSymbolSummariesAsync(repoId, toCommit, ct);

        // 2. Diff symbols
        var symbolChanges = DiffSymbols(fromSymbols, toSymbols);

        // 2b. Apply kinds filter
        if (kinds is { Count: > 0 })
            symbolChanges = symbolChanges.Where(s => kinds.Contains(s.Kind)).ToList();

        // 3. Diff facts (optional)
        List<FactDiff> factChanges = [];
        if (includeFacts)
        {
            var fromFacts = await LoadAllFactsAsync(store, repoId, fromCommit, ct);
            var toFacts   = await LoadAllFactsAsync(store, repoId, toCommit, ct);
            factChanges = DiffFacts(fromFacts, toFacts);
        }

        // 4. Build stats + markdown
        var stats    = BuildStats(symbolChanges, factChanges);
        var markdown = RenderMarkdown(fromCommit, toCommit, symbolChanges, factChanges, stats);

        return new DiffResponse(fromCommit, toCommit, markdown, stats, symbolChanges, factChanges);
    }

    // ── Symbol diffing ────────────────────────────────────────────────────────

    private static List<SymbolDiff> DiffSymbols(
        IReadOnlyList<SymbolSummary> fromSymbols,
        IReadOnlyList<SymbolSummary> toSymbols)
    {
        var changes = new List<SymbolDiff>();

        // Index by stable_id for rename-aware matching
        var fromByStable = fromSymbols
            .Where(s => s.StableId is not null)
            .ToDictionary(s => s.StableId!.Value.Value);
        var toByStable = toSymbols
            .Where(s => s.StableId is not null)
            .ToDictionary(s => s.StableId!.Value.Value);

        // Index by FQN (SymbolId.Value) for fallback
        var fromByFqn = fromSymbols.ToDictionary(s => s.SymbolId.Value);
        var toByFqn   = toSymbols.ToDictionary(s => s.SymbolId.Value);

        var matched     = new HashSet<string>(StringComparer.Ordinal); // matched stable_id values
        var matchedFqns = new HashSet<string>(StringComparer.Ordinal); // matched FQN strings

        // Pass 1: Match by stable_id
        foreach (var (stableIdVal, fromSym) in fromByStable)
        {
            if (!toByStable.TryGetValue(stableIdVal, out var toSym)) continue;

            matched.Add(stableIdVal);
            matchedFqns.Add(fromSym.SymbolId.Value);
            matchedFqns.Add(toSym.SymbolId.Value);

            if (fromSym.SymbolId != toSym.SymbolId)
            {
                changes.Add(new SymbolDiff("Renamed",
                    fromSym.SymbolId, toSym.SymbolId, fromSym.StableId,
                    fromSym.Signature, toSym.Signature, fromSym.Kind));
            }
            else if (fromSym.Signature != toSym.Signature)
            {
                changes.Add(new SymbolDiff("SignatureChanged",
                    fromSym.SymbolId, toSym.SymbolId, fromSym.StableId,
                    fromSym.Signature, toSym.Signature, fromSym.Kind));
            }
            else if (fromSym.Visibility != toSym.Visibility)
            {
                changes.Add(new SymbolDiff("VisibilityChanged",
                    fromSym.SymbolId, toSym.SymbolId, fromSym.StableId,
                    fromSym.Signature, toSym.Signature, fromSym.Kind));
            }
            // else: unchanged — don't report
        }

        // Pass 2: FQN fallback for unmatched symbols
        foreach (var fromSym in fromSymbols)
        {
            if (matchedFqns.Contains(fromSym.SymbolId.Value)) continue;

            if (toByFqn.TryGetValue(fromSym.SymbolId.Value, out var toSym))
            {
                matchedFqns.Add(fromSym.SymbolId.Value);
                if (fromSym.Signature != toSym.Signature)
                {
                    changes.Add(new SymbolDiff("SignatureChanged",
                        fromSym.SymbolId, toSym.SymbolId, null,
                        fromSym.Signature, toSym.Signature, fromSym.Kind));
                }
            }
            else
            {
                // In FROM but not in TO → removed
                changes.Add(new SymbolDiff("Removed",
                    fromSym.SymbolId, null, fromSym.StableId,
                    fromSym.Signature, null, fromSym.Kind));
            }
        }

        // Pass 3: Additions (in TO but not matched)
        foreach (var toSym in toSymbols)
        {
            if (matchedFqns.Contains(toSym.SymbolId.Value)) continue;
            if (toSym.StableId is not null && matched.Contains(toSym.StableId.Value.Value)) continue;

            changes.Add(new SymbolDiff("Added",
                null, toSym.SymbolId, toSym.StableId,
                null, toSym.Signature, toSym.Kind));
        }

        return changes;
    }

    // ── Fact diffing ──────────────────────────────────────────────────────────

    private static List<FactDiff> DiffFacts(
        IReadOnlyList<StoredFact> fromFacts,
        IReadOnlyList<StoredFact> toFacts)
    {
        // GroupBy → First() to deduplicate (multiple symbols may share the same fact key)
        var fromKeys = fromFacts
            .GroupBy(f => FactKey(f))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var toKeys = toFacts
            .GroupBy(f => FactKey(f))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var changes = new List<FactDiff>();

        // Removed
        foreach (var (key, fact) in fromKeys)
        {
            if (!toKeys.ContainsKey(key))
                changes.Add(new FactDiff("Removed", fact.Kind, fact.Value, null));
        }

        // Added
        foreach (var (key, fact) in toKeys)
        {
            if (!fromKeys.ContainsKey(key))
                changes.Add(new FactDiff("Added", fact.Kind, null, fact.Value));
        }

        // Changed (same key, different value — e.g. DI lifetime changed)
        foreach (var (key, fromFact) in fromKeys)
        {
            if (toKeys.TryGetValue(key, out var toFact) && fromFact.Value != toFact.Value)
                changes.Add(new FactDiff("Changed", fromFact.Kind, fromFact.Value, toFact.Value));
        }

        return changes;
    }

    private static string FactKey(StoredFact fact) => fact.Kind switch
    {
        FactKind.Route          => fact.Value,
        FactKind.Config         => fact.Value.Split('|')[0],
        FactKind.DbTable        => fact.Value.Split('|')[0],
        FactKind.DiRegistration => fact.Value.Split('\u2192')[0].Trim(), // → (Unicode arrow)
        _                       => $"{fact.Kind}:{fact.Value}",
    };

    private static async Task<List<StoredFact>> LoadAllFactsAsync(
        ISymbolStore store, RepoId repoId, CommitSha commitSha, CancellationToken ct)
    {
        var all = new List<StoredFact>();
        foreach (var kind in Enum.GetValues<FactKind>())
        {
            var facts = await store.GetFactsByKindAsync(repoId, commitSha, kind, 500, ct);
            all.AddRange(facts);
        }
        return all;
    }

    // ── Stats + markdown ──────────────────────────────────────────────────────

    private static DiffStats BuildStats(List<SymbolDiff> symbols, List<FactDiff> facts)
    {
        return new DiffStats(
            SymbolsAdded:             symbols.Count(s => s.ChangeType == "Added"),
            SymbolsRemoved:           symbols.Count(s => s.ChangeType == "Removed"),
            SymbolsRenamed:           symbols.Count(s => s.ChangeType == "Renamed"),
            SymbolsSignatureChanged:  symbols.Count(s => s.ChangeType == "SignatureChanged"),
            EndpointsAdded:           facts.Count(f => f.Kind == FactKind.Route    && f.ChangeType == "Added"),
            EndpointsRemoved:         facts.Count(f => f.Kind == FactKind.Route    && f.ChangeType == "Removed"),
            ConfigKeysAdded:          facts.Count(f => f.Kind == FactKind.Config   && f.ChangeType == "Added"),
            ConfigKeysRemoved:        facts.Count(f => f.Kind == FactKind.Config   && f.ChangeType == "Removed"),
            DbTablesAdded:            facts.Count(f => f.Kind == FactKind.DbTable  && f.ChangeType == "Added"),
            DbTablesRemoved:          facts.Count(f => f.Kind == FactKind.DbTable  && f.ChangeType == "Removed"),
            DiRegistrationsAdded:     facts.Count(f => f.Kind == FactKind.DiRegistration && f.ChangeType == "Added"),
            DiRegistrationsRemoved:   facts.Count(f => f.Kind == FactKind.DiRegistration && f.ChangeType == "Removed"));
    }

    private static string RenderMarkdown(
        CommitSha from, CommitSha to,
        List<SymbolDiff> symbols, List<FactDiff> facts, DiffStats stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Semantic Diff: {from.Value[..7]} \u2192 {to.Value[..7]}");
        sb.AppendLine();

        // Summary
        sb.AppendLine("## Summary");
        sb.AppendLine($"- {stats.SymbolsAdded} symbols added, {stats.SymbolsRemoved} removed, " +
                      $"{stats.SymbolsRenamed} renamed, {stats.SymbolsSignatureChanged} signature changes");
        sb.AppendLine($"- {stats.EndpointsAdded} endpoints added, {stats.EndpointsRemoved} removed");
        sb.AppendLine($"- {stats.ConfigKeysAdded} config keys added, {stats.ConfigKeysRemoved} removed");
        sb.AppendLine($"- {stats.DbTablesAdded} DB tables added, {stats.DbTablesRemoved} removed");
        sb.AppendLine($"- {stats.DiRegistrationsAdded} DI registrations added, {stats.DiRegistrationsRemoved} removed");
        sb.AppendLine();

        // Endpoints
        var endpoints = facts.Where(f => f.Kind == FactKind.Route).ToList();
        if (endpoints.Count > 0)
        {
            sb.AppendLine("## Endpoints");
            sb.AppendLine("| Change | Route |");
            sb.AppendLine("|--------|-------|");
            foreach (var f in endpoints)
                sb.AppendLine($"| {f.ChangeType} | {f.ToValue ?? f.FromValue} |");
            sb.AppendLine();
        }

        // Symbols
        if (symbols.Count > 0)
        {
            sb.AppendLine("## Symbols");
            sb.AppendLine("| Change | Symbol | Details |");
            sb.AppendLine("|--------|--------|---------|");
            foreach (var s in symbols)
            {
                var name    = ShortName(s.ToSymbolId?.Value ?? s.FromSymbolId?.Value ?? "—");
                var details = s.ChangeType switch
                {
                    "Renamed"          => $"{ShortName(s.FromSymbolId!.Value)} \u2192 {ShortName(s.ToSymbolId!.Value)}",
                    "SignatureChanged"  => $"`{s.FromSignature}` \u2192 `{s.ToSignature}`",
                    "VisibilityChanged" => $"visibility changed",
                    _                  => s.ChangeType.ToLowerInvariant(),
                };
                sb.AppendLine($"| {s.ChangeType} | `{name}` | {details} |");
            }
            sb.AppendLine();
        }

        // Configuration
        var configFacts = facts.Where(f => f.Kind == FactKind.Config).ToList();
        if (configFacts.Count > 0)
        {
            sb.AppendLine("## Configuration");
            sb.AppendLine("| Change | Key |");
            sb.AppendLine("|--------|-----|");
            foreach (var f in configFacts)
                sb.AppendLine($"| {f.ChangeType} | {FactKey(f)} |");
            sb.AppendLine();
        }

        // DI Registrations
        var diFacts = facts.Where(f => f.Kind == FactKind.DiRegistration).ToList();
        if (diFacts.Count > 0)
        {
            sb.AppendLine("## DI Registrations");
            sb.AppendLine("| Change | Service | Details |");
            sb.AppendLine("|--------|---------|---------|");
            foreach (var f in diFacts)
                sb.AppendLine($"| {f.ChangeType} | {FactKey(f)} | {f.ToValue ?? f.FromValue} |");
            sb.AppendLine();
        }

        sb.Append("---");
        return sb.ToString();
    }

    // Reuse FactKey for markdown rendering (strips pipe metadata for display)
    private static string FactKey(FactDiff f) => f.Kind switch
    {
        FactKind.Config         => (f.ToValue ?? f.FromValue ?? "").Split('|')[0],
        FactKind.DiRegistration => (f.ToValue ?? f.FromValue ?? "").Split('\u2192')[0].Trim(),
        _                       => f.ToValue ?? f.FromValue ?? "—",
    };

    private static string ShortName(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return "—";
        if (fqn.Length > 2 && fqn[1] == ':') fqn = fqn[2..];
        var paren = fqn.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) fqn = fqn[..paren];
        var dot = fqn.LastIndexOf('.');
        if (dot > 0)
        {
            var prev = fqn.LastIndexOf('.', dot - 1);
            if (prev > 0) fqn = fqn[(prev + 1)..];
        }
        return fqn;
    }
}
