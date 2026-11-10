namespace Lokad.Bash;

/// <summary>Controls whether opening a writable destination requires or permits creation.</summary>
public enum FileCreationMode
{
    /// <summary>Opens an existing file or creates a missing file.</summary>
    OpenOrCreate,

    /// <summary>Creates a file only if the destination name is unoccupied.</summary>
    CreateNew,

    /// <summary>Opens an existing file without creating a missing file.</summary>
    OpenExisting
}
