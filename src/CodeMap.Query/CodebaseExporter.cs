namespace CodeMap.Query;

using System.Text;
using System.Text.Json;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Builds portable codebase exports at three detail levels (summary / standard / full)
/// with a token budget to control output size.
/// Supports markdown and JSON output formats.
/// </summary>
internal static class CodebaseExporter
{
    private static readonly SymbolKind[] TypeKinds =
        [SymbolKind.Class, SymbolKind.Interface, SymbolKind.Record, SymbolKind.Struct];

    private static readonly SymbolKind[] MemberKinds =
        [SymbolKind.Method, SymbolKind.Property];

    private static readonly string[] ServiceSuffixes =
        ["Service", "Controller", "Handler", "Repository", "Manager", "Processor"];

    /// <summary>
    /// Exports the indexed codebase as a self-contained markdown or JSON document.
    /// </summary>
    public static async Task<ExportResponse> ExportAsync(
        ISymbolStore store,
        RepoId repoId,
        CommitSha commitSha,
        string solutionName,
        string detail,
        string format,
        int maxTokens,
        string[]? sectionFilter,
        CancellationToken ct)
    {
        var budget = new TokenBudget(Math.Max(maxTokens, 100));
        var sections = new List<ExportSection>();

        // ── Summary sections (always included) ───────────────────────────────
        var summary = await CodebaseSummarizer.SummarizeAsync(
            store, repoId, commitSha, solutionName, sectionFilter, 50, ct);

        foreach (var s in summary.Sections)
        {
            if (budget.Exhausted) break;
            sections.Add(new ExportSection(s.Title, budget.ConsumeWithLimit(s.Content), s.ItemCount));
        }

        bool summaryOnly = string.Equals(detail, "summary", StringComparison.OrdinalIgnoreCase);
        if (summaryOnly || budget.Exhausted)
            return BuildResponse(solutionName, sections, format, detail, budget, summary.Stats);

        // ── Standard sections (public API surface) ────────────────────────────
        var allTypes   = await store.GetSymbolsByKindsAsync(repoId, commitSha, TypeKinds, 100, ct);
        var allMembers = await store.GetSymbolsByKindsAsync(repoId, commitSha, MemberKinds, 1000, ct);

        // Full detail includes everything; standard excludes test and compiler-generated types.
        bool isFullDetail = string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase);
        var publicTypes = isFullDetail ? allTypes
            : allTypes.Where(t => !IsTestPath(t.FilePath) && !IsCompilerGenerated(t.FullyQualifiedName)).ToList();
        var members = isFullDetail ? allMembers : allMembers.Where(m => !IsTestPath(m.FilePath)).ToList();

        // Group members by parent FQN prefix (strip doc-comment type prefix M:/P:/etc.)
        var membersByParent = members
            .GroupBy(m => GetParentFqn(m.FullyQualifiedName))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (budget.HasRoom && !IsSectionExcluded("public_api", sectionFilter))
        {
            var section = BuildPublicApiSection(publicTypes, membersByParent, budget);
            if (section is not null) sections.Add(section);
        }

