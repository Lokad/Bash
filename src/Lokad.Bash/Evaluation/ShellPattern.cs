using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>A prepared byte-valued shell-word pattern, independent of pathname component policies.</summary>
internal sealed class ShellPattern
{
    private readonly ShellValue _value;
    private readonly GlobMask _mask;
    private readonly bool _literal;
    private readonly bool _validUtf8;
    private readonly EvaluationMemory _memory;
    private ReadOnlyMemory<int>? _scalarAtoms;
    private ReadOnlyMemory<int>? _byteAtoms;

    private ShellPattern(ShellValue value, GlobMask mask, bool literal, EvaluationMemory memory)
    {
        _value = value;
        _mask = mask;
        _literal = literal;
        _validUtf8 = System.Text.Unicode.Utf8.IsValid(value.Bytes.Span);
        _memory = memory;
    }

    public bool IsEmpty => _value.IsEmpty;
    public bool RequiresByteCharacters => !_validUtf8;

    public static ShellPattern FromWord(ExpandedWord word, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var value = word.ToValue(memory);
        var mask = word.GetGlobMask(memory);
        _ = mask.TryUnescape(value, memory, cancellationToken, out value, out mask);
        return FromUnescaped(value, mask, memory, cancellationToken);
    }

    /// <summary>Retains an already unescaped value and its quoting metadata for this command's matching.</summary>
    public static ShellPattern FromUnescaped(ShellValue value, GlobMask mask, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var activity = mask.AsSpan();
        var literal = true;
        for (var index = 0; index < value.Bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activity[index] == GlobMask.Active && value.Bytes.Span[index] is (byte)'*' or (byte)'?' or (byte)'[')
                literal = false;
        }
        memory.Charge(256);
        return new ShellPattern(value, mask, literal, memory);
    }

    /// <summary>Selects one character policy for a complete operand, including all later replacement slices.</summary>
    public bool UsesByteCharacters(ReadOnlySpan<byte> text) => !_validUtf8 || !System.Text.Unicode.Utf8.IsValid(text);

    public bool IsMatch(ReadOnlySpan<byte> text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal) return text.SequenceEqual(_value.Bytes.Span);
        var mask = _mask.AsSpan();
        return TextGlobMatcher.MatchUtf8(_value.Bytes.Span, in mask, text,
            requireEnd: true, UsesByteCharacters(text), cancellationToken, out _) == GlobMatchResult.Match;
    }

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
        var mask = _mask.AsSpan();
        return TextGlobMatcher.MatchUtf8Prefix(_value.Bytes.Span, in mask, text,
            shortest, byteCharacters, cancellationToken, out length) == GlobMatchResult.Match;
    }

    public bool TryFind(ReadOnlySpan<byte> text, bool byteCharacters, CancellationToken cancellationToken, out int start, out int length)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
        {
            start = text.IndexOf(_value.Bytes.Span);
            length = _value.Bytes.Length;
            return start >= 0;
        }
        var mask = _mask.AsSpan();
        return TextGlobMatcher.FindUtf8(_value.Bytes.Span, in mask, text,
            byteCharacters, cancellationToken, out start, out length) == GlobMatchResult.Match;
    }

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
        var mask = _mask.AsSpan();
        prepared ??= TextGlobMatcher.PrepareUtf8Atoms(_value.Bytes.Span, in mask, byteCharacters, _memory, cancellationToken);
        var matched = TextGlobMatcher.MatchUtf8Suffix(_value.Bytes.Span, in mask, prepared.Value.Span, text,
            shortest, byteCharacters, cancellationToken, out var length) == GlobMatchResult.Match;
        start = matched ? text.Length - length : -1;
        return matched;
    }
}
