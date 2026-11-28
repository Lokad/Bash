using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Prepares the active ignore list once for the candidate paths in an expansion.</summary>
internal sealed class GlobIgnorePatterns
{
    private static readonly GlobMask AllActive = new(ReadOnlyMemory<byte>.Empty);
    private readonly List<TextGlobMatcher.Utf8PatternMatcher> _patterns = [];

    internal GlobIgnorePatterns(ShellValue value, bool ignoreAsciiCase, EvaluationMemory memory, MemoryCapacity owner,
        CancellationToken cancellationToken)
    {
        memory.Charge(owner, 128);
        var bytes = value.Bytes.Span;
        var start = 0;
        for (var index = 0; index <= bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < bytes.Length && bytes[index] != (byte)':')
            {
                if (bytes[index] == (byte)'\\' && index + 1 < bytes.Length)
                    index++;
                continue;
            }
            if (index > start && AllActive.TryUnescape(ShellValue.FromBytes(value.Bytes[start..index]), memory, owner,
                    cancellationToken, out var pattern, out var mask))
            {
                memory.PrepareListCapacity(owner, _patterns, 1);
                _patterns.Add(new TextGlobMatcher.Utf8PatternMatcher(pattern.Bytes, mask, owner, ignoreAsciiCase));
            }
            start = index + 1;
        }
    }

    internal bool Matches(ShellValue path, CancellationToken cancellationToken)
    {
        foreach (ref var pattern in CollectionsMarshal.AsSpan(_patterns))
            if (pattern.Matches(path.Bytes.Span, cancellationToken))
                return true;
        return false;
    }

    internal static bool IsDotEntry(ReadOnlySpan<char> path) =>
        path[(path.LastIndexOf('/') + 1)..] is "." or "..";
}
