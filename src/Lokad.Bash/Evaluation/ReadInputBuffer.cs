using System;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>Normalizes read input while preserving escapes for field assignment.</summary>
internal struct ReadInputBuffer(ByteMemoryBuilder content, ReadBackslashMode backslashMode)
{
    private bool _escaped;
    private bool _assignedContentEnded;

    /// <summary>Indicates whether the normalized record still needs escape interpretation.</summary>
    public ReadBackslashMode FieldBackslashMode { get; private set; } = ReadBackslashMode.Preserve;

    /// <summary>Reports an escaped LF, or borrows the unchanged line when it supplies the entire record.</summary>
    public bool AppendLine(ReadOnlyMemory<byte> bytes, bool hasTerminator, out ReadOnlyMemory<byte>? borrowed,
        CancellationToken cancellationToken)
    {
        borrowed = null;
        if (content.Length == 0 && !_escaped)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var special = FindSpecialByte(bytes.Span, 0);
            if (special < 0)
            {
                borrowed = bytes;
                return false;
            }
            content.Append(bytes.Span[..special]);
            bytes = bytes[special..];
        }

        var remaining = int.MaxValue;
        Append(bytes.Span, delimiter: null, ref remaining, out _, cancellationToken);
        if (!hasTerminator || !_escaped)
            return false;
        _escaped = false;
        return true;
    }

    /// <summary>Appends a window up to a delimiter or count, returning the consumed prefix length.</summary>
    public bool Append(ReadOnlySpan<byte> bytes, byte? delimiter, ref int remaining, out int consumed,
        CancellationToken cancellationToken)
    {
        var stop = delimiter.GetValueOrDefault();
        consumed = 0;
        while (consumed < bytes.Length && remaining != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_escaped)
            {
                var value = bytes[consumed++];
                AppendEscapedByte(value);
                if (value != (byte)'\n')
                    remaining--;
                continue;
            }

            var run = bytes.Slice(consumed, Math.Min(bytes.Length - consumed, remaining));
            var special = FindSpecialByte(run, stop);
            var length = special < 0 ? run.Length : special;
            if (!_assignedContentEnded)
                content.Append(run[..length]);
            consumed += length;
            remaining -= length;
            if (special < 0)
                break;

            var next = bytes[consumed++];
            if (backslashMode == ReadBackslashMode.InterpretEscapes && next == (byte)'\\')
                _escaped = true;
            else if (delimiter == next)
                return true;
        }
        return remaining == 0;
    }

    private int FindSpecialByte(ReadOnlySpan<byte> run, byte stop) => backslashMode == ReadBackslashMode.Preserve
        ? run.IndexOfAny((byte)0, stop) : run.IndexOfAny((byte)0, (byte)'\\', stop);

    private void AppendEscapedByte(byte value)
    {
        _escaped = false;
        if (value == (byte)'\n' || _assignedContentEnded)
            return;
        content.Append((byte)'\\').Append(value);
        FieldBackslashMode = ReadBackslashMode.InterpretEscapes;
        // Quoted NUL participates in splitting as a protected empty field.
        _assignedContentEnded = value == 0;
    }
}
