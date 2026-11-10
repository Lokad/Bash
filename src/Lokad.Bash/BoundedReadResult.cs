using System;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>Complete borrowed-descriptor input, a read failure, or an exceeded byte limit.</summary>
public abstract record BoundedReadResult
{
    private BoundedReadResult() { }

    /// <summary>Returns complete input or throws for the original read failure or byte limit.</summary>
    internal ReadOnlyMemory<byte> GetContentOrThrow()
    {
        switch (this)
        {
            case Complete complete:
                return complete.Content;
            case Failed failed:
                failed.Failure.ThrowIfError();
                break;
            case TooLarge:
                throw new ByteMemoryBuilder.LimitExceededException();
        }
        throw new InvalidOperationException("Unexpected whole-input read outcome.");
    }

    /// <summary>Bytes published only after successful EOF; their storage must not be mutated.</summary>
    public sealed record Complete(ReadOnlyMemory<byte> Content) : BoundedReadResult;

    /// <summary>The original descriptor failure; partial input is not published.</summary>
    public sealed record Failed(ByteReadResult Failure) : BoundedReadResult;

    /// <summary>Input exceeded the caller's limit; partial input is not published.</summary>
    public sealed record TooLarge : BoundedReadResult;
}
