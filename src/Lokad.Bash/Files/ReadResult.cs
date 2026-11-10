using System;
using System.Collections.Generic;

namespace Lokad.Bash.Files;

/// <summary>A byte-valued line batch, normal end-of-file, or descriptor failure.</summary>
/// <remarks>
/// Reuses the raw-byte outcome contract. Successful batches are non-empty and may report EOF
/// with their final bytes. The default value is normal EOF; failures contain no usable data.
/// Retained producers keep content and offsets immutable. Internal reusable readers explicitly
/// document the next-read boundary after which their borrowed content can be overwritten.
/// </remarks>
public readonly record struct ReadResult
{
    private readonly ByteReadResult _read;
    // Only the default EOF value has no offset collection.
    private readonly IReadOnlyList<int>? _lineStartOffsets;

    private ReadResult(ReadOnlyMemory<byte> content, IReadOnlyList<int> lineStartOffsets, ByteReadResult read)
    {
        Content = content;
        _lineStartOffsets = lineStartOffsets;
        _read = read;
    }

    /// <summary>The bytes read, with LF-only line boundaries and no decoding or normalization.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>
    /// Starts of every line after the first, which starts at zero. Offsets must be increasing,
    /// strictly inside Content, and immediately after LF; never include the end of the batch.
    /// </summary>
    public IReadOnlyList<int> LineStartOffsets => _lineStartOffsets ?? Array.Empty<int>();

    /// <summary>Whether EOF is known. Failures are terminal regardless of this flag.</summary>
    public bool ReachedEoF => _read.ReachedEoF;

    /// <summary>Whether the descriptor reported failure, independently of EOF.</summary>
    public bool IsError => _read.IsError;

    /// <summary>The diagnostic on failure; null for success or normal end-of-file.</summary>
    public OperationError? Error => _read.Error;

    /// <summary>Normal EOF without data.</summary>
    public static ReadResult EndOfFile => default;

    /// <summary>A closed, missing or unreadable descriptor.</summary>
    public static ReadResult ReadFailure => Failure(OperationError.BadFileDescriptor);

    /// <summary>Validates a non-empty batch and retains its finished storage without copying.</summary>
    public static ReadResult Success(ReadOnlyMemory<byte> content, IReadOnlyList<int> lineStartOffsets, bool reachedEoF)
    {
        var read = ByteReadResult.Success(content.Length, reachedEoF);
        ValidateLineStarts(content.Span, lineStartOffsets);
        return new ReadResult(content, lineStartOffsets, read);

        static void ValidateLineStarts(ReadOnlySpan<byte> bytes, IReadOnlyList<int> offsets)
        {
            var start = 0;
            var offsetIndex = 0;
            while (start < bytes.Length)
            {
                var newline = bytes[start..].IndexOf((byte)'\n');
                if (newline < 0 || start + newline + 1 == bytes.Length)
                    break;

                start += newline + 1;
                if (offsetIndex >= offsets.Count || offsets[offsetIndex] != start)
                    throw new ArgumentException("Line starts must include every public LF boundary.", nameof(lineStartOffsets));
                offsetIndex++;
            }

            if (offsetIndex != offsets.Count)
                throw new ArgumentException("Line starts must contain only public LF boundaries.", nameof(lineStartOffsets));
        }
    }

    /// <summary>Retains offsets already established by a descriptor's line scan, without rescanning its bytes.</summary>
    /// <remarks>Only line scanners may use this path; other producers must call Success.</remarks>
    public static ReadResult FromValidatedLines(ReadOnlyMemory<byte> content, IReadOnlyList<int> lineStartOffsets, bool reachedEoF) =>
        new(content, lineStartOffsets, ByteReadResult.Success(content.Length, reachedEoF));

    /// <summary>A terminal descriptor failure; bytes from an incomplete failed batch are discarded.</summary>
    public static ReadResult Failure(OperationError error) =>
        new(ReadOnlyMemory<byte>.Empty, Array.Empty<int>(), ByteReadResult.Failure(0, error, reachedEoF: false));

    /// <summary>Exposes a descriptor failure as an I/O exception, retaining its operation error.</summary>
    public void ThrowIfError() => _read.ThrowIfError();
}
