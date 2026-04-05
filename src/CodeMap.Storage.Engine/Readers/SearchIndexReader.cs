namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

/// <summary>
/// Reads search.idx and implements symbol name search + file content text search.
/// Per SEARCH-DESIGN.MD §§1-5.
/// </summary>
internal sealed class SearchIndexReader : IEngineSearchIndex
{
    private readonly EngineBaselineReader _reader;
    private readonly byte[] _searchFileBytes;
    private readonly int _tokenCount;
    private readonly int _headerTableOffset;
    private readonly int _postingsOffset;

    // Sorted token arrays (built on init) — for prefix lookup
    private readonly string[] _sortedTokenStrings;
    private readonly int[] _sortedTokenStringIds;

    // TokenStringId → index into header table
    private readonly Dictionary<int, int> _tokenIdToHeaderIndex;

    private static readonly char[] QuerySeparators = ['.', '_', '-', '/', '\\', ' ', '\t'];

    public SearchIndexReader(EngineBaselineReader reader, string searchIdxPath)
    {
        _reader = reader;
        _searchFileBytes = File.ReadAllBytes(searchIdxPath);

        // Parse header
        _tokenCount = (int)BitConverter.ToUInt32(_searchFileBytes.AsSpan(8));
        _headerTableOffset = StorageConstants.SegFileHeaderSize;
        _postingsOffset = _headerTableOffset + _tokenCount * Marshal.SizeOf<TokenEntry>();

        // Build sorted token string arrays
        var pairs = new (string Token, int StringId, int HeaderIdx)[_tokenCount];
        var entrySize = Marshal.SizeOf<TokenEntry>();

        for (var i = 0; i < _tokenCount; i++)
        {
            var offset = _headerTableOffset + i * entrySize;
            var entry = MemoryMarshal.Read<TokenEntry>(_searchFileBytes.AsSpan(offset));
            var token = reader.Dictionary.Resolve(entry.TokenStringId);
            pairs[i] = (token, entry.TokenStringId, i);
        }

        Array.Sort(pairs, (a, b) => string.Compare(a.Token, b.Token, StringComparison.Ordinal));

        _sortedTokenStrings = new string[_tokenCount];
        _sortedTokenStringIds = new int[_tokenCount];
        _tokenIdToHeaderIndex = new Dictionary<int, int>(_tokenCount);

        for (var i = 0; i < _tokenCount; i++)
        {
            _sortedTokenStrings[i] = pairs[i].Token;
            _sortedTokenStringIds[i] = pairs[i].StringId;
            _tokenIdToHeaderIndex[pairs[i].StringId] = pairs[i].HeaderIdx;
        }
    }

    // ── Symbol Search ────────────────────────────────────────────────────────

    public SymbolSearchResult[] SearchSymbols(string query, SymbolSearchFilter filter)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Step 1: Normalize query into tokens (SEARCH-DESIGN §2.1, C-017)
        var queryTokens = NormalizeQuery(query);
        if (queryTokens.Length == 0) return [];

        // Step 2: Per-token postings lookup with prefix expansion
        HashSet<int>? intersection = null;
        foreach (var qt in queryTokens)
        {
            var postings = GetPostingsForPrefix(qt);
            if (postings.Count == 0) return [];

            if (intersection == null)
                intersection = postings;
            else
                intersection.IntersectWith(postings);

            if (intersection.Count == 0) return [];
        }

        if (intersection == null || intersection.Count == 0) return [];

        // Step 3: Score and filter
        var results = new List<SymbolSearchResult>();
        var queryLower = query.Trim().ToLowerInvariant();

        foreach (var symbolIntId in intersection)
        {
            if (symbolIntId < 1 || symbolIntId > _reader.SymbolCount) continue;

            ref readonly var sym = ref _reader.GetSymbolByIntId(symbolIntId);

            // Apply filters
            if (filter.Kind.HasValue && sym.Kind != filter.Kind.Value) continue;
            if (filter.ExcludeDecompiled && (sym.Flags & (1 << 7)) != 0) continue;
            if (filter.ExcludeTestSymbols && (sym.Flags & (1 << 8)) != 0) continue;

            if (filter.NamespacePrefix != null)
            {
                var ns = sym.NamespaceStringId > 0 ? _reader.ResolveString(sym.NamespaceStringId) : "";
                if (!ns.StartsWith(filter.NamespacePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var score = ComputeScore(sym, queryLower, queryTokens);
            results.Add(new SymbolSearchResult(sym, score));
        }

        // Step 4: Sort by score descending, limit
        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > filter.Limit)
            results.RemoveRange(filter.Limit, results.Count - filter.Limit);

        return results.ToArray();
    }

    private int ComputeScore(in SymbolRecord sym, string queryLower, string[] queryTokens)
    {
        var score = 0;
        var displayName = sym.DisplayNameStringId > 0 ? _reader.ResolveString(sym.DisplayNameStringId) : "";
        var fqn = sym.FqnStringId > 0 ? _reader.ResolveString(sym.FqnStringId) : "";

        var displayLower = displayName.ToLowerInvariant();
        var fqnLower = fqn.ToLowerInvariant();

        // Exact display name match
        if (displayLower == queryLower) score += 100;
        // Exact FQN match
        else if (fqnLower == queryLower || fqnLower.EndsWith("." + queryLower, StringComparison.Ordinal)) score += 80;
        // Prefix match on display name
        else if (displayLower.StartsWith(queryLower, StringComparison.Ordinal)) score += 40;
        // Partial match
        else score += 10;

        // Token coverage bonus
        if (sym.NameTokensStringId > 0)
        {
            var nameTokens = _reader.ResolveString(sym.NameTokensStringId).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nameTokens.Length > 0)
            {
                var matched = 0;
                foreach (var qt in queryTokens)
                {
                    foreach (var nt in nameTokens)
                    {
                        if (nt.StartsWith(qt, StringComparison.Ordinal))
                        {
                            matched++;
                            break;
                        }
                    }
                }
                score += (int)(20.0 * matched / queryTokens.Length);
            }
        }

        // Shorter name bonus
        if (displayName.Length < 30) score += 5;

        return score;
    }

