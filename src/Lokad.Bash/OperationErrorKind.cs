namespace Lokad.Bash;

/// <summary>A known failure cause, independent of its diagnostic wording.</summary>
public enum OperationErrorKind
{
    /// <summary>The operation did not establish a more specific cause.</summary>
    Unknown,
    /// <summary>The requested path or a required ancestor is absent.</summary>
    NotFound,
    /// <summary>A path that must be a directory is a file.</summary>
    NotDirectory,
    /// <summary>A path that must be a file is a directory.</summary>
    IsDirectory,
    /// <summary>The directory cannot be deleted while it contains entries.</summary>
    DirectoryNotEmpty,
    /// <summary>Access is denied or the path is protected from the operation.</summary>
    PermissionDenied,
    /// <summary>The destination is occupied and the operation requires a new path.</summary>
    AlreadyExists
}
