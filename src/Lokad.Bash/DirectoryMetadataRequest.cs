namespace Lokad.Bash;

/// <summary>Controls whether a directory listing includes already available child metadata.</summary>
public enum DirectoryMetadataRequest
{
    /// <summary>Return entries without metadata.</summary>
    None,

    /// <summary>Include available metadata without following child links, loading children or reading content.</summary>
    Available
}
