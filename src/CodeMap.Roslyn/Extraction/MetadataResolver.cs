namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

/// <summary>
/// Level 1 lazy resolver: walks Roslyn <see cref="INamedTypeSymbol"/> metadata
/// for DLL-referenced types and inserts stub <see cref="SymbolCard"/> records
/// into the baseline DB on first miss.
/// </summary>
public sealed class MetadataResolver : IMetadataResolver
{
    private readonly IncrementalCompiler _compiler;
    private readonly ISymbolStore _store;
    private readonly ILogger<MetadataResolver> _logger;

    /// <summary>Maximum character count for stored decompiled source (500 KB).</summary>
    internal const int MaxDecompiledSourceChars = 512_000;

    /// <summary>
    /// Wall-clock timeout in seconds for a single <c>DecompileTypeAsString</c> call.
    /// Exposed as a field (not a const) so tests can reduce it without reflection.
    /// </summary>
    internal int DecompileTimeoutSeconds = 30;

    // Assembly name suffixes that contain no navigable API surface — skip them entirely.
    // Checked with EndsWith directly; a HashSet<string> adds no benefit for a 2-element predicate scan.
    private static bool IsExcludedAssembly(string name) =>
        name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".XmlSerializers", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new <see cref="MetadataResolver"/>.
    /// </summary>
    public MetadataResolver(
        IncrementalCompiler compiler,
        ISymbolStore store,
        ILogger<MetadataResolver> logger)
    {
        _compiler = compiler;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<int> TryResolveTypeAsync(
        SymbolId symbolId,
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        // 1. Get any loaded compilation from the IncrementalCompiler cache
        var compilation = await _compiler.GetMetadataCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null)
        {
            _logger.LogDebug("No cached compilation — cannot resolve {SymbolId}", symbolId);
            return 0;
        }

        // 2. Parse metadata name from FQN (strip doc-comment-id prefix, strip params)
        var metadataName = FqnToMetadataName(symbolId.Value);
        if (metadataName is null) return 0;

        // 3. Resolve the containing type from Roslyn metadata
        var typeSymbol = compilation.GetTypeByMetadataName(metadataName);
        if (typeSymbol is null)
        {
            _logger.LogDebug("Roslyn cannot resolve metadata name '{Name}'", metadataName);
            return 0;
        }

        // 4. Must be from a MetadataReference (not source) to avoid duplicating source symbols
        if (typeSymbol.Locations.Any(l => l.IsInSource))
        {
            _logger.LogDebug("Symbol {Name} is a source symbol — skipping lazy resolution", metadataName);
            return 0;
        }

        // 5. Check assembly exclusion list
        var assemblyName = typeSymbol.ContainingAssembly?.Name ?? string.Empty;
        if (IsExcludedAssembly(assemblyName))
        {
            _logger.LogDebug("Assembly '{Assembly}' is on exclusion list — skipping", assemblyName);
            return 0;
        }

        // 6. Walk INamedTypeSymbol.GetMembers() → produce SymbolCard stubs
        var stubs = BuildStubs(typeSymbol);
        if (stubs.Count == 0) return 0;

        // 7. Build type relations (base type + interfaces)
        var typeRelations = BuildTypeRelations(typeSymbol);

        // 8. Insert stubs with INSERT OR IGNORE (concurrent-safe)
        int inserted = await _store.InsertMetadataStubsAsync(repoId, commitSha, stubs, typeRelations, ct)
                                   .ConfigureAwait(false);

        // 9. Sync FTS
        if (inserted > 0)
            await _store.RebuildFtsAsync(repoId, commitSha, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Lazy-resolved {Count} metadata stubs for type '{Type}' from assembly '{Assembly}'",
            inserted, metadataName, assemblyName);

        return inserted;
    }

    /// <inheritdoc/>
    public async Task<string?> TryDecompileTypeAsync(
        SymbolId symbolId,
        RepoId repoId,
        CommitSha commitSha,
        CancellationToken ct = default)
    {
        // 1. Short-circuit: already at Level 2 — return existing virtual path
        var existing = await _store.GetSymbolAsync(repoId, commitSha, symbolId, ct).ConfigureAwait(false);
        if (existing is not null && existing.IsDecompiled == 2)
            return existing.FilePath.Value;

        // 2. Get any cached compilation
        var compilation = await _compiler.GetMetadataCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null) return null;

        // 3. Extract CLR type name from Roslyn doc-comment FQN
        var typeFqn = FqnToMetadataName(symbolId.Value);
        if (string.IsNullOrEmpty(typeFqn)) return null;

        // 4. Resolve INamedTypeSymbol via Roslyn
        var typeSymbol = compilation.GetTypeByMetadataName(typeFqn);
        if (typeSymbol is null) return null;

        // 5. Only decompile metadata (DLL) symbols — not source symbols
        if (typeSymbol.Locations.Any(l => l.IsInSource)) return null;

