using System.IO;
using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>An expected path lookup failure exposed by a single-path helper.</summary>
public sealed class ShellPathInfoException : IOException
{
    public ShellPathInfoException(FilePathLookup path, OperationError error)
        : base($"{path.Path}: {error.MessageWithoutTerminator}")
    {
        Path = path;
        Error = error;
    }

    public FilePathLookup Path { get; }
    public OperationError Error { get; }
}
