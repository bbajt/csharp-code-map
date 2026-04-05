namespace CodeMap.Storage.Engine;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Maps v2 binary records back to Core domain types.
/// Used by CustomSymbolStore to return ISymbolStore-compatible results.
/// </summary>
internal static class RecordToCoreMappings
{
    // ── Reverse SymbolKind (v2 short → Core enum) ────────────────────────────

    public static SymbolKind ReverseSymbolKind(short kind) => kind switch
    {
        1  => SymbolKind.Class,
        2  => SymbolKind.Interface,
        3  => SymbolKind.Record,
        5  => SymbolKind.Struct,
        6  => SymbolKind.Enum,
        7  => SymbolKind.Delegate,
        8  => SymbolKind.Method,
        9  => SymbolKind.Constructor,
        10 => SymbolKind.Field,
        11 => SymbolKind.Property,
        12 => SymbolKind.Event,
        _  => SymbolKind.Class, // Unknown → Class fallback
    };

    // ── Reverse EdgeKind (v2 short → Core RefKind) ───────────────────────────

    public static RefKind ReverseEdgeKind(short edgeKind) => edgeKind switch
    {
        1  => RefKind.Call,
        2  => RefKind.Read,
        3  => RefKind.Write,
        4  => RefKind.Call,      // Inherits — no direct RefKind, map to Call
        5  => RefKind.Implementation,
        6  => RefKind.Override,
        7  => RefKind.Read,      // UsesType → Read
        _  => RefKind.Call,
    };

    // ── Reverse Accessibility (v2 short → string) ────────────────────────────

    public static string ReverseAccessibility(short accessibility) => accessibility switch
    {
        7 => "public",
        4 => "internal",
        3 => "protected",
        1 => "private",
        2 => "protected internal",
        6 => "private protected",
        _ => "public",
    };

    // ── Reverse Confidence ────────────────────────────────────────────────────

    public static Confidence ReverseConfidence(int confidence) => confidence switch
    {
        0 => Confidence.High,
        1 => Confidence.Medium,
        2 => Confidence.Low,
        _ => Confidence.High,
    };

    // ── Reverse FactKind ─────────────────────────────────────────────────────

    public static FactKind ReverseFactKind(int factKind) => (FactKind)factKind;

    // ── Reverse ResolutionState ──────────────────────────────────────────────

    public static ResolutionState ReverseResolutionState(short state) => state switch
    {
        0 => ResolutionState.Resolved,
        1 => ResolutionState.Unresolved,
        _ => ResolutionState.Resolved,
    };

    // ── SymbolRecord → SymbolCard ────────────────────────────────────────────

    /// <summary>Baseline-only path: all StringIds within baseline range.</summary>
    public static SymbolCard ToSymbolCard(in SymbolRecord sym, EngineBaselineReader reader)
        => ToSymbolCard(sym, reader, reader.ResolveString);

    /// <summary>Overlay-aware path: resolveString can resolve both baseline and overlay StringIds.</summary>
    public static SymbolCard ToSymbolCard(in SymbolRecord sym, EngineBaselineReader reader, Func<int, string> resolveString)
    {
        var symbolId = resolveString(sym.FqnStringId); // doc-comment ID: T:Ns.Class
        var isVb = IsVbSymbol(sym, reader);
        var displayFqn = DocIdToDisplayFqn(symbolId, isVb); // display format: global::Ns.Class or Global.Ns.Class
        var displayName = resolveString(sym.DisplayNameStringId);
        var ns = sym.NamespaceStringId > 0 ? resolveString(sym.NamespaceStringId) : "";
        var stableIdStr = sym.StableIdStringId > 0 ? resolveString(sym.StableIdStringId) : null;
        var filePath = sym.FileIntId > 0 ? resolveString(reader.GetFileByIntId(sym.FileIntId).PathStringId) : "";
        var visibility = ReverseAccessibility(sym.Accessibility);
        var kind = ReverseSymbolKind(sym.Kind);

        // Reconstruct containing type from container (positive IntIds = baseline, skip negative = overlay-local)
        string? containingType = null;
        if (sym.ContainerIntId > 0 && sym.ContainerIntId <= reader.SymbolCount)
        {
            try
            {
                ref readonly var container = ref reader.GetSymbolByIntId(sym.ContainerIntId);
                containingType = resolveString(container.DisplayNameStringId);
            }
            catch (StorageFormatException) { /* ContainerIntId out of range — skip */ }
        }

        // Hydrate facts
        var factRecords = reader.GetFactsBySymbol(sym.SymbolIntId);
        var facts = new List<Fact>(factRecords.Count);
        foreach (var fr in factRecords)
        {
            var primary = fr.PrimaryStringId > 0 ? resolveString(fr.PrimaryStringId) : "";
            var secondary = fr.SecondaryStringId > 0 ? resolveString(fr.SecondaryStringId) : "";
            var value = string.IsNullOrEmpty(secondary) ? primary : $"{primary}|{secondary}";
            facts.Add(new Fact(ReverseFactKind(fr.FactKind), value, null));
        }

        var card = new SymbolCard(
            SymbolId: SymbolId.From(symbolId),
            FullyQualifiedName: displayFqn,
            Kind: kind,
            Signature: $"{visibility} {kind.ToString().ToLowerInvariant()} {displayName}",
            Documentation: null,
            Namespace: ns,
            ContainingType: containingType,
            FilePath: FilePath.From(filePath.Length > 0 ? filePath : "unknown"),
            SpanStart: sym.SpanStart,
            SpanEnd: sym.SpanEnd,
            Visibility: visibility,
            CallsTop: [],
            Facts: facts,
            SideEffects: [],
            ThrownExceptions: [],
            Evidence: [],
            Confidence: Confidence.High,
            StableId: stableIdStr != null ? new StableId(stableIdStr) : null);

        return card with { IsDecompiled = (sym.Flags & (1 << 7)) != 0 ? 1 : 0 };
    }

