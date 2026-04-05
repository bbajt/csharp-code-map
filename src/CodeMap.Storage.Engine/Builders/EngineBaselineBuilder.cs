namespace CodeMap.Storage.Engine;

using System.Diagnostics;
using System.Text;
using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Storage.Telemetry;

/// <summary>
/// Builds an immutable baseline snapshot from Roslyn extraction output.
/// Implements the 11-phase pipeline per STORAGE-API.MD §10.
/// Single use — construct a new instance for each baseline build.
/// </summary>
internal sealed class EngineBaselineBuilder : IEngineBaselineBuilder
{
    private readonly string _storeBaseDir;

    public EngineBaselineBuilder(string storeBaseDir)
    {
        _storeBaseDir = storeBaseDir;
    }

    public Task<BaselineBuildResult> BuildAsync(BaselineBuildInput input, CancellationToken ct)
        => Task.Run(() => BuildCore(input, ct), ct);

    private BaselineBuildResult BuildCore(BaselineBuildInput input, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tempDir = Path.Combine(_storeBaseDir, "temp", $"build-{Guid.NewGuid():N}");
        var finalDir = Path.Combine(_storeBaseDir, "baselines", input.CommitSha);

        try
        {
            Directory.CreateDirectory(tempDir);

            // ── Phase 1: Dictionary build ────────────────────────────────────
            using var dictBuilder = new DictionaryBuilder();

            // Build file lookup: filePath → fileIntId (1-based)
            var fileIdByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < input.Files.Count; i++)
                fileIdByPath[input.Files[i].Path.Value] = i + 1;

            // Build project lookup: projectName → projectIntId (1-based)
            var projectNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in input.Files)
            {
                if (!string.IsNullOrEmpty(f.ProjectName))
                    projectNames.Add(f.ProjectName);
            }
            var projectList = projectNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            var projectIdByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < projectList.Count; i++)
                projectIdByName[projectList[i]] = i + 1;

            // Build symbol FQN → intId lookup (filter: only symbols whose file is in compilation)
            var symbolIntIdByFqn = new Dictionary<string, int>(StringComparer.Ordinal);
            var validSymbols = new List<SymbolCard>();
            foreach (var sym in input.Symbols)
            {
                if (fileIdByPath.ContainsKey(sym.FilePath.Value))
                    validSymbols.Add(sym);
            }

            for (var i = 0; i < validSymbols.Count; i++)
                symbolIntIdByFqn[validSymbols[i].SymbolId.Value] = i + 1;

            // Pre-build file→project lookup (avoids O(S*F) GetProjectForFile calls)
            var projectByFile = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in input.Files)
                projectByFile.TryAdd(f.Path.Value, f.ProjectName);

            // Pre-build container type reverse lookup: display name suffix → IntId
            // (avoids O(N²) EndsWith scan per symbol)
            var containerByDisplayName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < validSymbols.Count; i++)
            {
                var displayName = ExtractDisplayName(validSymbols[i].FullyQualifiedName);
                containerByDisplayName.TryAdd(displayName, i + 1);
            }

            // ── Phase 2: Map symbols to SymbolRecords ────────────────────────
            var symbolRecords = new SymbolRecord[validSymbols.Count];
            var searchData = new List<(int SymbolIntId, string Fqn, string DisplayName, string? Namespace, string? Signature, string? Documentation)>(validSymbols.Count);

            for (var i = 0; i < validSymbols.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var sym = validSymbols[i];
                var intId = i + 1;
                var fqn = sym.SymbolId.Value;

                // Intern strings
                var projectName = projectByFile.GetValueOrDefault(sym.FilePath.Value);
                var stableId = RecordMappers.ComputeDegradedStableId(sym.Kind, fqn, projectName);
                var stableIdSid = dictBuilder.Intern(stableId);
                var fqnSid = dictBuilder.Intern(fqn);
                var displayName = ExtractDisplayName(sym.FullyQualifiedName);
                var displayNameSid = dictBuilder.Intern(displayName);
                var nsSid = dictBuilder.Intern(sym.Namespace ?? "");
                var nameTokens = string.Join(' ', SearchIndexBuilder.Tokenize(fqn, displayName, sym.Namespace, sym.Signature, sym.Documentation));
                var nameTokensSid = dictBuilder.Intern(nameTokens);

                var fileIntId = fileIdByPath.GetValueOrDefault(sym.FilePath.Value, 0);
                var projectIntId = projectName != null ? projectIdByName.GetValueOrDefault(projectName, 0) : 0;

                // Container IntId: O(1) lookup by display name
                var containerIntId = 0;
                if (!string.IsNullOrEmpty(sym.ContainingType))
                    containerByDisplayName.TryGetValue(sym.ContainingType, out containerIntId);

                symbolRecords[i] = new SymbolRecord(
                    symbolIntId: intId,
                    stableIdStringId: stableIdSid,
                    fqnStringId: fqnSid,
                    displayNameStringId: displayNameSid,
                    namespaceStringId: nsSid,
                    containerIntId: containerIntId,
                    fileIntId: fileIntId,
                    projectIntId: projectIntId,
                    kind: RecordMappers.MapSymbolKind(sym.Kind),
                    accessibility: RecordMappers.MapAccessibility(sym.Visibility),
                    flags: RecordMappers.BuildSymbolFlags(sym),
                    spanStart: sym.SpanStart,
                    spanEnd: sym.SpanEnd,
                    nameTokensStringId: nameTokensSid,
                    signatureHash: 0 // Phase 3: not computed
                );

                searchData.Add((intId, fqn, displayName, sym.Namespace, sym.Signature, sym.Documentation));
            }

            // ── Phase 3: Map files to FileRecords + collect content ──────────
            var fileRecords = new FileRecord[input.Files.Count];
            var contentBodies = new List<byte[]>(input.Files.Count);

            for (var i = 0; i < input.Files.Count; i++)
            {
                var file = input.Files[i];
                var fileIntId = i + 1;
                var pathSid = dictBuilder.Intern(file.Path.Value);
                var normalizedSid = dictBuilder.Intern(file.Path.Value.ToLowerInvariant());
                var projectIntId = file.ProjectName != null ? projectIdByName.GetValueOrDefault(file.ProjectName, 0) : 0;
                var (hashHigh, hashLow) = RecordMappers.SplitSha256(file.Sha256Hash);
                var language = RecordMappers.DetectLanguage(file.Path.Value);

                // Content
                var contentId = 0;
                if (file.Content != null)
                {
                    contentBodies.Add(Encoding.UTF8.GetBytes(file.Content));
                    contentId = contentBodies.Count; // 1-based
                }

                fileRecords[i] = new FileRecord(
                    fileIntId: fileIntId,
                    pathStringId: pathSid,
                    normalizedStringId: normalizedSid,
                    projectIntId: projectIntId,
                    contentHashHigh: hashHigh,
                    contentHashLow: hashLow,
                    language: language,
                    flags: 0,
                    contentId: contentId
                );
            }

            // ── Phase 4: Map projects to ProjectRecords ──────────────────────
            var projectRecords = new ProjectRecord[projectList.Count];
            for (var i = 0; i < projectList.Count; i++)
            {
                var name = projectList[i];
                var intId = i + 1;
                var nameSid = dictBuilder.Intern(name);
                var asmSid = dictBuilder.Intern(name); // assembly name = project name
                var fwSid = dictBuilder.Intern("net9.0"); // default; not available in extraction
                var outSid = dictBuilder.Intern("Library");
                var isTest = name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                          || name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
                          || name.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);

                projectRecords[i] = new ProjectRecord(
                    projectIntId: intId,
                    nameStringId: nameSid,
                    assemblyNameStringId: asmSid,
                    targetFrameworkStringId: fwSid,
                    outputTypeStringId: outSid,
                    flags: isTest ? 1 : 0
                );
            }

            // ── Phase 5: Map references to EdgeRecords ───────────────────────
            var validEdges = new List<EdgeRecord>();
            var edgeId = 0;

            foreach (var r in input.References)
            {
                // Skip refs whose file or from-symbol isn't in the compilation
                if (!fileIdByPath.TryGetValue(r.FilePath.Value, out var refFileIntId))
                    continue;
                if (!symbolIntIdByFqn.TryGetValue(r.FromSymbol.Value, out var fromIntId))
                    continue;

                edgeId++;
                var toIntId = symbolIntIdByFqn.GetValueOrDefault(r.ToSymbol.Value, 0);
                var toNameSid = 0;
                if (toIntId == 0 && !string.IsNullOrEmpty(r.ToName))
                    toNameSid = dictBuilder.Intern(r.ToName);

                var edgeFlags = r.IsDecompiled ? 1 : 0;

                validEdges.Add(new EdgeRecord(
                    edgeIntId: edgeId,
                    fromSymbolIntId: fromIntId,
                    toSymbolIntId: toIntId,
                    toNameStringId: toNameSid,
                    fileIntId: refFileIntId,
                    spanStart: r.LineStart,
                    spanEnd: r.LineEnd,
                    edgeKind: RecordMappers.MapEdgeKind(r.Kind),
                    resolutionState: RecordMappers.MapResolutionState(r.ResolutionState),
                    flags: edgeFlags,
                    weight: 1
                ));
            }

            // ── Phase 5b: Convert TypeRelations to EdgeRecords (Inherits=4, Implements=5) ──
            foreach (var tr in input.TypeRelations)
            {
                var fromIntId = symbolIntIdByFqn.GetValueOrDefault(tr.TypeSymbolId.Value, 0);
                var toIntId = symbolIntIdByFqn.GetValueOrDefault(tr.RelatedSymbolId.Value, 0);
                if (fromIntId == 0) continue; // Skip if source type not in symbols table

                edgeId++;
                short edgeKind = tr.RelationKind == TypeRelationKind.BaseType ? (short)4 : (short)5;
                validEdges.Add(new EdgeRecord(
                    edgeIntId: edgeId,
                    fromSymbolIntId: fromIntId,
                    toSymbolIntId: toIntId,
                    toNameStringId: toIntId == 0 ? dictBuilder.Intern(tr.RelatedSymbolId.Value) : 0,
                    fileIntId: 0,
                    spanStart: 0,
                    spanEnd: 0,
                    edgeKind: edgeKind,
                    resolutionState: 0,
                    flags: 0,
                    weight: 1
                ));
            }

            var edgeRecords = validEdges.ToArray();

            // ── Phase 6: Map facts to FactRecords ────────────────────────────
            var validFacts = new List<FactRecord>();
            var factId = 0;

            foreach (var f in input.Facts)
            {
                if (!fileIdByPath.TryGetValue(f.FilePath.Value, out var factFileIntId))
                    continue;
                // Skip facts with no valid symbol ID (matches SQLite InsertFactsAsync behavior)
                if (f.SymbolId == SymbolId.Empty)
                    continue;
                // Facts can reference symbols not in the symbols table (ADR-011)
                var ownerIntId = symbolIntIdByFqn.GetValueOrDefault(f.SymbolId.Value, 0);

                factId++;
                var (primary, secondary) = RecordMappers.SplitFactValue(f.Value);
                var primarySid = dictBuilder.Intern(primary);
                var secondarySid = dictBuilder.Intern(secondary);

                validFacts.Add(new FactRecord(
                    factIntId: factId,
                    ownerSymbolIntId: ownerIntId,
                    fileIntId: factFileIntId,
                    spanStart: f.LineStart,
                    spanEnd: f.LineEnd,
                    factKind: RecordMappers.MapFactKind(f.Kind),
                    primaryStringId: primarySid,
                    secondaryStringId: secondarySid,
                    confidence: RecordMappers.MapConfidence(f.Confidence),
                    flags: 0
                ));
            }

            var factRecords = validFacts.ToArray();

            // ── Phase 7: Write dictionary.seg ────────────────────────────────
            var dictPath = Path.Combine(tempDir, "dictionary.seg");
            // SearchIndexBuilder also interns tokens into the dictionary
            var searchPath = Path.Combine(tempDir, "search.idx");
            SearchIndexBuilder.Build(searchPath, searchData, dictBuilder);

            var nStringIds = dictBuilder.Count;
            using var dictReader = dictBuilder.Build(dictPath);

            // ── Phase 8: Write content.seg ───────────────────────────────────
            var contentPath = Path.Combine(tempDir, "content.seg");
            ContentSegmentWriter.Write(contentPath, contentBodies);

            // ── Phase 9: Write record segments ───────────────────────────────
            var symbolsPath = Path.Combine(tempDir, "symbols.seg");
            var filesPath = Path.Combine(tempDir, "files.seg");
            var projectsPath = Path.Combine(tempDir, "projects.seg");
            var edgesPath = Path.Combine(tempDir, "edges.seg");
            var factsPath = Path.Combine(tempDir, "facts.seg");

            SegmentWriter.Write(symbolsPath, symbolRecords);
            SegmentWriter.Write(filesPath, fileRecords);
            SegmentWriter.Write(projectsPath, projectRecords);
            SegmentWriter.Write(edgesPath, edgeRecords);
            SegmentWriter.Write(factsPath, factRecords);

            // ── Phase 10: Build adjacency indexes ────────────────────────────
            var adjOutPath = Path.Combine(tempDir, "adjacency-out.idx");
            var adjInPath = Path.Combine(tempDir, "adjacency-in.idx");
            AdjacencyIndexBuilder.Build(adjOutPath, adjInPath, edgeRecords, validSymbols.Count);

            // search.idx was already written in Phase 7

            // ── Phase 11: Write checksums.bin ────────────────────────────────
            var checksumsPath = Path.Combine(tempDir, "checksums.bin");
            var segmentFiles = new List<(string Name, string Path)>
            {
                ("dictionary", dictPath),
                ("content", contentPath),
                ("symbols", symbolsPath),
                ("files", filesPath),
                ("projects", projectsPath),
                ("edges", edgesPath),
                ("adj_out", adjOutPath),
                ("adj_in", adjInPath),
                ("facts", factsPath),
                ("search", searchPath),
            };
            var crcMap = ChecksumWriter.WriteChecksums(checksumsPath, segmentFiles);

            // ── Phase 12: Write manifest.json LAST ───────────────────────────
            var segments = new Dictionary<string, SegmentInfo>();
            foreach (var (name, filePath) in segmentFiles)
            {
                var fileName = Path.GetFileName(filePath);
                segments[name] = new SegmentInfo(fileName, crcMap.GetValueOrDefault(name, ""));
            }

            var manifest = new BaselineManifest(
                FormatMajor: StorageConstants.FormatMajor,
                FormatMinor: StorageConstants.FormatMinor,
                CommitSha: input.CommitSha,
                CreatedAt: DateTimeOffset.UtcNow,
                SymbolCount: symbolRecords.Length,
                FileCount: fileRecords.Length,
                ProjectCount: projectRecords.Length,
                EdgeCount: edgeRecords.Length,
                FactCount: factRecords.Length,
                NStringIds: nStringIds,
                Segments: segments,
                RepoRootPath: input.RepoRootPath,
                ProjectDiagnostics: input.ProjectDiagnostics);

            ManifestWriter.Write(Path.Combine(tempDir, "manifest.json"), manifest);

            // ── Phase 13: Atomic publish ─────────────────────────────────────
            // Dispose dict reader before moving directory (Windows file locks)
            dictReader.Dispose();

            if (Directory.Exists(finalDir))
                Directory.Delete(finalDir, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(finalDir)!);
            Directory.Move(tempDir, finalDir);

            sw.Stop();
            StorageTelemetry.BaselineBuilds.Add(1);
            StorageTelemetry.BaselineBuildDuration.Record(sw.Elapsed.TotalMilliseconds);

            return new BaselineBuildResult(
                CommitSha: input.CommitSha,
                BaselinePath: finalDir,
                Elapsed: sw.Elapsed,
                SymbolCount: symbolRecords.Length,
                EdgeCount: edgeRecords.Length,
                FactCount: factRecords.Length,
                FileCount: fileRecords.Length,
                Success: true);
        }
        catch (OperationCanceledException)
        {
            CleanupTemp(tempDir);
            throw;
        }
        catch (Exception ex)
        {
            CleanupTemp(tempDir);
            sw.Stop();
            return new BaselineBuildResult(
                CommitSha: input.CommitSha,
                BaselinePath: "",
                Elapsed: sw.Elapsed,
                SymbolCount: 0,
                EdgeCount: 0,
                FactCount: 0,
                FileCount: 0,
                Success: false,
                ErrorMessage: ex.Message);
        }
    }

    public void Dispose() { }

    private static string ExtractDisplayName(string fqn)
    {
        // Strip doc-ID prefix
        var clean = fqn.Length > 2 && fqn[1] == ':' ? fqn[2..] : fqn;
        // Strip params
        var paren = clean.IndexOf('(');
        if (paren >= 0) clean = clean[..paren];
        // Strip generic arity
        var tick = clean.IndexOf('`');
        if (tick >= 0) clean = clean[..tick];
        // Get last segment
        var dot = clean.LastIndexOf('.');
        return dot >= 0 ? clean[(dot + 1)..] : clean;
    }

    private static void CleanupTemp(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
