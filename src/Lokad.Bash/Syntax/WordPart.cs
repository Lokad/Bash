using System;
using System.Collections.Generic;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a segment of a word with a specific quoting context.
/// </summary>
public abstract class WordPart
{
    private protected WordPart(WordPartKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the quoting context of the segment.
    /// </summary>
    public WordPartKind Kind { get; }

    /// <summary>Set by tokenization before a word is published.</summary>
    internal DoubleQuoteBoundary DoubleQuote { get; set; }

    internal bool IsDoubleQuoted => DoubleQuote != DoubleQuoteBoundary.None
        || Kind is WordPartKind.DoubleQuoted or WordPartKind.CommandSubstitutionQuoted;

    /// <summary>
    /// Decodes source or literal bytes at a text-only consumer boundary.
    /// Implementations must reject invalid UTF-8 rather than replace its bytes.
    /// </summary>
    public abstract string GetUtf8Text();

    /// <summary>Returns literal bytes or source bytes without evaluating expansion syntax.</summary>
    public abstract ShellValue GetUnexpandedValue();

    /// <summary>Creates a source segment or an already-literal text segment according to its quote kind.</summary>
    public static WordPart FromText(WordPartKind kind, string text) =>
        FromSource(kind, ShellSource.FromText(text));

    /// <summary>Retains source bytes, applying only the part's literal/source distinction.</summary>
    internal static WordPart FromSource(WordPartKind kind, ShellSource source) => FromSource(kind, source, null);

    internal static WordPart FromSource(WordPartKind kind, ShellSource source, MemoryCapacity? memory) => kind switch
    {
        WordPartKind.SingleQuoted or WordPartKind.Escaped => FromValue(kind, source.ToValue()),
        WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted =>
            new CommandSubstitutionWordPart(kind, new CommandSubstitutionSyntax.Deferred(source)),
        _ => new SourceWordPart(kind, source, memory),
    };

    /// <summary>Creates a non-expanding literal segment without decoding its bytes.</summary>
    public static LiteralWordPart FromValue(WordPartKind kind, ShellValue value)
    {
        if (kind is not (WordPartKind.SingleQuoted or WordPartKind.Escaped))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return new LiteralWordPart(kind, value);
    }
}

/// <summary>Source bytes whose expansion syntax has not yet been evaluated.</summary>
public sealed class SourceWordPart : WordPart
{
    private readonly IReadOnlyDictionary<int, ArithmeticExpansion> _arithmetic;
    private readonly int _arithmeticOffset;
    private static readonly IReadOnlyDictionary<int, ArithmeticExpansion> NoArithmetic = new Dictionary<int, ArithmeticExpansion>();

    /// <summary>Brace sequences can synthesize escapes after the original word was tokenized.</summary>
    internal bool IsBraceExpanded { get; init; }
    // The bytes still carry nested expansion operands in source form.
    internal int RenderedLineAdjustment { get; init; }
    internal ParameterSubscriptFailure ParameterSubscriptFailure { get; init; }

    /// <summary>The selected lexical span and its operand. A missing operand fails if reached.</summary>
    internal readonly record struct ArithmeticExpansion(int End, ArithmeticSource? Operand);
    internal SourceWordPart(WordPartKind kind, ShellSource source, MemoryCapacity? memory) : base(kind)
    {
        Source = source;
        _arithmetic = PrepareArithmetic(kind, source, memory);
    }

    private SourceWordPart(WordPartKind kind, ShellSource source,
        IReadOnlyDictionary<int, ArithmeticExpansion> arithmetic, int offset) : base(kind)
    {
        Source = source;
        _arithmetic = arithmetic;
        _arithmeticOffset = offset;
    }

    /// <summary>Assignment and descriptor punctuation borrow the same immutable lexical metadata.</summary>
    internal SourceWordPart Slice(int start, int length, MemoryCapacity? memory)
    {
        var source = Source.Slice(start, length);
        if (length != 0 && Source.Length > 2L * length)
        {
            memory?.Grow(checked(32L + length));
            source = ShellSource.FromBytes(source.Bytes.ToArray());
        }
        return new(Kind, source, _arithmetic, checked(_arithmeticOffset + start))
        {
            DoubleQuote = DoubleQuote,
            IsBraceExpanded = IsBraceExpanded,
            RenderedLineAdjustment = RenderedLineAdjustment,
            ParameterSubscriptFailure = ParameterSubscriptFailure
        };
    }

