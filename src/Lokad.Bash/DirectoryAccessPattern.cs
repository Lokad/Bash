namespace Lokad.Bash;

/// <summary>Advises how callers expect to use directory metadata; listing contents are unchanged.</summary>
public enum DirectoryAccessPattern
{
    /// <summary>List the requested directories without requesting recursive read-ahead.</summary>
    SingleDirectory,

    /// <summary>The caller expects to traverse descendants; the host may prepare their metadata.</summary>
    RecursiveTraversal
}
