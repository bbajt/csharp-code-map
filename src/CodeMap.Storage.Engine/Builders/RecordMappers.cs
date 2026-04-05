namespace CodeMap.Storage.Engine;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Maps Core domain types to v2 binary record types.
/// Handles string interning, IntId assignment, and enum translation.
/// </summary>
internal static class RecordMappers
{
    // ── SymbolKind mapping (Core enum → v2 spec §5.2) ───────────────────────

    public static short MapSymbolKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Class       => 1,
        SymbolKind.Interface   => 2,
        SymbolKind.Record      => 3,
        SymbolKind.Struct      => 5,
        SymbolKind.Enum        => 6,
        SymbolKind.Delegate    => 7,
        SymbolKind.Method      => 8,
        SymbolKind.Constructor => 9,
        SymbolKind.Field       => 10,
        SymbolKind.Property    => 11,
        SymbolKind.Event       => 12,
        SymbolKind.Constant    => 10, // stored as Field
        SymbolKind.Indexer     => 11, // stored as Property
        SymbolKind.Operator    => 8,  // stored as Method
        _                      => 0,  // Unknown
    };

    // ── Accessibility mapping ────────────────────────────────────────────────

    public static short MapAccessibility(string? visibility) => visibility?.ToLowerInvariant() switch
    {
        "public"               => 7,
        "internal"             => 4,
        "protected"            => 3,
        "private"              => 1,
        "protected internal"   => 2,
        "private protected"    => 6,
        _                      => 0, // NotApplicable
    };

    // ── RefKind → EdgeKind mapping (Core enum → v2 spec §8.2) ───────────────

    public static short MapEdgeKind(RefKind kind) => kind switch
    {
        RefKind.Call           => 1,
        RefKind.Read           => 2,
        RefKind.Write          => 3,
        RefKind.Instantiate    => 1, // treated as Call
        RefKind.Override       => 6,
        RefKind.Implementation => 5,
        _                      => 0,
    };

    // ── ResolutionState mapping ──────────────────────────────────────────────

    public static short MapResolutionState(ResolutionState state) => state switch
    {
        ResolutionState.Resolved   => 0,
        ResolutionState.Unresolved => 1,
        _                          => 0,
    };

    // ── FactKind mapping (direct cast, values already match v2 §9.2) ────────

    public static int MapFactKind(FactKind kind) => (int)kind;

    // ── Confidence mapping ───────────────────────────────────────────────────

    public static int MapConfidence(Confidence conf) => conf switch
    {
        Confidence.High   => 0,
        Confidence.Medium => 1,
        Confidence.Low    => 2,
        _                 => 0,
    };

    // ── v2 degraded StableId (DD-1: hash of Kind + FQN + ProjectName) ────────

    public static string ComputeDegradedStableId(SymbolKind kind, string fqn, string? projectName)
    {
        var input = $"{kind}\x00{fqn}\x00{projectName ?? ""}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return "sym_" + Convert.ToHexString(hash[..8]).ToLowerInvariant();
    }

    // ── Flags bitmask builder ────────────────────────────────────────────────

    public static int BuildSymbolFlags(SymbolCard card)
    {
        var flags = 0;
        if (card.IsDecompiled > 0) flags |= 1 << 7;
        // Other flags (static, abstract, virtual, etc.) not available on SymbolCard
        // — would require Roslyn extraction extension. Phase 3 stores 0 for these.
        return flags;
    }

    // ── File language detection ──────────────────────────────────────────────

    public static short DetectLanguage(string filePath)
    {
        if (filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return 1;  // CSharp
        if (filePath.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)) return 2;  // VisualBasic
        if (filePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)) return 3;  // FSharp
        return 0; // Unknown
    }

    // ── Content hash splitting ───────────────────────────────────────────────

    public static (long High, long Low) SplitSha256(string sha256Hex)
    {
        if (string.IsNullOrEmpty(sha256Hex) || sha256Hex.Length < 32)
            return (0, 0);
        var high = Convert.ToInt64(sha256Hex[..16], 16);
        var low = Convert.ToInt64(sha256Hex[16..32], 16);
        return (high, low);
    }

    // ── Fact Value splitting (pipe-separated) ────────────────────────────────

    public static (string Primary, string Secondary) SplitFactValue(string value)
    {
        var pipeIdx = value.IndexOf('|');
        if (pipeIdx < 0) return (value, "");
        return (value[..pipeIdx], value[(pipeIdx + 1)..]);
    }
}
