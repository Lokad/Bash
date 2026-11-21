using System;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>Normalizes read input while preserving escapes for field assignment.</summary>
internal struct ReadInputBuffer(ByteMemoryBuilder content, ReadBackslashMode backslashMode)
{
    private bool _escaped;
    private bool _assignedContentEnded;

    /// <summary>Appends a framed line and reports whether its LF was escaped.</summary>
    public bool AppendLine(ReadOnlySpan<byte> bytes, bool hasTerminator, CancellationToken cancellationToken)
    {
        var remaining = int.MaxValue;
        Append(bytes, delimiter: null, ref remaining, out _, cancellationToken);
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
            var special = backslashMode == ReadBackslashMode.Preserve
                ? run.IndexOfAny((byte)0, stop) : run.IndexOfAny((byte)0, (byte)'\\', stop);
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

    private void AppendEscapedByte(byte value)
    {
        _escaped = false;
        if (value == (byte)'\n' || _assignedContentEnded)
            return;
        content.Append((byte)'\\').Append(value);
        // Quoted NUL participates in splitting as a protected empty field.
        _assignedContentEnded = value == 0;
    }
}
