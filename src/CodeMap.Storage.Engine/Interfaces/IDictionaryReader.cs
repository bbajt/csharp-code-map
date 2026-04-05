namespace CodeMap.Storage.Engine;

/// <summary>Read-only string resolution over the mmap'd dictionary segment. Thread-safe; immutable after baseline open.</summary>
internal interface IDictionaryReader : IDisposable
{
    int Count { get; }
    string Resolve(int stringId);
    ReadOnlySpan<byte> ResolveUtf8(int stringId);
    bool TryFind(string value, out int stringId);
}