    // ── SymbolRecord → SymbolSearchHit ───────────────────────────────────────

    public static SymbolSearchHit ToSearchHit(in SymbolRecord sym, EngineBaselineReader reader, double score)
    {
        var symbolId = reader.ResolveString(sym.FqnStringId);
        var displayFqn = DocIdToDisplayFqn(symbolId, IsVbSymbol(sym, reader));
        var displayName = reader.ResolveString(sym.DisplayNameStringId);
        var filePath = sym.FileIntId > 0 ? reader.ResolveString(reader.GetFileByIntId(sym.FileIntId).PathStringId) : "";

        return new SymbolSearchHit(
            SymbolId: SymbolId.From(symbolId),
            FullyQualifiedName: displayFqn,
            Kind: ReverseSymbolKind(sym.Kind),
            Signature: displayName,
            DocumentationSnippet: null,
            FilePath: FilePath.From(filePath.Length > 0 ? filePath : "unknown"),
            Line: sym.SpanStart,
            Score: score);
    }

    // ── SymbolRecord → SymbolSummary ─────────────────────────────────────────

    public static SymbolSummary ToSummary(in SymbolRecord sym, EngineBaselineReader reader)
    {
        var symbolId = reader.ResolveString(sym.FqnStringId);
        var displayFqn = DocIdToDisplayFqn(symbolId, IsVbSymbol(sym, reader));
        var displayName = reader.ResolveString(sym.DisplayNameStringId);
        var stableIdStr = sym.StableIdStringId > 0 ? reader.ResolveString(sym.StableIdStringId) : null;

        return new SymbolSummary(
            SymbolId: SymbolId.From(symbolId),
            StableId: stableIdStr != null ? new StableId(stableIdStr) : null,
            FullyQualifiedName: displayFqn,
            Signature: displayName,
            Visibility: ReverseAccessibility(sym.Accessibility),
            Kind: ReverseSymbolKind(sym.Kind));
    }

    // ── EdgeRecord → StoredReference ─────────────────────────────────────────

    /// <summary>Baseline-only path.</summary>
    public static StoredReference ToStoredReference(in EdgeRecord edge, EngineBaselineReader reader)
        => ToStoredReference(edge, reader, reader.ResolveString);

    /// <summary>Overlay-aware path: resolveString handles both baseline and overlay StringIds.</summary>
    public static StoredReference ToStoredReference(in EdgeRecord edge, EngineBaselineReader reader, Func<int, string> resolveString)
    {
        var fromFqn = edge.FromSymbolIntId > 0 && edge.FromSymbolIntId <= reader.SymbolCount
            ? resolveString(reader.GetSymbolByIntId(edge.FromSymbolIntId).FqnStringId) : "";
        var filePath = edge.FileIntId > 0 && edge.FileIntId <= reader.FileCount
            ? resolveString(reader.GetFileByIntId(edge.FileIntId).PathStringId) : "";
        var toName = edge.ToNameStringId > 0 ? resolveString(edge.ToNameStringId) : null;

        return new StoredReference(
            Kind: ReverseEdgeKind(edge.EdgeKind),
            FromSymbol: fromFqn.Length > 0 ? SymbolId.From(fromFqn) : SymbolId.Empty,
            FilePath: FilePath.From(filePath.Length > 0 ? filePath : "unknown"),
            LineStart: edge.SpanStart,
            LineEnd: edge.SpanEnd,
            Excerpt: null,
            ResolutionState: ReverseResolutionState(edge.ResolutionState),
            ToName: toName);
    }

    // ── EdgeRecord → StoredOutgoingReference ─────────────────────────────────

