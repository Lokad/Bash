using System;
using System.Runtime.CompilerServices;

namespace Lokad.Bash.Files;

/// <summary>An opaque filesystem object identity, independent of its paths and content versions.</summary>
/// <remarks>
/// Reuse the same scope object for one filesystem. Keys must have immutable equality and hash codes;
/// equal keys within that scope denote the same live object. Edits retain identity, while copies
/// have distinct identities. Identities are lookup facts, not durable content snapshots.
/// </remarks>
public sealed record FileIdentity
{
    private readonly object _fileSystem;
    private readonly object _key;

    public FileIdentity(object fileSystem, object key)
    {
        _fileSystem = fileSystem;
        _key = key;
    }

    public bool Equals(FileIdentity? other) => other is not null
        && ReferenceEquals(_fileSystem, other._fileSystem) && _key.Equals(other._key);

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(_fileSystem), _key.GetHashCode());
}