        if (budget.HasRoom && !IsSectionExcluded("dependencies", sectionFilter))
        {
            var serviceTypes = publicTypes
                .Where(t => ServiceSuffixes.Any(sfx => t.FullyQualifiedName.EndsWith(sfx, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (serviceTypes.Count > 0)
            {
                var section = await BuildCallRelationshipsSection(serviceTypes, store, repoId, commitSha, budget, ct);
                if (section is not null) sections.Add(section);
            }
        }

        if (budget.HasRoom && !IsSectionExcluded("interfaces", sectionFilter))
        {
            var ifaces = publicTypes.Where(t => t.Kind == SymbolKind.Interface).ToList();
            if (ifaces.Count > 0)
            {
                var section = BuildInterfaceContractsSection(ifaces, membersByParent, budget);
                if (section is not null) sections.Add(section);
            }
        }

        bool standardOnly = string.Equals(detail, "standard", StringComparison.OrdinalIgnoreCase);
        if (standardOnly || budget.Exhausted)
            return BuildResponse(solutionName, sections, format, detail, budget, summary.Stats);

        // ── Full sections (everything) ────────────────────────────────────────
        if (budget.HasRoom && !IsSectionExcluded("all_symbols", sectionFilter))
        {
            var allSymbols = await store.GetSymbolsByKindsAsync(repoId, commitSha, null, 500, ct);
            var section = BuildFullSymbolSection(allSymbols, budget);
            if (section is not null) sections.Add(section);
        }

        if (budget.HasRoom && !IsSectionExcluded("references", sectionFilter))
        {
            var section = await BuildReferenceMatrixSection(publicTypes, store, repoId, commitSha, budget, ct);
            if (section is not null) sections.Add(section);
        }

        return BuildResponse(solutionName, sections, format, detail, budget, summary.Stats);
    }

    // ── Section builders ──────────────────────────────────────────────────────

    private static ExportSection? BuildPublicApiSection(
        IReadOnlyList<SymbolSearchHit> types,
        Dictionary<string, List<SymbolSearchHit>> membersByParent,
        TokenBudget budget)
    {
        var sb = new StringBuilder();
        int typeCount = 0;

        foreach (var type in types.OrderBy(t => t.FullyQualifiedName))
        {
            if (budget.Exhausted) break;

            var fqn = type.FullyQualifiedName;
            sb.AppendLine($"### {type.Kind} `{fqn}`");
            if (!string.IsNullOrEmpty(type.Signature) && type.Signature != fqn)
                sb.AppendLine($"```csharp\n{type.Signature}\n```");

            if (membersByParent.TryGetValue(fqn, out var typemembers))
            {
                foreach (var m in typemembers.Take(20))
                    sb.AppendLine($"- `{m.Signature}`");
            }

            sb.AppendLine();
            typeCount++;
        }

        if (typeCount == 0) return null;

        var content = budget.ConsumeWithLimit(sb.ToString());
        return new ExportSection($"Public API Surface ({typeCount} type{Plural(typeCount)})", content, typeCount);
    }

    private static async Task<ExportSection?> BuildCallRelationshipsSection(
        IReadOnlyList<SymbolSearchHit> services,
        ISymbolStore store,
        RepoId repoId,
        CommitSha commitSha,
        TokenBudget budget,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        int serviceCount = 0;

        foreach (var svc in services.Take(20))
        {
            if (budget.Exhausted) break;

            var refs = await store.GetOutgoingReferencesAsync(
                repoId, commitSha, svc.SymbolId, RefKind.Call, limit: 10, ct: ct);

            var callees = refs
                .Where(r => r.ResolutionState == ResolutionState.Resolved && r.ToSymbol != SymbolId.Empty)
                .Select(r => r.ToSymbol.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(5)
                .ToList();

            if (callees.Count == 0) continue;

            var shortName = GetShortName(svc.FullyQualifiedName);
            sb.AppendLine($"**{shortName}** calls:");
            foreach (var callee in callees)
                sb.AppendLine($"  → {GetShortName(callee)}");
            sb.AppendLine();
            serviceCount++;
        }

        if (serviceCount == 0) return null;

        var content = budget.ConsumeWithLimit(sb.ToString());
        return new ExportSection($"Service Dependencies ({serviceCount} service{Plural(serviceCount)})", content, serviceCount);
    }

    private static ExportSection? BuildInterfaceContractsSection(
        IReadOnlyList<SymbolSearchHit> interfaces,
        Dictionary<string, List<SymbolSearchHit>> membersByParent,
        TokenBudget budget)
    {
        var sb = new StringBuilder();
        int ifaceCount = 0;

        foreach (var iface in interfaces.OrderBy(i => i.FullyQualifiedName))
        {
            if (budget.Exhausted) break;

            var fqn = iface.FullyQualifiedName;
            sb.AppendLine($"#### `{fqn}`");

            if (membersByParent.TryGetValue(fqn, out var members))
            {
                foreach (var m in members.Take(15))
                    sb.AppendLine($"- `{m.Signature}`");
            }

            sb.AppendLine();
            ifaceCount++;
        }

        if (ifaceCount == 0) return null;

        var content = budget.ConsumeWithLimit(sb.ToString());
        return new ExportSection($"Interface Contracts ({ifaceCount} interface{Plural(ifaceCount)})", content, ifaceCount);
    }

    private static ExportSection? BuildFullSymbolSection(
        IReadOnlyList<SymbolSearchHit> symbols,
        TokenBudget budget)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Kind | Symbol | File | Line |");
        sb.AppendLine("|------|--------|------|------|");

        foreach (var s in symbols)
        {
            if (budget.Exhausted) break;
            sb.AppendLine($"| {s.Kind} | `{GetShortName(s.FullyQualifiedName)}` | {s.FilePath} | {s.Line} |");
        }

        var content = budget.ConsumeWithLimit(sb.ToString());
        return new ExportSection($"All Symbols ({symbols.Count})", content, symbols.Count);
    }

    private static async Task<ExportSection?> BuildReferenceMatrixSection(
        IReadOnlyList<SymbolSearchHit> types,
        ISymbolStore store,
        RepoId repoId,
        CommitSha commitSha,
        TokenBudget budget,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| From | Kind | To |");
        sb.AppendLine("|------|------|----|");

        foreach (var type in types.Take(30))
        {
            if (budget.Exhausted) break;

            var refs = await store.GetOutgoingReferencesAsync(
                repoId, commitSha, type.SymbolId, null, limit: 20, ct: ct);

            foreach (var r in refs.Take(10))
            {
                if (r.ResolutionState != ResolutionState.Resolved || r.ToSymbol == SymbolId.Empty)
                    continue;
                sb.AppendLine($"| `{GetShortName(type.FullyQualifiedName)}` | {r.Kind} | `{GetShortName(r.ToSymbol.Value)}` |");
            }
        }

        var content = budget.ConsumeWithLimit(sb.ToString());
        return new ExportSection("Reference Matrix", content, types.Count);
    }

    // ── Response assembly ─────────────────────────────────────────────────────

    private static ExportResponse BuildResponse(
        string solutionName,
        IReadOnlyList<ExportSection> sections,
        string format,
        string detail,
        TokenBudget budget,
        SummaryStats stats)
    {
        string content = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? RenderJson(solutionName, sections, detail, stats)
            : RenderMarkdown(solutionName, sections);

        int estimatedTokens = content.Length / 4;

        return new ExportResponse(
            Content: content,
            Format: format.ToLowerInvariant(),
            DetailLevel: detail.ToLowerInvariant(),
            EstimatedTokens: estimatedTokens,
            Truncated: budget.Truncated,
            Stats: stats);
    }

    private static string RenderMarkdown(string solutionName, IReadOnlyList<ExportSection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {solutionName} — Codebase Context");
        sb.AppendLine();
        foreach (var section in sections)
        {
            sb.AppendLine($"## {section.Title}");
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }
        sb.Append("---");
        return sb.ToString();
    }

    private static string RenderJson(
        string solutionName,
        IReadOnlyList<ExportSection> sections,
        string detail,
        SummaryStats stats)
    {
        var doc = new
        {
            solution = solutionName,
            detail,
            stats = new
            {
                projects = stats.ProjectCount,
                symbols = stats.SymbolCount,
                references = stats.ReferenceCount,
                facts = stats.FactCount,
                endpoints = stats.EndpointCount,
                config_keys = stats.ConfigKeyCount,
                db_tables = stats.DbTableCount,
                di_registrations = stats.DiRegistrationCount,
                semantic_level = stats.SemanticLevel.ToString(),
            },
            sections = sections.Select(s => new
            {
                title = s.Title,
                item_count = s.ItemCount,
                content = s.Content,
            }).ToList(),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsSectionExcluded(string section, string[]? filter)
        => filter is { Length: > 0 } && !filter.Contains(section, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns true if the file path belongs to a test or benchmark project.</summary>
    private static bool IsTestPath(FilePath filePath)
    {
        var v = filePath.Value;
        return v.Contains(".Tests/", StringComparison.Ordinal)
            || v.Contains(".Tests\\", StringComparison.Ordinal)
            || v.Contains(".Benchmarks/", StringComparison.Ordinal)
            || v.Contains(".Benchmarks\\", StringComparison.Ordinal)
            || v.Contains(".TestUtilities/", StringComparison.Ordinal)
            || v.Contains(".TestUtilities\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true for compiler-generated types that should not appear in the public API surface.
    /// Covers <c>AutoGeneratedProgram</c> (public name) and <c>&lt;Program&gt;$</c> (internal name)
    /// generated from top-level statement <c>Program.cs</c> files.
    /// </summary>
    private static bool IsCompilerGenerated(string? fqn)
        => fqn is not null
        && (fqn.EndsWith("AutoGeneratedProgram", StringComparison.Ordinal)
         || fqn.EndsWith("<Program>$", StringComparison.Ordinal));

    private static string GetParentFqn(string fqn)
    {
        // Strip doc-comment type prefix (M:, T:, P:, etc.)
        if (fqn.Length > 2 && fqn[1] == ':') fqn = fqn[2..];
        // Remove parameter list
        var paren = fqn.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) fqn = fqn[..paren];
        // Parent = everything before last dot
        var dot = fqn.LastIndexOf('.');
        return dot > 0 ? fqn[..dot] : fqn;
    }

    private static string GetShortName(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return "—";
        // Strip doc-comment prefix
        if (fqn.Length > 2 && fqn[1] == ':') fqn = fqn[2..];
        // Drop parameters
        var paren = fqn.IndexOf('(', StringComparison.Ordinal);
        if (paren > 0) fqn = fqn[..paren];
        // Keep last two segments
        var dot = fqn.LastIndexOf('.');
        if (dot > 0)
        {
            var prev = fqn.LastIndexOf('.', dot - 1);
            if (prev > 0) fqn = fqn[(prev + 1)..];
        }
        return fqn;
    }

    private static string Plural(int count) => count == 1 ? "" : "s";
}

/// <summary>A single section in a codebase export.</summary>
internal record ExportSection(string Title, string Content, int ItemCount);

/// <summary>
/// Tracks token consumption and enforces a budget ceiling.
/// Token estimate: <c>text.Length / 4</c> (same heuristic as token savings calculations).
/// </summary>
internal sealed class TokenBudget
{
    private readonly int _maxTokens;
    private int _consumed;

    public TokenBudget(int maxTokens) => _maxTokens = maxTokens;

    /// <summary>True when consumed tokens are below 90% of the budget.</summary>
    public bool HasRoom => _consumed < _maxTokens * 0.9;

    /// <summary>True when consumed tokens have reached the budget ceiling.</summary>
    public bool Exhausted => _consumed >= _maxTokens;

    /// <summary>Total tokens consumed so far.</summary>
    public int Consumed => _consumed;

    /// <summary>True when any content was truncated due to budget exhaustion.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Unconditionally records the token cost of <paramref name="content"/>.</summary>
    public void Consume(string content) => _consumed += EstimateTokens(content);

    /// <summary>
    /// Consumes <paramref name="content"/> up to the remaining budget.
    /// Returns the full content if it fits; truncates with a marker and sets
    /// <see cref="Truncated"/> if it does not.
    /// </summary>
    public string ConsumeWithLimit(string content)
    {
        var tokens = EstimateTokens(content);
        if (_consumed + tokens <= _maxTokens)
        {
            _consumed += tokens;
            return content;
        }

        var charBudget = Math.Max(0, (_maxTokens - _consumed) * 4);
        Truncated = true;
        _consumed = _maxTokens;

        if (charBudget == 0) return "\n[truncated — increase max_tokens for more detail]\n";

        var truncated = content[..Math.Min(content.Length, charBudget)];
        // Trim to last newline so we don't cut mid-line
        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > 0) truncated = truncated[..lastNewline];
        return truncated + "\n\n[truncated — increase max_tokens for more detail]";
    }

    private static int EstimateTokens(string text) => text.Length / 4;
}
