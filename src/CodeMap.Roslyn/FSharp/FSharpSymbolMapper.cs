namespace CodeMap.Roslyn.FSharp;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using global::FSharp.Compiler.Symbols;

/// <summary>
/// Maps FSharp.Compiler.Service entities and members to CodeMap SymbolCards.
/// Uses XmlDocSig as the SymbolId bridge — same format as Roslyn doc-comment IDs.
/// </summary>
internal static class FSharpSymbolMapper
{
    public static (IReadOnlyList<SymbolCard> Cards, IReadOnlyDictionary<string, StableId> StableIdMap)
        ExtractSymbols(IReadOnlyList<FSharpFileAnalysis> analyses, string projectName, string solutionDir)
    {
        var cards = new List<SymbolCard>();
        var stableIdMap = new Dictionary<string, StableId>(StringComparer.Ordinal);

        // Normalize solutionDir for repo-relative path construction
        string normalizedDir = solutionDir.Replace('\\', '/').TrimEnd('/') + '/';

        foreach (var analysis in analyses)
        {
            if (analysis.CheckResults is null) continue;

            var filePath = MakeRepoRelative(analysis.FilePath, normalizedDir);

            try
            {
                foreach (var entity in analysis.CheckResults.PartialAssemblySignature.Entities)
                {
                    WalkEntity(entity, filePath, projectName, normalizedDir, cards, stableIdMap);
                }
            }
            catch
            {
                // PartialAssemblySignature can throw "not available" when type-check
                // produced degraded results. Skip semantic extraction for this file.
            }
        }

        return (cards, stableIdMap);
    }

    private static void WalkEntity(
        FSharpEntity entity,
        string filePath,
        string projectName,
        string normalizedDir,
        List<SymbolCard> cards,
        Dictionary<string, StableId> stableIdMap)
    {
        // Skip compiler-generated / internal plumbing
        if (entity.IsCompilerGenerated()) return;

        // Namespaces have empty XmlDocSig — skip card creation but recurse into nested entities
        // (e.g. `namespace Fake.Core` wraps `module Target =` which contains the real symbols)
        if (entity.IsNamespace)
        {
            foreach (var nested in entity.NestedEntities)
                WalkEntity(nested, filePath, projectName, normalizedDir, cards, stableIdMap);
            return;
        }

        // Get entity XmlDocSig — may be empty or throw for [<RequireQualifiedAccess>] modules
        string xmlDocSig;
        try { xmlDocSig = entity.XmlDocSig; }
        catch { xmlDocSig = ""; } // treat as empty — still walk members + nested below

        // Get entity metadata safely — FCS can throw on some entities (e.g. [<RequireQualifiedAccess>])
        string ns = "", displayName = "", fqn = "";
        try { ns = entity.AccessPath; displayName = entity.DisplayName; fqn = entity.FullName; }
        catch { /* best-effort — use empty strings, still walk members + nested */ }

        if (!string.IsNullOrEmpty(xmlDocSig))
        {
            var kind = MapEntityKind(entity);
            var doc = GetDocumentation(entity);
            var visibility = MapAccessibility(entity);

            var (spanStart, spanEnd) = GetEntitySpan(entity);

            var card = new SymbolCard(
                SymbolId: SymbolId.From(xmlDocSig),
                FullyQualifiedName: fqn,
                Kind: kind,
                Signature: $"{visibility} {kind.ToString().ToLowerInvariant()} {displayName}",
                Documentation: doc,
                Namespace: ns,
                ContainingType: null,
                FilePath: FilePath.From(filePath),
                SpanStart: spanStart,
                SpanEnd: spanEnd,
                Visibility: visibility,
                CallsTop: [],
                Facts: [],
                SideEffects: [],
                ThrownExceptions: [],
                Evidence: [],
                Confidence: Confidence.High,
                StableId: null);

            var stableId = ComputeFSharpStableId(xmlDocSig, kind, projectName);
            stableIdMap[xmlDocSig] = stableId;
            card = card with { StableId = stableId };
            cards.Add(card);
        }

        // Walk members — always, even when entity XmlDocSig is empty/throws.
        // [<RequireQualifiedAccess>] modules return empty XmlDocSig for the entity itself
        // but their member functions have independent XmlDocSigs (e.g. M:Fake.Core.Target.run).
        IEnumerable<FSharpMemberOrFunctionOrValue> members;
        try { members = entity.MembersFunctionsAndValues; }
        catch { members = []; } // MembersFunctionsAndValues itself can throw on complex entities

        foreach (var member in members)
        {
            try
            {
                if (member.IsCompilerGenerated) continue;

                string memberDocSig;
                try { memberDocSig = member.XmlDocSig; }
                catch { continue; } // XmlDocSig can throw on assembly resolution failure
                if (string.IsNullOrEmpty(memberDocSig)) continue;
                if (stableIdMap.ContainsKey(memberDocSig)) continue; // dedup

                var memberKind = MapMemberKind(member);
                var memberVisibility = member.Accessibility.IsPublic ? "public"
                    : member.Accessibility.IsInternal ? "internal"
                    : member.Accessibility.IsPrivate ? "private"
                    : "internal";

                var memberDoc = GetMemberDocumentation(member);
                var sig = FormatMemberSignature(member, memberVisibility, memberKind);
                var (mStart, mEnd) = GetMemberSpan(member);

                var memberCard = new SymbolCard(
                    SymbolId: SymbolId.From(memberDocSig),
                    FullyQualifiedName: $"{fqn}.{member.DisplayName}",
                    Kind: memberKind,
                    Signature: sig,
                    Documentation: memberDoc,
                    Namespace: ns,
                    ContainingType: displayName,
                    FilePath: FilePath.From(filePath),
                    SpanStart: mStart,
                    SpanEnd: mEnd,
                    Visibility: memberVisibility,
                    CallsTop: [],
                    Facts: [],
                    SideEffects: [],
                    ThrownExceptions: [],
                    Evidence: [],
                    Confidence: Confidence.High,
                    StableId: null);

                var memberStableId = ComputeFSharpStableId(memberDocSig, memberKind, projectName);
                stableIdMap[memberDocSig] = memberStableId;
                memberCard = memberCard with { StableId = memberStableId };
                cards.Add(memberCard);
            }
            catch { /* member processing failed — skip this member, continue with others */ }
        }

        // Walk nested entities (nested modules)
        foreach (var nested in entity.NestedEntities)
        {
            var nestedFile = filePath;
            // Nested entities may be in the same file
            WalkEntity(nested, nestedFile, projectName, normalizedDir, cards, stableIdMap);
        }
    }

