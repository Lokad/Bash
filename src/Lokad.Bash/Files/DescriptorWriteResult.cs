namespace Lokad.Bash.Files;

/// <summary>Distinguishes an open write, downstream closure, and failure with an operation error.</summary>
public readonly record struct DescriptorWriteResult
{
    private readonly DescriptorWriteKind _kind;

    private DescriptorWriteResult(DescriptorWriteKind kind, OperationError? error)
    {
        _kind = kind;
        Error = error;
    }

    /// <summary>Gets whether the descriptor can accept more content.</summary>
    public bool CanAcceptMore => _kind == DescriptorWriteKind.Open;

    /// <summary>Gets whether the write failed, rather than ending because the downstream reader closed.</summary>
    public bool IsError => _kind == DescriptorWriteKind.Failure;

    /// <summary>The diagnostic on failure; null for an open write or downstream closure.</summary>
    public OperationError? Error { get; }

    /// <summary>Gets a successful write result for a descriptor that remains open.</summary>
    public static DescriptorWriteResult Open => new(DescriptorWriteKind.Open, null);

    /// <summary>Gets a successful write result for a descriptor whose downstream reader closed.</summary>
    public static DescriptorWriteResult Closed => default;

    /// <summary>Gets a failure for a missing, closed, or non-writable descriptor.</summary>
    public static DescriptorWriteResult WriteFailure => Failure(OperationError.BadFileDescriptor);

    /// <summary>Creates a failed write result.</summary>
    public static DescriptorWriteResult Failure(OperationError error) => new(DescriptorWriteKind.Failure, error);

    private enum DescriptorWriteKind : byte
    {
        Closed,
        Open,
        Failure
    }
}
