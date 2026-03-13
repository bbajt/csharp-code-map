namespace CodeMap.Query;

using CodeMap.Core.Errors;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Shared algorithm for symbols.get_context: collect primary card + code + callee cards + code.
/// Called by both QueryEngine (committed mode) and MergedQueryEngine (workspace mode).
/// </summary>
internal static class ContextBuilder
{
    private const int PrimaryMaxLines = 200;
    private const int CalleeMaxLines = 50;

    /// <summary>
    /// Collects the primary symbol and its callees using the provided engine instance.
    /// The engine handles routing (committed / workspace / ephemeral) transparently.
    /// </summary>
    internal static async Task<Result<ContextData, CodeMapError>> CollectAsync(
        IQueryEngine engine,
        RoutingContext routing,
        SymbolId symbolId,
        int calleeDepth,
        int maxCallees,
        bool includeCode,
        CancellationToken ct)
    {
        // 1. Get primary card
        var primaryCardResult = await engine.GetSymbolCardAsync(routing, symbolId, ct).ConfigureAwait(false);
        if (primaryCardResult.IsFailure)
            return Result<ContextData, CodeMapError>.Failure(primaryCardResult.Error);

        var primaryCard = primaryCardResult.Value.Data;

        // 2. Read primary source code (max 200 lines)
        string? primaryCode = null;
        bool primaryTruncated = false;
        if (includeCode && primaryCard.SpanStart > 0 && primaryCard.SpanEnd > 0)
        {
            var spanResult = await engine.GetDefinitionSpanAsync(
                routing, symbolId, maxLines: PrimaryMaxLines, contextLines: 0, ct).ConfigureAwait(false);
            if (spanResult.IsSuccess)
            {
                primaryCode = spanResult.Value.Data.Content;
                primaryTruncated = spanResult.Value.Data.Truncated;
            }
        }

        // 3. Get callees (if depth > 0)
        var calleeCards = new List<SymbolCardWithCode>();
        int totalCalleesFound = 0;

        if (calleeDepth > 0)
        {
            var clampedMax = Math.Clamp(maxCallees, 0, 25);
            var calleesResult = await engine.GetCalleesAsync(
                routing, symbolId, calleeDepth, limitPerLevel: clampedMax, budgets: null, ct).ConfigureAwait(false);

            if (calleesResult.IsSuccess)
            {
                // Nodes includes the root at depth=0 — skip it; only depth>0 are actual callees
                var calleeNodes = calleesResult.Value.Data.Nodes
                    .Where(n => n.Depth > 0)
                    .Take(clampedMax)
                    .ToList();
                // TotalNodesFound already excludes the root node (GraphTraverser convention)
                totalCalleesFound = calleesResult.Value.Data.TotalNodesFound;

                foreach (var node in calleeNodes)
                {
                    var calleeCardResult = await engine.GetSymbolCardAsync(routing, node.SymbolId, ct).ConfigureAwait(false);
                    if (calleeCardResult.IsFailure) continue;

                    var calleeCard = calleeCardResult.Value.Data;
                    string? calleeCode = null;
                    bool calleeTruncated = false;

                    if (includeCode && calleeCard.SpanStart > 0 && calleeCard.SpanEnd > 0)
                    {
                        var calleeSpanResult = await engine.GetDefinitionSpanAsync(
                            routing, node.SymbolId, maxLines: CalleeMaxLines, contextLines: 0, ct).ConfigureAwait(false);
                        if (calleeSpanResult.IsSuccess)
                        {
                            calleeCode = calleeSpanResult.Value.Data.Content;
                            calleeTruncated = calleeSpanResult.Value.Data.Truncated;
                        }
                    }

                    calleeCards.Add(new SymbolCardWithCode(calleeCard, calleeCode, calleeTruncated));
                }
            }
        }

        return Result<ContextData, CodeMapError>.Success(new ContextData(
            new SymbolCardWithCode(primaryCard, primaryCode, primaryTruncated),
            calleeCards,
            totalCalleesFound));
    }

    /// <summary>Renders a markdown summary of the symbol context.</summary>
    internal static string RenderMarkdown(
        SymbolCard primary,
        string? primaryCode,
        IReadOnlyList<SymbolCardWithCode> callees)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"# {primary.FullyQualifiedName}");
        sb.AppendLine();
        sb.AppendLine($"**File:** {primary.FilePath.Value} (lines {primary.SpanStart}–{primary.SpanEnd})");
        sb.AppendLine($"**Signature:** `{primary.Signature}`");

        if (primary.StableId is not null)
            sb.AppendLine($"**Stable ID:** {primary.StableId.Value.Value}");

        if (!string.IsNullOrEmpty(primary.Documentation))
        {
            sb.AppendLine();
            sb.AppendLine(primary.Documentation);
        }

        if (primary.Facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Facts");
            foreach (var fact in primary.Facts)
                sb.AppendLine($"- {fact.Kind}: {fact.Value}");
        }

        if (primaryCode is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Source Code");
            sb.AppendLine("```csharp");
            sb.Append(primaryCode);
            if (!primaryCode.EndsWith('\n'))
                sb.AppendLine();
            sb.AppendLine("```");
        }

        if (callees.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Callees ({callees.Count})");

            foreach (var callee in callees)
            {
                sb.AppendLine();
                sb.AppendLine($"### {callee.Card.FullyQualifiedName}");
                sb.AppendLine($"**File:** {callee.Card.FilePath.Value} (lines {callee.Card.SpanStart}–{callee.Card.SpanEnd})");
                sb.AppendLine($"**Signature:** `{callee.Card.Signature}`");

                if (callee.SourceCode is not null)
                {
                    sb.AppendLine("```csharp");
                    sb.Append(callee.SourceCode);
                    if (!callee.SourceCode.EndsWith('\n'))
                        sb.AppendLine();
                    sb.AppendLine("```");
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>Intermediate result from <see cref="ContextBuilder.CollectAsync"/>.</summary>
internal record ContextData(
    SymbolCardWithCode Primary,
    List<SymbolCardWithCode> Callees,
    int TotalCalleesFound
);
