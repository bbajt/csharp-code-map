namespace CodeMap.Core.Models;

using CodeMap.Core.Enums;
using CodeMap.Core.Types;

/// <summary>Response payload for the <c>index.diff</c> MCP tool.</summary>
public record DiffResponse(
    /// <summary>The "before" commit SHA.</summary>
    CommitSha FromCommit,
    /// <summary>The "after" commit SHA.</summary>
    CommitSha ToCommit,
    /// <summary>Rendered markdown summary of all changes.</summary>
    string Markdown,
    /// <summary>Aggregate counts of all detected changes.</summary>
    DiffStats Stats,
    /// <summary>Symbol-level changes (added, removed, renamed, signature changed).</summary>
    IReadOnlyList<SymbolDiff> SymbolChanges,
    /// <summary>Fact-level changes (endpoints, config keys, DB tables, DI registrations).</summary>
    IReadOnlyList<FactDiff> FactChanges
);

/// <summary>Aggregate counts for a semantic diff result.</summary>
public record DiffStats(
    int SymbolsAdded,
    int SymbolsRemoved,
    int SymbolsRenamed,
    int SymbolsSignatureChanged,
    int EndpointsAdded,
    int EndpointsRemoved,
    int ConfigKeysAdded,
    int ConfigKeysRemoved,
    int DbTablesAdded,
    int DbTablesRemoved,
    int DiRegistrationsAdded,
    int DiRegistrationsRemoved
);

/// <summary>A single symbol-level change between two baselines.</summary>
public record SymbolDiff(
    /// <summary>"Added", "Removed", "Renamed", "SignatureChanged", or "VisibilityChanged".</summary>
    string ChangeType,
    /// <summary>The symbol in the FROM baseline. Null when <see cref="ChangeType"/> is "Added".</summary>
    SymbolId? FromSymbolId,
    /// <summary>The symbol in the TO baseline. Null when <see cref="ChangeType"/> is "Removed".</summary>
    SymbolId? ToSymbolId,
    /// <summary>Shared stable identity when the match was made by stable_id. Null for FQN-matched symbols.</summary>
    StableId? StableId,
    /// <summary>Signature in the FROM baseline. Null when <see cref="ChangeType"/> is "Added".</summary>
    string? FromSignature,
    /// <summary>Signature in the TO baseline. Null when <see cref="ChangeType"/> is "Removed".</summary>
    string? ToSignature,
    /// <summary>Symbol kind (same in both commits for matched symbols).</summary>
    SymbolKind Kind
);

/// <summary>A single fact-level change between two baselines.</summary>
public record FactDiff(
    /// <summary>"Added", "Removed", or "Changed".</summary>
    string ChangeType,
    /// <summary>The category of the fact (Route, Config, DbTable, DiRegistration, etc.).</summary>
    FactKind Kind,
    /// <summary>Fact value in the FROM baseline. Null when <see cref="ChangeType"/> is "Added".</summary>
    string? FromValue,
    /// <summary>Fact value in the TO baseline. Null when <see cref="ChangeType"/> is "Removed".</summary>
    string? ToValue
);
