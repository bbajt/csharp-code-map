namespace CodeMap.Storage.Engine;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Computes v2 stable symbol identities per STABLE-IDENTITY.MD.
/// Format: "sym_" + lowercase hex of first 8 bytes of SHA-256(fingerprintInput).
/// </summary>
internal static class StableIdComputer
{
    /// <summary>
    /// Computes a StableId from a pre-assembled fingerprint input string.
    /// Fields must be separated by \x00 (null byte) as specified in STABLE-IDENTITY.MD §4.1.
    /// </summary>
    public static string Compute(string fingerprintInput)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput));
        return "sym_" + Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the fingerprint input string for a named type (class, interface, struct, record, enum, delegate).
    /// </summary>
    public static string BuildNamedTypeFingerprint(
        string name, string containerFqn, string ns, string projectName, bool isStatic, int arity)
        => Join("NamedType", name, containerFqn, ns, projectName, isStatic ? "static" : "instance", arity.ToString());

    /// <summary>
    /// Builds the fingerprint input string for a method (including constructors, operators, local functions).
    /// </summary>
    public static string BuildMethodFingerprint(
        string name, string containerFqn, string ns, string projectName, bool isStatic,
        string returnType, int arity, IEnumerable<string> paramTypes)
    {
        var parts = new List<string>
        {
            "Method", name, containerFqn, ns, projectName,
            isStatic ? "static" : "instance", returnType, arity.ToString()
        };
        parts.AddRange(paramTypes);
        return string.Join('\x00', parts);
    }

    /// <summary>Builds the fingerprint input string for a property (including indexers).</summary>
    public static string BuildPropertyFingerprint(
        string name, string containerFqn, string ns, string projectName, bool isStatic,
        string propertyType, IEnumerable<string> indexerParamTypes)
    {
        var parts = new List<string>
        {
            "Property", name, containerFqn, ns, projectName,
            isStatic ? "static" : "instance", propertyType
        };
        parts.AddRange(indexerParamTypes);
        return string.Join('\x00', parts);
    }

    /// <summary>Builds the fingerprint input string for a field or enum member.</summary>
    public static string BuildFieldFingerprint(
        string name, string containerFqn, string ns, string projectName, bool isStatic, string fieldType)
        => Join("Field", name, containerFqn, ns, projectName, isStatic ? "static" : "instance", fieldType);

    /// <summary>Builds the fingerprint input string for an event.</summary>
    public static string BuildEventFingerprint(
        string name, string containerFqn, string ns, string projectName, bool isStatic, string eventType)
        => Join("Event", name, containerFqn, ns, projectName, isStatic ? "static" : "instance", eventType);

    private static string Join(params string[] parts) => string.Join('\x00', parts);
}
