namespace Lokad.Bash;

/// <summary>Controls whether path resolution also captures a stable file version.</summary>
public enum FileSnapshotRequest
{
    /// <summary>Inspect the path without selecting content for a later read.</summary>
    None,
    /// <summary>Return a snapshot or capture failure without reading content or opening a descriptor.</summary>
    Capture
}
