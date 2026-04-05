namespace CodeMap.Storage.Engine;

/// <summary>Error codes for storage engine exceptions.</summary>
public enum StorageErrorCode
{
    InvalidFormat,
    DataCorruption,
    VersionMismatch,
    WriteFailure,
}

/// <summary>Base class for all storage engine exceptions.</summary>
public abstract class StorageEngineException(StorageErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The structured error code identifying the failure category.</summary>
    public StorageErrorCode Code { get; } = code;
}

/// <summary>Thrown when a segment file fails magic number or header validation.</summary>
public sealed class StorageFormatException(string message, Exception? inner = null)
    : StorageEngineException(StorageErrorCode.InvalidFormat, message, inner);

/// <summary>Thrown when a CRC32 page checksum validation fails.</summary>
public sealed class StorageCorruptionException(string message, Exception? inner = null)
    : StorageEngineException(StorageErrorCode.DataCorruption, message, inner);

/// <summary>Thrown when the on-disk format major version does not match the reader.</summary>
public sealed class StorageVersionException(int actual, int expected)
    : StorageEngineException(StorageErrorCode.VersionMismatch,
        $"Format major version {actual} cannot be read by engine expecting {expected}");

/// <summary>Thrown when a WAL or overlay write operation fails with an I/O error.</summary>
public sealed class StorageWriteException(string message, Exception? inner = null)
    : StorageEngineException(StorageErrorCode.WriteFailure, message, inner);
