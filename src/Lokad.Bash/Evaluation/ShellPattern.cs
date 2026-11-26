using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>A prepared byte-valued shell-word pattern, independent of pathname component policies.</summary>
internal sealed class ShellPattern
{
    private readonly ShellValue _value;
    private readonly GlobMask? _mask;
    private readonly bool _literal;
    private readonly EvaluationMemory _memory;
    private ReadOnlyMemory<int>? _scalarAtoms;
    private ReadOnlyMemory<int>? _byteAtoms;

    private ShellPattern(ShellValue value, GlobMask? mask, bool literal, EvaluationMemory memory)
    {
        _value = value;
        _mask = mask;
        _literal = literal;
        RequiresByteCharacters = !literal && !System.Text.Unicode.Utf8.IsValid(value.Bytes.Span);
        _memory = memory;
    }

    public bool IsEmpty => _value.IsEmpty;
    public bool RequiresByteCharacters { get; }
    private GlobMaskSpan Mask => _mask == null ? default : _mask.AsSpan();

    /// <summary>Prepares a shell-word pattern, returning null if it cannot match any subject.</summary>
    public static ShellPattern? FromWords(
        IReadOnlyList<ExpandedWord> words, EvaluationMemory memory, CancellationToken cancellationToken)
        => TryPrepareWords(words, memory, cancellationToken, out var value, out var mask)
            ? FromUnescaped(value, mask, memory, cancellationToken) : null;

    /// <summary>Retains an already unescaped value and its quoting metadata for this command's matching.</summary>
    public static ShellPattern FromUnescaped(ShellValue value, GlobMask? mask, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var literal = mask == null || IsLiteral(value.Bytes.Span, mask.AsSpan(), cancellationToken);
        memory.Charge(256);
        return new ShellPattern(value, mask, literal, memory);
    }

    /// <summary>Compares expanded words once without retaining a prepared matcher.</summary>
    public static bool MatchesWords(IReadOnlyList<ExpandedWord> words, ReadOnlySpan<byte> text,
        EvaluationMemory memory, CancellationToken cancellationToken)
    {
        bool? subjectHasMalformedUtf8 = null;
        return MatchesWords(words, text, memory, cancellationToken, ref subjectHasMalformedUtf8);
    }

    /// <summary>Reuses the UTF-8 classification across comparisons with the same immutable subject.</summary>
    public static bool MatchesWords(IReadOnlyList<ExpandedWord> words, ReadOnlySpan<byte> text,
        EvaluationMemory memory, CancellationToken cancellationToken, ref bool? subjectHasMalformedUtf8)
    {
        if (!TryPrepareWords(words, memory, cancellationToken, out var value, out var activity))
            return false;
        var bytes = value.Bytes.Span;
        if (activity == null)
            return text.SequenceEqual(bytes);

        var mask = activity.AsSpan();
        if (IsLiteral(bytes, mask, cancellationToken))
            return text.SequenceEqual(bytes);

        var byteCharacters = !System.Text.Unicode.Utf8.IsValid(bytes);
        if (!byteCharacters)
        {
            subjectHasMalformedUtf8 ??= !System.Text.Unicode.Utf8.IsValid(text);
            byteCharacters = subjectHasMalformedUtf8.Value;
        }
        return TextGlobMatcher.MatchUtf8(bytes, in mask, text,
            requireEnd: true, byteCharacters, memory.Workspace, cancellationToken, out _) == GlobMatchResult.Match;
    }

    private static bool TryPrepareWords(IReadOnlyList<ExpandedWord> words, EvaluationMemory memory,
        CancellationToken cancellationToken, out ShellValue value, out GlobMask? mask)
    {
        (value, mask) = ExpandedWord.JoinPatternWithSpaces(words, memory);
        cancellationToken.ThrowIfCancellationRequested();
        if (mask == null)
            return true;

        var complete = mask.TryUnescape(value, memory, cancellationToken, out value, out var unescapedMask);
        mask = unescapedMask;
        if (complete)
            return true;

        // Bash reads a final backslash literally unless a star's wildcard run reaches it.
        var bytes = value.Bytes.Span;
        var activity = unescapedMask.AsSpan();
        for (var index = bytes.Length - 2; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activity[index] != GlobMask.Active)
                break;
            if (bytes[index] == (byte)'*')
                return false;
            if (bytes[index] != (byte)'?')
                break;
        }
        // A terminal escape inside an incomplete bracket makes the pattern fail.
        return !TextGlobMatcher.HasIncompleteBracket(bytes, in activity, cancellationToken);
    }

    private static bool IsLiteral(ReadOnlySpan<byte> bytes, GlobMaskSpan mask, CancellationToken cancellationToken) =>
        mask.IndexOfActiveByte(bytes, "*?["u8, cancellationToken) < 0;

    /// <summary>Selects byte traversal for a glob with malformed pattern or subject bytes.</summary>
    public bool UsesByteCharacters(ReadOnlySpan<byte> text) =>
        !_literal && (RequiresByteCharacters || !System.Text.Unicode.Utf8.IsValid(text));

    public bool TryMatchPrefix(ReadOnlySpan<byte> text, bool shortest, CancellationToken cancellationToken, out int length) =>
        TryMatchPrefix(text, shortest, UsesByteCharacters(text), cancellationToken, out length);

    public bool TryMatchPrefix(ReadOnlySpan<byte> text, bool shortest, bool byteCharacters, CancellationToken cancellationToken, out int length)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
        {
            length = _value.Bytes.Length;
            return text.StartsWith(_value.Bytes.Span);
        }
        var mask = Mask;
        return TextGlobMatcher.MatchUtf8Prefix(_value.Bytes.Span, in mask, text,
            shortest, byteCharacters, _memory.Workspace, cancellationToken, out length) == GlobMatchResult.Match;
    }

    public bool TryFind(ReadOnlySpan<byte> text, bool byteCharacters, CancellationToken cancellationToken, out int start, out int length)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
            return TextGlobMatcher.TryFindLiteral(_value.Bytes.Span, text, out start, out length);

        var mask = Mask;
        return TextGlobMatcher.FindUtf8(_value.Bytes.Span, in mask, text,
            byteCharacters, _memory.Workspace, cancellationToken, out start, out length) == GlobMatchResult.Match;
    }

    public TextGlobMatcher.Utf8Search CreateSearch(MemoryCapacity scratch) => new(_value.Bytes.Span, Mask, _literal, scratch);

    public bool TryMatchSuffix(ReadOnlySpan<byte> text, bool shortest, CancellationToken cancellationToken, out int start) =>
        TryMatchSuffix(text, shortest, UsesByteCharacters(text), cancellationToken, out start);

    public bool TryMatchSuffix(ReadOnlySpan<byte> text, bool shortest, bool byteCharacters, CancellationToken cancellationToken, out int start)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
        {
            start = text.Length - _value.Bytes.Length;
            return start >= 0 && text.EndsWith(_value.Bytes.Span);
        }
        ref var prepared = ref (byteCharacters ? ref _byteAtoms : ref _scalarAtoms);
        var mask = Mask;
        prepared ??= TextGlobMatcher.PrepareUtf8Atoms(_value.Bytes.Span, in mask, byteCharacters, _memory, cancellationToken);
        var matched = TextGlobMatcher.MatchUtf8Suffix(_value.Bytes.Span, in mask, prepared.Value.Span, text,
            shortest, byteCharacters, _memory.Workspace, cancellationToken, out var length) == GlobMatchResult.Match;
        start = matched ? text.Length - length : -1;
        return matched;
    }
}
