using System;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>The outcome of reading a complete text file through a host.</summary>
public enum TextFileReadOutcome
{
    /// <summary>The rejected open refers to an absent path or a directory.</summary>
    NotFile,
    /// <summary>The entire file was read, including a possibly empty file.</summary>
    Completed,
    /// <summary>The host rejected the open for a reason other than a known non-file path.</summary>
    OpenFailed,
    /// <summary>The descriptor reported a read or close failure; text was discarded.</summary>
    ReadFailed,
    /// <summary>The source bytes reached the whole-input size limit; partial text was discarded.</summary>
    TooLarge
}

/// <summary>Keeps absent/non-file paths distinct from open, descriptor and size failures.</summary>
public readonly record struct TextFileReadResult
{
    private TextFileReadResult(TextFileReadOutcome outcome, string? content, OperationError? error, ReadResult readFailure)
    {
        Outcome = outcome;
        Content = content;
        Error = error;
        ReadFailure = readFailure;
    }

    public TextFileReadOutcome Outcome { get; }

    /// <summary>The complete decoded text on success; null otherwise.</summary>
    public string? Content { get; }

    /// <summary>The open/read/close diagnostic; null for other outcomes.</summary>
    public OperationError? Error { get; }

    /// <summary>The original descriptor failure, or normal EOF when no read failure occurred.</summary>
    public ReadResult ReadFailure { get; }

    /// <summary>A known non-file path. The default value has this meaning.</summary>
    public static TextFileReadResult NotFile => default;

    /// <summary>The complete text could not be materialized within the byte limit.</summary>
    public static TextFileReadResult TooLarge => new(TextFileReadOutcome.TooLarge, null, null, default);

    public static TextFileReadResult Completed(string content) => new(TextFileReadOutcome.Completed, content, null, default);

    public static TextFileReadResult OpenFailed(OperationError error) => new(TextFileReadOutcome.OpenFailed, null, error, default);

    public static TextFileReadResult ReadFailed(ReadResult failure)
    {
        if (!failure.IsError)
            throw new ArgumentException("A descriptor failure is required.", nameof(failure));
        return new TextFileReadResult(TextFileReadOutcome.ReadFailed, null, failure.Error, failure);
    }

    /// <summary>Translates descriptor and size failures for throwing text-read wrappers.</summary>
    internal void ThrowIfReadFailed()
    {
        ReadFailure.ThrowIfError();
        if (Outcome == TextFileReadOutcome.TooLarge)
            throw new ByteMemoryBuilder.LimitExceededException();
    }
}