        // 6. Get the DLL path via the compilation's MetadataReference for this assembly
        var containingAssembly = typeSymbol.ContainingAssembly;
        if (containingAssembly is null) return null;

        string? dllPath = null;
        if (compilation.GetMetadataReference(containingAssembly)
            is Microsoft.CodeAnalysis.PortableExecutableReference peRef)
        {
            dllPath = peRef.FilePath;
        }
        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath)) return null;

        // 7. Decompile using ICSharpCode.Decompiler, bounded by a wall-clock timeout.
        // DecompileTypeAsString is synchronous with no cancellation support — wrap in
        // Task.Run so the caller can abandon the wait when the timeout fires.
        string decompiledSource;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(DecompileTimeoutSeconds));

            decompiledSource = await Task.Run(() =>
            {
                var settings = new ICSharpCode.Decompiler.DecompilerSettings
                {
                    ThrowOnAssemblyResolveErrors = false
                };
                var decompiler = new ICSharpCode.Decompiler.CSharp.CSharpDecompiler(dllPath, settings);
                var fullTypeName = new ICSharpCode.Decompiler.TypeSystem.FullTypeName(typeFqn);
                return decompiler.DecompileTypeAsString(fullTypeName);
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout fired — caller did not cancel; return null so the graph traversal
            // continues without this type's decompiled source.
            _logger.LogWarning(
                "ICSharpCode.Decompiler timed out after {Timeout} s for type {TypeFqn} in {DllPath}",
                DecompileTimeoutSeconds, typeFqn, dllPath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ICSharpCode.Decompiler failed for type {TypeFqn} in {DllPath}",
                typeFqn, dllPath);
            return null;
        }

        // 7b. Cap source size — generated or obfuscated types can produce multi-MB strings.
        if (decompiledSource.Length > MaxDecompiledSourceChars)
        {
            _logger.LogWarning(
                "Decompiled source for {TypeFqn} in {DllPath} exceeds {Max:N0} chars ({Actual:N0}); truncating before storage.",
                typeFqn, dllPath, MaxDecompiledSourceChars, decompiledSource.Length);
            decompiledSource = decompiledSource[..MaxDecompiledSourceChars];
        }

        // 8. Build virtual file path
        var assemblyName = containingAssembly.Name;
        var virtualPath = BuildVirtualPath(assemblyName, typeFqn);

        // 9. Extract cross-DLL refs from decompiled SyntaxTree
        IReadOnlyList<ExtractedReference>? decompiledRefs = null;
        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(decompiledSource, path: virtualPath);
            var inMemoryCompilation = CSharpCompilation.Create(
                assemblyName: $"__decompiled_{assemblyName}",
                syntaxTrees: [syntaxTree],
                references: compilation.References,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var allRefs = ReferenceExtractor.ExtractAll(inMemoryCompilation, solutionDir: "");

            // Filter to refs whose FromSymbol directly belongs to the decompiled type.
            // BelongsToType rejects members of nested types (dot before first '(' in the
            // member segment) while correctly accepting constructors and generic params.
            var typePrefix    = "M:" + typeFqn + ".";
            var typeCtorPrefix = "M:" + typeFqn + ".#";
            decompiledRefs = allRefs
                .Where(r => BelongsToType(r.FromSymbol.Value, typePrefix, typeCtorPrefix))
                .Select(r => r with { IsDecompiled = true })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ref extraction from decompiled SyntaxTree failed for {TypeFqn}", typeFqn);
            decompiledRefs = null;
        }

        // 10. Persist virtual file (and refs in same transaction)
        await _store.InsertVirtualFileAsync(repoId, commitSha, virtualPath, decompiledSource, decompiledRefs, ct)
                    .ConfigureAwait(false);
        await _store.UpgradeDecompiledSymbolAsync(repoId, commitSha, symbolId, virtualPath, ct)
                    .ConfigureAwait(false);

        return virtualPath;
    }

    /// <summary>
    /// Builds the virtual file path: <c>decompiled/{AssemblyName}/{TypeFqn with dots as /}.cs</c>
    /// </summary>
    private static string BuildVirtualPath(string assemblyName, string typeFqn)
    {
        var typePath = typeFqn.Replace('.', '/').Replace('+', '/');
        return $"decompiled/{assemblyName}/{typePath}.cs";
    }

    /// <summary>
    /// Converts a Roslyn doc-comment FQN to a CLR metadata name suitable for
    /// <c>Compilation.GetTypeByMetadataName</c>.
    /// </summary>
    internal static string? FqnToMetadataName(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return null;

        // Strip doc-comment-id prefix (M:, T:, P:, E:, F:)
        var name = fqn.Length > 2 && fqn[1] == ':' ? fqn[2..] : fqn;

        // Strip parameter list
        var parenIdx = name.IndexOf('(');
        if (parenIdx >= 0) name = name[..parenIdx];

        // Strip the member name segment — keep only the containing type
        // T: = type itself, no stripping needed; M:/P:/E:/F: = member, strip last segment
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && fqn.Length > 1 && fqn[1] == ':' && fqn[0] != 'T')
            name = name[..lastDot];

        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static IReadOnlyList<SymbolCard> BuildStubs(INamedTypeSymbol typeSymbol)
    {
        var stubs = new List<SymbolCard>();

        // Include the type itself
        var typeCard = ToStubCard(typeSymbol);
        if (typeCard is not null) stubs.Add(typeCard);

        // Include all direct public/protected members
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member.DeclaredAccessibility
                    is not Accessibility.Public
                    and not Accessibility.Protected
                    and not Accessibility.ProtectedOrInternal)
                continue;

            var memberCard = ToStubCard(member);
            if (memberCard is not null) stubs.Add(memberCard);
        }

        return stubs;
    }

    private static IReadOnlyList<ExtractedTypeRelation> BuildTypeRelations(INamedTypeSymbol typeSymbol)
    {
        var relations = new List<ExtractedTypeRelation>();
        var typeFqn = typeSymbol.GetDocumentationCommentId() ?? typeSymbol.ToDisplayString();
        var typeId = SymbolId.From(typeFqn);

        // Base type (exclude System.Object — adds noise)
        if (typeSymbol.BaseType is { } baseType
            && baseType.SpecialType != SpecialType.System_Object)
        {
            var baseFqn = baseType.GetDocumentationCommentId() ?? baseType.ToDisplayString();
            relations.Add(new ExtractedTypeRelation(
                TypeSymbolId: typeId,
                RelatedSymbolId: SymbolId.From(baseFqn),
                RelationKind: TypeRelationKind.BaseType,
                DisplayName: baseType.Name));
        }

        // Directly implemented interfaces
        foreach (var iface in typeSymbol.Interfaces)
        {
            var ifaceFqn = iface.GetDocumentationCommentId() ?? iface.ToDisplayString();
            relations.Add(new ExtractedTypeRelation(
                TypeSymbolId: typeId,
                RelatedSymbolId: SymbolId.From(ifaceFqn),
                RelationKind: TypeRelationKind.Interface,
                DisplayName: iface.Name));
        }

        return relations;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="fromSymbolId"/> is a member that directly
    /// belongs to the type identified by <paramref name="prefix"/> /
    /// <paramref name="ctorPrefix"/>, and <c>false</c> for members of nested types.
    /// </summary>
    /// <remarks>
    /// After stripping the type prefix the remaining segment is the member name and
    /// optional parameter list.  A dot appearing <em>before</em> the first <c>(</c>
    /// indicates another type nesting level; a dot that appears only inside a parameter
    /// list (e.g. <c>System.Collections.Generic.List{System.Int32}</c>) is benign.
    /// </remarks>
    internal static bool BelongsToType(string fromSymbolId, string prefix, string ctorPrefix)
    {
        if (!fromSymbolId.StartsWith(prefix, StringComparison.Ordinal)
            && !fromSymbolId.StartsWith(ctorPrefix, StringComparison.Ordinal))
            return false;

        var memberSegment = fromSymbolId.StartsWith(ctorPrefix, StringComparison.Ordinal)
            ? fromSymbolId[ctorPrefix.Length..]
            : fromSymbolId[prefix.Length..];

        var dotIdx   = memberSegment.IndexOf('.');
        var parenIdx = memberSegment.IndexOf('(');
        if (dotIdx < 0) return true;                          // no dot — direct member
        if (parenIdx >= 0 && dotIdx > parenIdx) return true;  // dot is inside param list only
        return false;                                          // dot before '(' → nested type
    }

    private static SymbolCard? ToStubCard(ISymbol symbol)
    {
        var fqn = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(fqn)) return null;

        var kind = SymbolKindMapper.Map(symbol);
        var symbolId = SymbolId.From(fqn);
        var signature = SignatureFormatter.Format(symbol);
        var documentation = DocumentationReader.GetSummary(symbol);
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var containingType = symbol.ContainingType?.ToDisplayString();
        var visibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
        var assemblyName = symbol.ContainingAssembly?.Name ?? "unknown";
        var typeName = symbol.ContainingType?.Name ?? symbol.Name;

        // Virtual file path: decompiled/{AssemblyName}/{Namespace/TypeName}.cs
        var nsPath = string.IsNullOrEmpty(ns) ? "" : ns.Replace('.', '/') + "/";
        var filePath = FilePath.From($"decompiled/{assemblyName}/{nsPath}{typeName}.cs");

        return SymbolCard.CreateMinimal(
            symbolId: symbolId,
            fullyQualifiedName: fqn,
            kind: kind,
            signature: signature,
            @namespace: ns,
            filePath: filePath,
            spanStart: 0,
            spanEnd: 0,
            visibility: visibility,
            confidence: Confidence.High,
            documentation: documentation,
            containingType: containingType);
    }
}
