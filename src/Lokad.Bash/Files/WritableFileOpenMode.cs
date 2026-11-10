namespace Lokad.Bash.Files;

/// <summary>Access and cursor behavior for a writable file, independent of creation and retention.</summary>
/// <remarks>The default value is invalid; callers must select a writable mode.</remarks>
public enum WritableFileOpenMode
{
    /// <summary>Opens for writing and truncates existing content.</summary>
    WriteTruncate = 1,

    /// <summary>Opens for writing at EOF on every write.</summary>
    WriteAppend,

    /// <summary>Preserves content and starts one shared read/write cursor at zero.</summary>
    ReadWrite
}
