using System;
using System.Diagnostics;
using System.Threading;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Retains per-character glob activity metadata; empty storage means every position is active.
/// </summary>
public sealed class GlobMask(ReadOnlyMemory<byte> mask)
{
    /// <summary> Byte value meaning glob metacharacters are active at this position. </summary>
    public const byte Active = 1;

    public ReadOnlyMemory<byte> Memory => mask;

    public GlobMaskSpan AsSpan() => new(mask.Span);

    /// <summary>Removes active backslash escapes while retaining which resulting bytes are literal.</summary>
    /// <returns>False for a terminal active backslash, which stays in the returned pattern.</returns>
    internal bool TryUnescape(ShellValue value, EvaluationMemory memory, CancellationToken cancellationToken,
        out ShellValue unescaped, out GlobMask unescapedMask)
    {
        unescaped = value;
        unescapedMask = this;
        var bytes = value.Bytes.Span;
        var activityMask = AsSpan();
        var firstEscape = activityMask.IndexOfActiveByte(bytes, "\\"u8, cancellationToken);
        if (firstEscape < 0)
            return true;
        if (firstEscape == bytes.Length - 1)
            return false;

        memory.Charge(checked(4L * (2L * bytes.Length)));
        var content = new byte[bytes.Length];
        var activity = new byte[bytes.Length];
        bytes[..firstEscape].CopyTo(content);
        if (mask.IsEmpty)
            activity.AsSpan(0, firstEscape).Fill(Active);
        else
            mask.Span[..firstEscape].CopyTo(activity);
        var count = firstEscape;
        var escapedQuote = false;
        var terminalEscape = false;
        for (var index = firstEscape; index < bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = escapedQuote ? Active : activityMask[index];
            escapedQuote = false;
            if (active == Active && bytes[index] == (byte)'\\')
            {
                if (index + 1 == bytes.Length)
                    terminalEscape = true;
                else if (activityMask[index + 1] != Active || bytes[index + 1] is 1 or 127)
                {
                    // Bash exposes its quoted-byte marker when an unquoted backslash consumes it.
                    content[count++] = 1;
                    escapedQuote = true;
                    continue;
                }
                else
                {
                    index++;
                    active = 0;
                }
            }
            content[count] = bytes[index];
            activity[count++] = active;
        }
        unescaped = ShellValue.FromBytes(content.AsMemory(0, count));
        unescapedMask = new GlobMask(activity.AsMemory(0, count));
        return !terminalEscape;
    }
}

/// <summary>
/// Stack-only view over glob activity metadata; the default view makes every position active.
/// </summary>
public readonly ref struct GlobMaskSpan(ReadOnlySpan<byte> mask)
{
    private readonly ReadOnlySpan<byte> _mask = mask;

    public byte this[int index] => _mask.IsEmpty ? GlobMask.Active : _mask[index];

    internal int IndexOfActiveByte(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> candidates,
        CancellationToken cancellationToken)
    {
        const int ScanBytes = 4096;
        Debug.Assert(candidates.Length is >= 1 and <= 3);
        var first = candidates[0];
        var second = candidates.Length > 1 ? candidates[1] : first;
        var third = candidates.Length > 2 ? candidates[2] : second;
        for (var offset = 0; offset < bytes.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(ScanBytes, bytes.Length - offset);
            var hit = bytes.Slice(offset, length).IndexOfAny(first, second, third);
            var end = offset + length;
            if (hit >= 0)
            {
                var index = offset + hit;
                if (this[index] == GlobMask.Active)
                    return index;

                // A dense quoted chunk uses one scan after its first hit.
                for (index++; index < end; index++)
                {
                    var value = bytes[index];
                    if ((value == first || value == second || value == third) && _mask[index] == GlobMask.Active)
                        return index;
                }
            }
            offset = end;
        }
        return -1;
    }
}
