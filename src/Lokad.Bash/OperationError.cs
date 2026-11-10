using System;
using Lokad.Bash.Tree;

namespace Lokad.Bash;

/// <summary>
/// Carries an LF-terminated filesystem or descriptor diagnostic, ready to write without another terminator.
/// </summary>
public readonly record struct OperationError
{
    public OperationError(string message)
    {
        if (!message.EndsWith('\n'))
            throw new ArgumentException("Operation diagnostics must end with LF.", nameof(message));

        Message = message;
    }

    public string Message { get; }

    /// <summary>The established cause; Unknown when only a diagnostic is available.</summary>
    /// <remarks>Classify from operation facts, never from diagnostic text.</remarks>
    public OperationErrorKind Kind { get; init; }

    /// <summary>Removes only the framing LF for consumers with an unframed text contract.</summary>
    public string MessageWithoutTerminator => Message[..^1];

    /// <summary>Returns LF-terminated detail and the established cause of an expected exception.</summary>
    public static OperationError FromException(Exception exception)
    {
        if (exception is ShellDescriptorReadException read)
            return read.Error;

        var message = exception is FileContentUnavailableException
            ? "selected version unavailable\n" : exception.Message;
        return new OperationError(message.EndsWith('\n') ? message : message + "\n")
        {
            // A missing provider resource need not mean the requested filesystem path is absent.
            Kind = exception is PermissionDeniedException or UnauthorizedAccessException
                ? OperationErrorKind.PermissionDenied : OperationErrorKind.Unknown
        };
    }

    /// <summary>The descriptor is missing, closed, or lacks the requested access.</summary>
    public static OperationError BadFileDescriptor => new("Bad file descriptor\n");
}
