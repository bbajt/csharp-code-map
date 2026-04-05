namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Builds search.idx per STORAGE-FORMAT.MD §12 and SEARCH-DESIGN.MD §7.
/// Token generation: display name (lowercased), camelCase splits (skip 1-char),
/// namespace segments, full FQN lowercased. Delta-encoded LEB128 postings.
/// </summary>
internal static partial class SearchIndexBuilder
{
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[a-zA-Z])(?=[0-9])|(?<=[0-9])(?=[a-zA-Z])")]
    private static partial Regex CamelCaseSplit();

    private static readonly char[] Separators = ['.', '_', '-', '/', '\\'];

    /// <summary>
    /// Generates the set of search tokens for a symbol.
    /// </summary>
    public static HashSet<string> Tokenize(string fqn, string displayName, string? ns, string? signature = null, string? documentation = null)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        // 1. Display name lowercased
        if (!string.IsNullOrEmpty(displayName))
        {
            tokens.Add(displayName.ToLowerInvariant());

            // 2. CamelCase split — skip 1-char tokens
            foreach (var part in CamelCaseSplit().Split(displayName))
            {
                if (part.Length > 1)
                    tokens.Add(part.ToLowerInvariant());
            }
        }

        // 3. Namespace segments
        if (!string.IsNullOrEmpty(ns))
        {
            foreach (var segment in ns.Split('.'))
            {
                if (segment.Length > 1)
                    tokens.Add(segment.ToLowerInvariant());
            }
        }

        // 4. Full FQN lowercased (strip doc-ID prefix like "M:", "T:", etc.)
        if (!string.IsNullOrEmpty(fqn))
        {
            var cleanFqn = fqn.Length > 2 && fqn[1] == ':' ? fqn[2..] : fqn;
            // Strip params
            var parenIdx = cleanFqn.IndexOf('(');
            if (parenIdx >= 0)
                cleanFqn = cleanFqn[..parenIdx];
            // Strip generic arity
            var backtickIdx = cleanFqn.IndexOf('`');
            if (backtickIdx >= 0)
                cleanFqn = cleanFqn[..backtickIdx];
            tokens.Add(cleanFqn.ToLowerInvariant());
        }

        // 5. Signature tokens — matches SQLite FTS5 which indexes the signature column.
        //    Extracts type names from field/param/return types (e.g. "IOrderService" from
        //    "private readonly IOrderService _orderService").
        if (!string.IsNullOrEmpty(signature))
        {
            foreach (var word in SignatureSplit().Split(signature))
            {
                if (word.Length <= 1) continue;
                var lower = word.ToLowerInvariant();
                tokens.Add(lower);
                // CamelCase split within signature words
                foreach (var part in CamelCaseSplit().Split(word))
                {
                    if (part.Length > 1)
                        tokens.Add(part.ToLowerInvariant());
                }
            }
        }

        // 6. Documentation tokens — matches SQLite FTS5 which indexes the documentation column.
        if (!string.IsNullOrEmpty(documentation))
        {
            foreach (var word in SignatureSplit().Split(documentation))
            {
                if (word.Length <= 1) continue;
                var lower = word.ToLowerInvariant();
                tokens.Add(lower);
                // CamelCase split within doc words (e.g. "IOrderService" → "i", "order", "service")
                foreach (var part in CamelCaseSplit().Split(word))
                {
                    if (part.Length > 1)
                        tokens.Add(part.ToLowerInvariant());
                }
            }
        }

        return tokens;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex SignatureSplit();

    /// <summary>
    /// Builds search.idx from symbol data. Each symbol provides its SymbolIntId, FQN, display name, and namespace.
    /// All token strings are interned via the provided dictionary builder.
    /// </summary>
    public static void Build(
        string path,
        IReadOnlyList<(int SymbolIntId, string Fqn, string DisplayName, string? Namespace, string? Signature, string? Documentation)> symbols,
        IDictionaryBuilder dictionary)
    {
        // Step 1: Build token → symbolIntId postings
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var (symbolIntId, fqn, displayName, ns, signature, documentation) in symbols)
        {
            foreach (var token in Tokenize(fqn, displayName, ns, signature, documentation))
            {
                if (!postings.TryGetValue(token, out var list))
                {
                    list = [];
                    postings[token] = list;
                }
                list.Add(symbolIntId);
            }
        }

        // Step 2: Intern tokens, sort posting lists, build blocks
        var tokenEntries = new List<(int TokenStringId, string Token)>(postings.Count);
        foreach (var token in postings.Keys)
        {
            var tokenStringId = dictionary.Intern(token);
            tokenEntries.Add((tokenStringId, token));
        }
        tokenEntries.Sort((a, b) => a.TokenStringId.CompareTo(b.TokenStringId));

        // Step 3: Encode all postings blocks into a single byte array
        using var postingsStream = new MemoryStream();
        var entries = new TokenEntry[tokenEntries.Count];

        for (var i = 0; i < tokenEntries.Count; i++)
        {
            var (tokenStringId, token) = tokenEntries[i];
            var list = postings[token];
            list.Sort();

            // Deduplicate (same symbol can produce same token from different paths)
            var deduped = Deduplicate(list);

            var blockOffset = (uint)postingsStream.Position;

            // Delta encode + LEB128
            uint prev = 0;
            foreach (var id in deduped)
            {
                var delta = (uint)id - prev;
                Leb128.Write(postingsStream, delta);
                prev = (uint)id;
            }

            entries[i] = new TokenEntry(tokenStringId, blockOffset, (uint)deduped.Count);
        }

        var postingsBytes = postingsStream.ToArray();

        // Step 4: Write file
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        // SegmentFileHeader
        Span<byte> header = stackalloc byte[StorageConstants.SegFileHeaderSize];
        BitConverter.TryWriteBytes(header, StorageConstants.SegmentMagic);
        BitConverter.TryWriteBytes(header[4..], (ushort)StorageConstants.FormatMajor);
        BitConverter.TryWriteBytes(header[6..], (ushort)StorageConstants.FormatMinor);
        BitConverter.TryWriteBytes(header[8..], (uint)entries.Length);
        BitConverter.TryWriteBytes(header[12..], 0u);
        fs.Write(header);

        // TokenHeaderTable
        fs.Write(MemoryMarshal.AsBytes(entries.AsSpan()));

        // PostingsRegion
        fs.Write(postingsBytes);

        fs.Flush(true);
    }

    private static List<int> Deduplicate(List<int> sorted)
    {
        if (sorted.Count <= 1) return sorted;
        var result = new List<int>(sorted.Count) { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] != sorted[i - 1])
                result.Add(sorted[i]);
        }
        return result;
    }
}