    /// <summary>Baseline-only path.</summary>
    public static StoredOutgoingReference ToOutgoingReference(in EdgeRecord edge, EngineBaselineReader reader)
        => ToOutgoingReference(edge, reader, reader.ResolveString);

    /// <summary>Overlay-aware path: resolveString handles both baseline and overlay StringIds.</summary>
    public static StoredOutgoingReference ToOutgoingReference(in EdgeRecord edge, EngineBaselineReader reader, Func<int, string> resolveString)
    {
        var toFqn = edge.ToSymbolIntId > 0 && edge.ToSymbolIntId <= reader.SymbolCount
            ? resolveString(reader.GetSymbolByIntId(edge.ToSymbolIntId).FqnStringId) : "";
        var filePath = edge.FileIntId > 0 && edge.FileIntId <= reader.FileCount
            ? resolveString(reader.GetFileByIntId(edge.FileIntId).PathStringId) : "";
        var toName = edge.ToNameStringId > 0 ? resolveString(edge.ToNameStringId) : null;

        return new StoredOutgoingReference(
            Kind: ReverseEdgeKind(edge.EdgeKind),
            ToSymbol: toFqn.Length > 0 ? SymbolId.From(toFqn) : SymbolId.Empty,
            FilePath: FilePath.From(filePath.Length > 0 ? filePath : "unknown"),
            LineStart: edge.SpanStart,
            LineEnd: edge.SpanEnd,
            ResolutionState: ReverseResolutionState(edge.ResolutionState),
            ToName: toName);
    }

    // ── FactRecord → StoredFact ──────────────────────────────────────────────

    /// <summary>Baseline-only path.</summary>
    public static StoredFact ToStoredFact(in FactRecord fact, EngineBaselineReader reader)
        => ToStoredFact(fact, reader, reader.ResolveString);

    /// <summary>Overlay-aware path: resolveString handles both baseline and overlay StringIds.</summary>
    public static StoredFact ToStoredFact(in FactRecord fact, EngineBaselineReader reader, Func<int, string> resolveString)
    {
        var ownerFqn = fact.OwnerSymbolIntId > 0 && fact.OwnerSymbolIntId <= reader.SymbolCount
            ? resolveString(reader.GetSymbolByIntId(fact.OwnerSymbolIntId).FqnStringId) : "";
        var stableIdStr = fact.OwnerSymbolIntId > 0 && fact.OwnerSymbolIntId <= reader.SymbolCount
            ? resolveString(reader.GetSymbolByIntId(fact.OwnerSymbolIntId).StableIdStringId) : null;
        var filePath = fact.FileIntId > 0 && fact.FileIntId <= reader.FileCount
            ? resolveString(reader.GetFileByIntId(fact.FileIntId).PathStringId) : "";
        var primary = fact.PrimaryStringId > 0 ? resolveString(fact.PrimaryStringId) : "";
        var secondary = fact.SecondaryStringId > 0 ? resolveString(fact.SecondaryStringId) : "";
        var value = string.IsNullOrEmpty(secondary) ? primary : $"{primary}|{secondary}";

        return new StoredFact(
            SymbolId: SymbolId.From(ownerFqn.Length > 0 ? ownerFqn : "unknown"),
            StableId: stableIdStr != null ? new StableId(stableIdStr) : null,
            Kind: ReverseFactKind(fact.FactKind),
            Value: value,
            FilePath: FilePath.From(filePath.Length > 0 ? filePath : "unknown"),
            LineStart: fact.SpanStart,
            LineEnd: fact.SpanEnd,
            Confidence: ReverseConfidence(fact.Confidence));
    }

    // ── Doc-comment ID → display FQN conversion ─────────────────────────────

    /// <summary>
    /// Converts a doc-comment ID (e.g., "T:Ns.Class", "M:Ns.Class.Method(params)")
    /// to a display FQN (e.g., "global::Ns.Class", "global::Ns.Class.Method(params)").
    /// This matches what SQLite's BaselineStore stores as FullyQualifiedName.
    /// </summary>
    internal static bool IsVbSymbol(in SymbolRecord sym, EngineBaselineReader reader)
    {
        if (sym.FileIntId < 1 || sym.FileIntId > reader.FileCount) return false;
        return reader.GetFileByIntId(sym.FileIntId).Language == 2; // 2 = VisualBasic
    }

    internal static string DocIdToDisplayFqn(string docId, bool isVb = false)
    {
        if (string.IsNullOrEmpty(docId)) return docId;
        // Strip doc-comment prefix: "T:", "M:", "P:", "F:", "E:"
        var body = docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;
        // VB.NET uses "Global." prefix; C# uses "global::"
        return isVb ? "Global." + body : "global::" + body;
    }
}
