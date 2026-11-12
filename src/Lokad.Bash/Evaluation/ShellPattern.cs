using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>A prepared byte-valued shell-word pattern, independent of pathname component policies.</summary>
internal sealed class ShellPattern
{
    private readonly ShellValue _value;
    private readonly GlobMask _mask;
    private readonly bool _literal;
    private readonly bool _validUtf8;

    private ShellPattern(ShellValue value, GlobMask mask, bool literal)
    {
        _value = value;
        _mask = mask;
        _literal = literal;
        _validUtf8 = System.Text.Unicode.Utf8.IsValid(value.Bytes.Span);
    }

    public static ShellPattern FromWord(ExpandedWord word, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        var value = word.ToValue(memory);
        var mask = word.GetGlobMask(memory);
        _ = mask.TryUnescape(value, memory, cancellationToken, out value, out mask);
        var activity = mask.AsSpan();
        var literal = true;
        for (var index = 0; index < value.Bytes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (activity[index] == GlobMask.Active && value.Bytes.Span[index] is (byte)'*' or (byte)'?' or (byte)'[')
                literal = false;
        }
        memory.Charge(256);
        return new ShellPattern(value, mask, literal);
    }

    public bool IsMatch(ReadOnlySpan<byte> text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
            return text.SequenceEqual(_value.Bytes.Span);
        var mask = _mask.AsSpan();
        return TextGlobMatcher.MatchUtf8(_value.Bytes.Span, in mask, text,
            requireEnd: true, !_validUtf8 || !System.Text.Unicode.Utf8.IsValid(text), cancellationToken, out _) == GlobMatchResult.Match;
    }

    public bool TryMatchPrefix(ReadOnlySpan<byte> text, bool shortest, CancellationToken cancellationToken, out int length)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
        {
            length = _value.Bytes.Length;
            return text.StartsWith(_value.Bytes.Span);
        }
        var mask = _mask.AsSpan();
        return TextGlobMatcher.MatchUtf8Prefix(_value.Bytes.Span, in mask, text,
            shortest, !_validUtf8 || !System.Text.Unicode.Utf8.IsValid(text), cancellationToken, out length) == GlobMatchResult.Match;
    }

    public bool TryMatchSuffix(ReadOnlySpan<byte> text, bool shortest, CancellationToken cancellationToken, out int start)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_literal)
        {
            start = text.Length - _value.Bytes.Length;
            return start >= 0 && text.EndsWith(_value.Bytes.Span);
        }

        start = -1;
        var byteCharacters = !_validUtf8 || !System.Text.Unicode.Utf8.IsValid(text);
        var mask = _mask.AsSpan();
        for (var position = 0; ;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TextGlobMatcher.MatchUtf8(_value.Bytes.Span, in mask, text[position..],
                    requireEnd: true, byteCharacters, cancellationToken, out _) == GlobMatchResult.Match)
            {
                start = position;
                if (!shortest)
                    return true;
            }
            if (position == text.Length)
                return start >= 0;
            position += byteCharacters ? 1 : Utf8Text.FirstCharacterLength(text[position..]);
        }
    }
}