    private static string[] NormalizeQuery(string rawQuery)
    {
        // C-017: Split on dots/separators first, then camelCase split within segments
        // Strip FTS5 wildcard suffix (*) — v2 engine uses prefix matching natively
        var cleaned = rawQuery.Trim().TrimEnd('*').ToLowerInvariant();
        var segments = cleaned.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seg in segments)
        {
            tokens.Add(seg);
            foreach (var part in SearchIndexBuilder.Tokenize("", seg, null))
            {
                if (part.Length >= 1)
                    tokens.Add(part);
            }
        }
        return tokens.ToArray();
    }

    private HashSet<int> GetPostingsForPrefix(string prefix)
    {
        var result = new HashSet<int>();

        // Binary search for first entry >= prefix
        var lo = Array.BinarySearch(_sortedTokenStrings, prefix, StringComparer.Ordinal);
        if (lo < 0) lo = ~lo;

        // C-016: Cap prefix expansion at 50 tokens for 1-char queries
        var maxExpansion = prefix.Length <= 1 ? 50 : int.MaxValue;
        var expanded = 0;

        for (var i = lo; i < _sortedTokenStrings.Length && expanded < maxExpansion; i++)
        {
            if (!_sortedTokenStrings[i].StartsWith(prefix, StringComparison.Ordinal))
                break;

            var tokenStringId = _sortedTokenStringIds[i];
            DecodePostings(tokenStringId, result);
            expanded++;
        }

        return result;
    }

    private void DecodePostings(int tokenStringId, HashSet<int> result)
    {
        if (!_tokenIdToHeaderIndex.TryGetValue(tokenStringId, out var headerIdx))
            return;

        var entrySize = Marshal.SizeOf<TokenEntry>();
        var offset = _headerTableOffset + headerIdx * entrySize;
        var entry = MemoryMarshal.Read<TokenEntry>(_searchFileBytes.AsSpan(offset));

        var blockStart = _postingsOffset + (int)entry.BlockOffset;
        var readOffset = blockStart;
        uint running = 0;

        for (var i = 0; i < (int)entry.PostingCount; i++)
        {
            running += Leb128.Read(_searchFileBytes, ref readOffset);
            result.Add((int)running);
        }
    }

    // ── Text Search ──────────────────────────────────────────────────────────

    public (TextMatch[] Matches, bool IsTruncated) SearchText(string pattern, TextSearchFilter filter)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return ([], false);

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(5));
        }
        catch (RegexParseException)
        {
            return ([], false);
        }

        var matches = new List<TextMatch>();
        var limit = filter.Limit;

        for (var i = 0; i < _reader.FileCount; i++)
        {
            ref readonly var file = ref _reader.GetFileByIntId(i + 1);
            if (file.ContentId == 0) continue;

            var filePath = _reader.ResolveString(file.PathStringId);

            // Apply file glob filter
            if (filter.FileGlob != null && !MatchGlob(filePath, filter.FileGlob))
                continue;

            var content = _reader.ResolveContent(file.ContentId);
            var lines = content.Split('\n');

            for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                var match = regex.Match(line);
                if (!match.Success) continue;

                matches.Add(new TextMatch(filePath, lineIdx + 1, line.TrimEnd('\r'), match.Index, match.Length));

                if (matches.Count >= limit)
                    return (matches.ToArray(), true);
            }
        }

        return (matches.ToArray(), false);
    }

    private static bool MatchGlob(string filePath, string glob)
    {
        // Simple glob: *.cs, src/**/*.vb
        var regexPattern = "^" + Regex.Escape(glob)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(filePath, regexPattern, RegexOptions.IgnoreCase);
    }
}
