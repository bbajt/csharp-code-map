namespace CodeMap.Storage.Engine;

/// <summary>Mutable string dictionary for baseline construction. Not thread-safe. Dispose to release build-time memory.</summary>
internal interface IDictionaryBuilder : IDisposable
{
    int Intern(string value);
    int Intern(ReadOnlySpan<byte> utf8Value);
    int Count { get; }
    IDictionaryReader Build(string targetPath);
}
