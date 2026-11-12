using System;
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
    internal bool TryUnescape(ShellValue value, EvaluationMemory memory, CancellationToken cancellationToken,
        out ShellValue unescaped, out GlobMask unescapedMask)
    {
        unescaped = value;
        unescapedMask = this;
        var bytes = value.Bytes.Span;
        var activityMask = AsSpan();
        var firstEscape = -1;
        for (var index = 0; index < bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[index] == (byte)'\\' && activityMask[index] == Active)
            {
                firstEscape = index;
                break;
            }
        }
        if (firstEscape < 0)
            return true;

        memory.Charge(checked(4L * (2L * bytes.Length)));
        var content = new byte[bytes.Length];
        var activity = new byte[bytes.Length];
        bytes[..firstEscape].CopyTo(content);
        for (var index = 0; index < firstEscape; index++)
            activity[index] = activityMask[index];
        var count = firstEscape;
        var escapedQuote = false;
        for (var index = firstEscape; index < bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = escapedQuote ? Active : activityMask[index];
            escapedQuote = false;
            if (active == Active && bytes[index] == (byte)'\\')
            {
                if (index + 1 == bytes.Length)
                    return false;
                if (activityMask[index + 1] != Active || bytes[index + 1] is 1 or 127)
                {
                    // Bash exposes its quoted-byte marker when an unquoted backslash consumes it.
                    content[count++] = 1;
                    escapedQuote = true;
                    continue;
                }
                index++;
                active = 0;
            }
            content[count] = bytes[index];
            activity[count++] = active;
        }
        unescaped = ShellValue.FromBytes(content.AsMemory(0, count));
        unescapedMask = new GlobMask(activity.AsMemory(0, count));
        return true;
    }
}

/// <summary>
/// Stack-only view over glob activity metadata; the default view makes every position active.
/// </summary>
public readonly ref struct GlobMaskSpan(ReadOnlySpan<byte> mask)
{
    private readonly ReadOnlySpan<byte> _mask = mask;

    public byte this[int index] => _mask.IsEmpty ? GlobMask.Active : _mask[index];
}
