using Lokad.Bash.Files;

namespace Lokad.Bash.Evaluation;

/// <summary>A shell predicate evaluated from path information or terminal status.</summary>
internal readonly record struct FilePredicateKind
{
    public static FilePredicateKind Exists => default;
    public static FilePredicateKind Directory => new(FilePathKind.Directory);
    public static FilePredicateKind File => new(FilePathKind.File);
    public static FilePredicateKind SameFile => new() { ComparesIdentity = true };
    public static FilePredicateKind SymbolicLink => new(FilePathKind.SymbolicLink);
    public static FilePredicateKind ModifiedSinceAccess => new(FileMetadataPredicate.ModifiedSinceAccess);
    public static FilePredicateKind NonEmpty => new() { IsNonEmpty = true };

    public static FilePredicateKind NewerThan => new(FileTimeComparison.NewerThan);
    public static FilePredicateKind OlderThan => new(FileTimeComparison.OlderThan);
    public static FilePredicateKind Writable => new(FileMetadataPredicate.Writable);
    public static FilePredicateKind Readable => new(FileMetadataPredicate.Readable);
    public static FilePredicateKind Executable => new(FileMetadataPredicate.Executable);
    public static FilePredicateKind SetUserId => new(FileMetadataPredicate.SetUserId);
    public static FilePredicateKind SetGroupId => new(FileMetadataPredicate.SetGroupId);
    public static FilePredicateKind Sticky => new(FileMetadataPredicate.Sticky);
    public static FilePredicateKind OwnedByEffectiveUser => new(FileMetadataPredicate.OwnedByEffectiveUser);
    public static FilePredicateKind OwnedByEffectiveGroup => new(FileMetadataPredicate.OwnedByEffectiveGroup);
    public static FilePredicateKind BlockDevice => new(FilePathKind.BlockDevice);
    public static FilePredicateKind CharacterDevice => new(FilePathKind.CharacterDevice);
    public static FilePredicateKind Fifo => new(FilePathKind.Fifo);
    public static FilePredicateKind Socket => new(FilePathKind.Socket);
    public static FilePredicateKind Terminal => new() { IsTerminal = true };

    private FilePredicateKind(FilePathKind requiredKind) => RequiredKind = requiredKind;

    private FilePredicateKind(FileTimeComparison timeComparison) => TimeComparison = timeComparison;

    private FilePredicateKind(FileMetadataPredicate metadataPredicate) => MetadataPredicate = metadataPredicate;

    public FileTimeComparison? TimeComparison { get; }
    public bool IsTerminal { get; private init; }
    public bool IsNonEmpty { get; private init; }
    public bool ComparesIdentity { get; private init; }
    public FileMetadataPredicate? MetadataPredicate { get; }

    /// <summary>For a path-kind predicate, null means any existing path.</summary>
    public FilePathKind? RequiredKind { get; }
}

/// <summary>Compares modification times, including the shell's missing-operand rules.</summary>
internal enum FileTimeComparison
{
    NewerThan,
    OlderThan
}

/// <summary>A shell predicate evaluated from metadata without retrieving file contents.</summary>
internal enum FileMetadataPredicate
{
    ModifiedSinceAccess,
    Writable,
    Readable,
    Executable,
    SetUserId,
    SetGroupId,
    Sticky,
    OwnedByEffectiveUser,
    OwnedByEffectiveGroup
}
