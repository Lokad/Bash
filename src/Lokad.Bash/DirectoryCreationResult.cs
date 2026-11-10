namespace Lokad.Bash;

/// <summary>The outcome observed while creating one directory.</summary>
public abstract record DirectoryCreationResult
{
    private DirectoryCreationResult() { }

    /// <summary>The directory was created.</summary>
    public sealed record Created : DirectoryCreationResult;

    /// <summary>A directory already occupied the path; the operation made no changes.</summary>
    public sealed record AlreadyExistsAsDirectory : DirectoryCreationResult;

    /// <summary>Creation failed without changing the filesystem; a non-directory collision is <see cref="OperationErrorKind.AlreadyExists"/>.</summary>
    public sealed record Failed(OperationError Error) : DirectoryCreationResult;
}
