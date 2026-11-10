using System;

namespace Lokad.Bash.Files;

/// <summary>Represents a bounded raw-byte read from a descriptor.</summary>
/// <remarks>
/// A successful read supplies at least one byte. A final successful read may report end-of-file together
/// with its bytes. The default value represents normal end-of-file.
/// </remarks>
public readonly record struct ByteReadResult
{
    private readonly ByteReadKind _kind;
    private readonly bool _reachedEoF;

    private ByteReadResult(
        ByteReadKind kind,
        int bytesRead,
        OperationError? error,
        bool reachedEoF)
    {
        _kind = kind;
        Error = error;
        _reachedEoF = reachedEoF;
        BytesRead = bytesRead;
    }

    /// <summary>Gets the number of bytes offered by the read, independently of consumption.</summary>
    public int BytesRead { get; }

    /// <summary>
    /// Gets whether the descriptor reached end-of-file while producing this result.
    /// A failure is terminal even when this property is <see langword="false"/>.
    /// </summary>
    public bool ReachedEoF => _kind == ByteReadKind.EndOfFile || _reachedEoF;

    /// <summary>Gets whether the descriptor reported a read failure.</summary>
    public bool IsError => _kind == ByteReadKind.Failure;

    /// <summary>The diagnostic on failure; null for success or normal end-of-file.</summary>
    public OperationError? Error { get; }

    /// <summary>Gets a normal end-of-file result.</summary>
    public static ByteReadResult EndOfFile => default;

    /// <summary>A closed, missing or unreadable descriptor, without bytes.</summary>
    public static ByteReadResult ReadFailure =>
        Failure(0, OperationError.BadFileDescriptor, reachedEoF: true);

    /// <summary>Creates a successful read result.</summary>
    public static ByteReadResult Success(int bytesRead, bool reachedEoF)
    {
        if (bytesRead <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRead), "A successful read must supply bytes.");

        return new ByteReadResult(ByteReadKind.Success, bytesRead, error: null, reachedEoF);
    }

    /// <summary>Creates a failed read result, optionally preserving bytes read before the failure.</summary>
    public static ByteReadResult Failure(int bytesRead, OperationError error, bool reachedEoF)
    {
        if (bytesRead < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesRead), "The byte count cannot be negative.");
        return new ByteReadResult(ByteReadKind.Failure, bytesRead, error, reachedEoF);
    }

    /// <summary>Exposes a descriptor failure as an I/O exception, retaining its operation error.</summary>
    public void ThrowIfError()
    {
        if (Error is { } error)
            throw new ShellDescriptorReadException(error);
    }

    private enum ByteReadKind : byte
    {
        EndOfFile,
        Success,
        Failure
    }
}