    // ── Kind mapping ────────────────────────────────────────────────────────

    private static SymbolKind MapEntityKind(FSharpEntity entity)
    {
        if (entity.IsInterface) return SymbolKind.Interface;
        if (entity.IsFSharpRecord) return SymbolKind.Record;
        if (entity.IsEnum) return SymbolKind.Enum;
        if (entity.IsValueType) return SymbolKind.Struct;
        // Modules and DUs map to Class (they compile to static/abstract classes)
        return SymbolKind.Class;
    }

    private static SymbolKind MapMemberKind(FSharpMemberOrFunctionOrValue member)
    {
        if (member.IsProperty) return SymbolKind.Property;
        if (member.IsEvent) return SymbolKind.Event;
        return SymbolKind.Method;
    }

    // ── Accessibility ───────────────────────────────────────────────────────

    private static string MapAccessibility(FSharpEntity entity)
    {
        if (entity.Accessibility.IsPublic) return "public";
        if (entity.Accessibility.IsInternal) return "internal";
        if (entity.Accessibility.IsPrivate) return "private";
        return "internal";
    }

    // ── Documentation ───────────────────────────────────────────────────────

    private static string? GetDocumentation(FSharpEntity entity)
    {
        try
        {
            // XmlDocSig is always available; for full doc text, try XmlDoc
            var xmlDoc = entity.XmlDoc;
            if (xmlDoc != null)
            {
                var text = xmlDoc.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }
        catch { /* XmlDoc can throw on some entities */ }
        return null;
    }

    private static string? GetMemberDocumentation(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var xmlDoc = member.XmlDoc;
            if (xmlDoc != null)
            {
                var text = xmlDoc.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }
        catch { /* XmlDoc can throw on some members */ }
        return null;
    }

    // ── Signatures ──────────────────────────────────────────────────────────

    private static string FormatMemberSignature(
        FSharpMemberOrFunctionOrValue member, string visibility, SymbolKind kind)
    {
        return $"{visibility} {kind.ToString().ToLowerInvariant()} {member.DisplayName}";
    }

    // ── Source spans ────────────────────────────────────────────────────────

    private static (int Start, int End) GetEntitySpan(FSharpEntity entity)
    {
        try
        {
            var range = entity.DeclarationLocation;
            return (range.StartLine, range.EndLine);
        }
        catch { return (0, 0); }
    }

    private static (int Start, int End) GetMemberSpan(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var range = member.DeclarationLocation;
            return (range.StartLine, range.EndLine);
        }
        catch { return (0, 0); }
    }

    // ── StableId (FQN-based fingerprint — same pattern as v2 engine) ───────

    internal static StableId ComputeFSharpStableId(string xmlDocSig, SymbolKind kind, string projectName)
    {
        var input = $"{kind}|{xmlDocSig}|{projectName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new StableId($"sym_{Convert.ToHexStringLower(hash)[..16]}");
    }

    // ── Path helpers ────────────────────────────────────────────────────────

    internal static string MakeRepoRelative(string absolutePath, string normalizedDir)
    {
        var normalized = absolutePath.Replace('\\', '/');
        if (normalized.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
            return normalized[normalizedDir.Length..];
        return Path.GetFileName(normalized);
    }
}

// ── Extension method for compiler-generated check ───────────────────────────

internal static class FSharpEntityExtensions
{
    public static bool IsCompilerGenerated(this FSharpEntity entity)
    {
        try
        {
            // F# generates backing types for DU cases, closure types, etc.
            var name = entity.DisplayName;
            if (name.StartsWith("<") || name.StartsWith("@")) return true;
            return false;
        }
        catch { return true; }
    }
}
