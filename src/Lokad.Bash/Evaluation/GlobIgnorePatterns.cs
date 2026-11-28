using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>Prepares the active ignore list once for the candidate paths in an expansion.</summary>
internal sealed class GlobIgnorePatterns(ShellValue value, bool ignoreAsciiCase, EvaluationMemory memory) : IDisposable
{
    private static readonly GlobMask AllActive = new(ReadOnlyMemory<byte>.Empty);
    private MemoryCapacity? _owner;
    private List<TextGlobMatcher.Utf8PathMatcher>? _patterns;
    private ByteMemoryBuilder? _longPath;

    internal bool Excludes(string path, CancellationToken cancellationToken)
    {
        if (path.AsSpan(path.LastIndexOf('/') + 1) is "." or "..")
            return true;

        var patterns = _patterns ??= PreparePatterns(cancellationToken);
        if (patterns.Count == 0)
            return false;

        var length = Utf8Text.StrictUtf8.GetByteCount(path);
        Span<byte> bytes = length <= ByteMemoryBuilder.DefaultCapacity
            ? stackalloc byte[length] : LongPathBuffer(length);
        Utf8Text.StrictUtf8.GetBytes(path, bytes);
        foreach (ref var pattern in CollectionsMarshal.AsSpan(patterns))
            if (pattern.Matches(bytes, cancellationToken))
                return true;
        return false;

        Span<byte> LongPathBuffer(int length)
        {
            var buffer = _longPath ??= new ByteMemoryBuilder(0, memory.Budget);
            buffer.Truncate(0);
            return buffer.AppendSpan(length);
        }
    }

    private List<TextGlobMatcher.Utf8PathMatcher> PreparePatterns(CancellationToken cancellationToken)
    {
        var owner = _owner ??= new MemoryCapacity(memory.Budget, 0);
        memory.Charge(owner, 128);
        List<TextGlobMatcher.Utf8PathMatcher> patterns = [];
        var bytes = value.Bytes.Span;
        var start = 0;
        var brackets = 0;
        for (var index = 0; index <= bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index < bytes.Length && (bytes[index] != (byte)':' || brackets != 0))
            {
                switch (bytes[index])
                {
                    case (byte)'\\' when index + 1 < bytes.Length: index++; break;
                    case (byte)'[': brackets++; break;
                    case (byte)']' when brackets != 0: brackets--; break;
                }
                continue;
            }
            if (index > start && AllActive.TryUnescape(ShellValue.FromBytes(value.Bytes[start..index]), memory, owner,
                    cancellationToken, out var pattern, out var mask))
            {
                memory.PrepareListCapacity(owner, patterns, 1);
                patterns.Add(new TextGlobMatcher.Utf8PathMatcher(pattern.Bytes, mask, owner, ignoreAsciiCase));
            }
            start = index + 1;
        }
        return patterns;
    }

    public void Dispose()
    {
        _longPath?.Dispose();
        _owner?.Dispose();
    }
}