    internal bool TryGetArithmetic(int start, out ArithmeticExpansion expansion)
    {
        if (_arithmetic.TryGetValue(checked(start + _arithmeticOffset), out var cached)
            && cached.End < (long)_arithmeticOffset + Source.Length)
        {
            expansion = new ArithmeticExpansion(cached.End - _arithmeticOffset, cached.Operand);
            return true;
        }
        expansion = default;
        return false;
    }

    private static IReadOnlyDictionary<int, ArithmeticExpansion> PrepareArithmetic(
        WordPartKind kind, ShellSource source, MemoryCapacity? memory)
    {
        Dictionary<int, ArithmeticExpansion>? result = null;
        var bytes = source.AsSpan();
        for (var cursor = 0; cursor < bytes.Length;)
        {
            var dollar = bytes[cursor..].IndexOf((byte)'$');
            if (dollar < 0)
                break;
            var index = cursor + dollar;
            cursor = index + 1;
            if (!ArithmeticExpansionScanner.HasPrefix(source, index))
                continue;
            var context = new ShellScanContext { SyntaxMemory = memory };
            var status = ArithmeticExpansionScanner.Scan(context, source, index, out var end,
                out var expression, out _, out var bracketed, out _, out _);
            if (status != ShellScanStatus.Complete)
                continue;
            memory?.Grow(256);
            var operand = bracketed && kind == WordPartKind.DoubleQuoted
                && !ArithmeticExpansionScanner.TryPrepareDoubleQuotedBracketOperand(context, ref expression)
                ? null
                : new ArithmeticSource(expression, memory);
            (result ??= new Dictionary<int, ArithmeticExpansion>())
                .Add(index, new ArithmeticExpansion(end, operand));
            cursor = end + 1;
        }
        return result ?? NoArithmetic;
    }

    /// <summary>Gets unexpanded syntax without text conversion.</summary>
    internal ShellSource Source { get; }

    public override string GetUtf8Text() => Source.ToUtf8String();

    public override ShellValue GetUnexpandedValue() => Source.ToValue();
}

internal enum DoubleQuoteBoundary : byte { None, Start, Continuation }

/// <summary>A failed substitution in generated syntax, reported in expansion order.</summary>
internal sealed class BraceExpansionFailureWordPart(ShellValue message) : WordPart(WordPartKind.Unquoted)
{
    internal ShellValue Message { get; } = message;
    public override string GetUtf8Text() => Message.ToUtf8String();
    public override ShellValue GetUnexpandedValue() => Message;

    internal static ShellValue FormatMissingDelimiter(ShellSource suffix, byte delimiter, MemoryCapacity memory)
    {
        var message = new ShellScanContext { SyntaxMemory = memory }.CreateByteBuilder(0);
        message.Append("no closing \""u8).Append(delimiter).Append("\" in "u8).Append(suffix.Bytes);
        return ShellValue.FromBytes(message.ToMemory());
    }
}

/// <summary>Quoted or unquoted substitution syntax shared by tokenization and execution.</summary>
internal sealed class CommandSubstitutionWordPart : WordPart
{
    internal CommandSubstitutionWordPart(WordPartKind kind, CommandSubstitutionSyntax substitution) : base(kind)
    {
        if (kind is not (WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Substitution = substitution;
    }

    internal CommandSubstitutionSyntax Substitution { get; }

    public override string GetUtf8Text() => Substitution.Source.ToUtf8String();

    public override ShellValue GetUnexpandedValue() => Substitution.Source.ToValue();
}

/// <summary>Already-decoded literal bytes, protected from expansion.</summary>
public sealed class LiteralWordPart : WordPart
{
    internal LiteralWordPart(WordPartKind kind, ShellValue value) : base(kind)
    {
        Value = value;
    }

    /// <summary>Gets literal bytes without interpreting them as source or UTF-8 text.</summary>
    public ShellValue Value { get; }

    public override string GetUtf8Text() => Value.ToUtf8String();

    public override ShellValue GetUnexpandedValue() => Value;
}
