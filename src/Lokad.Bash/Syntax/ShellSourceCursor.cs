using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Traverses shell source while applying shared quote, escape, and nested-expansion rules.</summary>
internal sealed class ShellSourceCursor
{
    /// <summary>Bounds recursive scanner composition before it can threaten the CLR stack.</summary>
    internal const int MaximumNestedExpansionDepth = 256;

    /// <summary>The immutable complete source being scanned.</summary>
    private readonly ShellSource _text;

    /// <summary>The recursion budget shared by mutually recursive nested scanners.</summary>
    private readonly ShellScanContext _context;

    /// <summary>The currently open shell quote, if any.</summary>
    private ShellQuoteKind _quote;

    /// <summary>Creates a cursor at one validated source position with no open quote.</summary>
    public ShellSourceCursor(ShellSource text, int position)
        : this(text, position, new ShellScanContext())
    {
    }

    internal ShellSourceCursor(ShellSource text, int position, ShellScanContext context)
    {
        if (position < 0 || position > text.Length)
            throw new System.ArgumentOutOfRangeException(nameof(position));

        _text = text;
        _context = context;
        Position = position;
    }

    /// <summary>Gets the next source position to inspect.</summary>
    public int Position { get; private set; }

    /// <summary>Gets whether no source byte remains.</summary>
    public bool IsAtEnd => Position >= _text.Length;

    /// <summary>Gets the current byte.</summary>
    /// <exception cref="System.InvalidOperationException">Thrown at end of input.</exception>
    public byte Current => !IsAtEnd
        ? _text[Position]
        : throw new System.InvalidOperationException("The shell source cursor is at end of input.");

    /// <summary>Gets whether scanning is inside single, double or ANSI-C quotes.</summary>
    public bool IsQuoted => _quote != ShellQuoteKind.None;

    /// <summary>Gets whether scanning is specifically inside double quotes.</summary>
    public bool IsDoubleQuoted => _quote == ShellQuoteKind.Double;

    /// <summary>Advances one byte, never moving beyond the end position.</summary>
    public void Advance()
    {
        if (!IsAtEnd)
            Position++;
    }

    /// <summary>Skips consecutive backslash-LF pairs, retaining physical source offsets.</summary>
    public static int SkipLineContinuations(ReadOnlySpan<byte> source, int position)
    {
        while (position < source.Length - 1 && source[position] == (byte)'\\' && source[position + 1] == (byte)'\n')
            position += 2;
        return position;
    }

    /// <summary>Skips token spaces, tabs and backslash-LF pairs, but no physical newline or CR.</summary>
    public static int SkipTokenSpacing(ReadOnlySpan<byte> source, int position)
    {
        while (true)
        {
            position = SkipLineContinuations(source, position);
            if (position >= source.Length || source[position] is not ((byte)' ' or (byte)'\t'))
                return position;
            position++;
        }
    }

    /// <summary>Skips an already selected line comment; only LF ends it, including after backslash.</summary>
    internal static int SkipComment(ReadOnlySpan<byte> source, int position)
    {
        var length = source[position..].IndexOf((byte)'\n');
        return length < 0 ? source.Length : position + length;
    }

    /// <summary>Reads one ordinary or ANSI-C literal, retaining its physical span even after decoded NUL.</summary>
    internal bool TryReadQuotedLiteral(out ShellValue value)
    {
        value = default;
        if (!TryReadQuotedLiteralSource(out var payload, out var kind))
            return false;
        // Literal arguments may survive source execution (for example, set -- 'value').
        // Keep their storage independent of the complete input buffer.
        if (kind == ShellQuoteKind.AnsiC)
            value = ShellEscapeDecoder.DecodeAnsiCValue(payload.AsSpan(), _context.SyntaxMemory);
        else
        {
            _context.SyntaxMemory?.Grow(checked(64L + payload.Length));
            value = ShellValue.FromBytes(payload.Bytes.ToArray());
        }
        return true;
    }

    /// <summary>Advances over one literal without decoding again when the token already owns its value.</summary>
    internal bool TryReadQuotedLiteralSource(out ShellSource payload, out ShellQuoteKind kind)
    {
        payload = default;
        kind = Current == (byte)'$' ? ShellQuoteKind.AnsiC : ShellQuoteKind.Single;
        _ = TryConsumeQuoteOrEscape();
        var start = Position;
        while (!IsAtEnd && IsQuoted)
            _ = TryConsumeQuoteOrEscape();
        if (IsQuoted)
            return false;

        payload = _text.Slice(start, Position - start - 1);
        return true;
    }

    /// <summary>
    /// Matches an unquoted ASCII token across backslash-LF pairs. Failure leaves the returned
    /// offset at the start; success returns the physical position after its final character.
    /// </summary>
    public static bool TryMatchLogicalText(
        ReadOnlySpan<byte> source,
        int startIndex,
        ReadOnlySpan<char> expected,
        out int endIndex)
    {
        var position = startIndex;
        foreach (var character in expected)
        {
            position = SkipLineContinuations(source, position);
            if (position >= source.Length || source[position] != character)
            {
                endIndex = startIndex;
                return false;
            }
            position++;
        }
        endIndex = position;
        return true;
    }

    /// <summary>
    /// Removes this lexical layer's continuations using the cursor's quote/escape rules.
    /// Nested expansions retain their source for their own parser, including heredoc data.
    /// </summary>
    public static ShellSource RemoveLineContinuations(ShellSource source)
    {
        if (source.AsSpan().IndexOf("\\\n"u8) < 0)
            return source;
        return RemoveLineContinuations(new ShellScanContext(), source);
    }

    /// <summary>Normalizes a scanned payload without rebuilding its nested lookahead syntax.</summary>
    internal static ShellSource RemoveLineContinuations(ShellScanContext context, ShellSource source)
    {
        if (source.AsSpan().IndexOf("\\\n"u8) < 0)
            return source;

        var result = context.CreateByteBuilder(source.Length);
        var cursor = new ShellSourceCursor(source, 0, context);
        while (!cursor.IsAtEnd)
        {
            var start = cursor.Position;
            var next = SkipLineContinuations(source.AsSpan(), start);
            if (cursor._quote is not (ShellQuoteKind.Single or ShellQuoteKind.AnsiC) && next != start)
            {
                cursor.Position = next;
                continue;
            }

            var precedingQuote = cursor._quote;
            if (!cursor.TryConsumeQuoteOrEscape()
                && cursor.SkipNestedExpansion(BracedParameterRecognition.All) is not ShellNestedExpansionScan.Complete)
            {
                cursor.Advance();
            }
            if (precedingQuote == ShellQuoteKind.None && cursor._quote == ShellQuoteKind.AnsiC)
                result.Append("$'"u8);
            else
                result.Append(source.AsSpan(start, cursor.Position - start));
        }
        return ShellSource.FromBytes(result.ToMemory());
    }

    /// <summary>
    /// Consumes quote syntax, quoted content, or one backslash escape and updates quote
    /// state. In double quotes, backslash escapes only dollar, backtick, double quote,
    /// backslash, and newline; before any other character the backslash alone is consumed.
    /// ANSI-C quotes protect escaped apostrophes and suppress all nested expansions.
    /// </summary>
    /// <returns>True when at least the current character was consumed.</returns>
    public bool TryConsumeQuoteOrEscape()
    {
        var current = Current;
        if (_quote == ShellQuoteKind.AnsiC)
        {
            if (current == (byte)'\'')
                _quote = ShellQuoteKind.None;
            Position += current == (byte)'\\' && Position + 1 < _text.Length ? 2 : 1;
            return true;
        }

        if (_quote == ShellQuoteKind.Single)
        {
            if (current == (byte)'\'')
                _quote = ShellQuoteKind.None;

            Position++;
            return true;
        }

        if (_quote == ShellQuoteKind.None && current == (byte)'$'
            && TryMatchLogicalText(_text.AsSpan(), Position, "$'", out var literalStart))
        {
            _quote = ShellQuoteKind.AnsiC;
            Position = literalStart;
            return true;
        }

        if (current == (byte)'\\')
        {
            if (_quote != ShellQuoteKind.Double)
            {
                Position += Position + 1 < _text.Length ? 2 : 1;
                return true;
            }

            if (Position + 1 < _text.Length
                && _text[Position + 1] is (byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\' or (byte)'\n')
            {
                Position += 2;
                return true;
            }

            Position++;
            return true;
        }

        if (current == (byte)'"')
        {
            _quote = _quote == ShellQuoteKind.Double
                ? ShellQuoteKind.None
                : ShellQuoteKind.Double;
            Position++;
            return true;
        }

        if (_quote == ShellQuoteKind.None && current == (byte)'\'')
        {
            _quote = ShellQuoteKind.Single;
            Position++;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to consume one nested arithmetic, parameter, or command substitution.
    /// A <see cref="ShellNestedExpansionScan.Complete"/> result advances past the closing
    /// delimiter; every other result leaves the cursor at the original position.
    /// Arithmetic commands leave unquoted parameter operands to their own delimiter rules;
    /// those operands are validated only when execution reaches the command.
    /// </summary>
    public ShellNestedExpansionScan SkipNestedExpansion(BracedParameterRecognition parameters)
    {
        if (_quote is ShellQuoteKind.Single or ShellQuoteKind.AnsiC)
            return ShellNestedExpansionScan.NotFound.Instance;

        var start = Position;
        if (ArithmeticExpansionScanner.HasPrefix(_text, start) && !_context.TryGetCommandSubstitution(_text, start, out _))
        {
            var status = ArithmeticExpansionScanner.ScanShape(
                _context,
                _text,
                start,
                out var endIndex,
                out var containsCommandSubstitution);
            if (status == ShellScanStatus.Complete)
            {
                Position = endIndex + 1;
                return new ShellNestedExpansionScan.Complete(
                    ShellNestedExpansionKind.Arithmetic,
                    containsCommandSubstitution);
            }
            if (status != ShellScanStatus.NotFound)
                return ToFailure(status, ShellNestedExpansionKind.Arithmetic);
        }

        if ((parameters == BracedParameterRecognition.All || IsDoubleQuoted)
            && BracedParameterScanner.HasPrefix(_text, start))
        {
            var status = BracedParameterScanner.ScanShape(
                _context,
                _text,
                start,
                out var endIndex,
                out _,
                out var containsCommandSubstitution);
            if (status != BracedParameterScanStatus.Found)
            {
                return status is BracedParameterScanStatus.Unterminated
                    or BracedParameterScanStatus.UnterminatedCommandSubstitution
                    ? new ShellNestedExpansionScan.Incomplete(ShellNestedExpansionKind.Parameter)
                    : new ShellNestedExpansionScan.Invalid(ShellNestedExpansionKind.Parameter);
            }

            Position = endIndex + 1;
            return new ShellNestedExpansionScan.Complete(
                ShellNestedExpansionKind.Parameter,
                containsCommandSubstitution);
        }

        if (CommandSubstitutionScanner.HasPrefix(_text, start))
        {
            var status = CommandSubstitutionScanner.ScanParsedForLaterReuse(
                _context,
                _text,
                start,
                out var endIndex,
                out _);
            if (status != ShellScanStatus.Complete)
                return ToFailure(status, ShellNestedExpansionKind.Command);

            Position = endIndex + 1;
            return new ShellNestedExpansionScan.Complete(
                ShellNestedExpansionKind.Command,
                ContainsCommandSubstitution: true);
        }

        return ShellNestedExpansionScan.NotFound.Instance;

        static ShellNestedExpansionScan ToFailure(
            ShellScanStatus status,
            ShellNestedExpansionKind kind) =>
            status == ShellScanStatus.Incomplete
                ? new ShellNestedExpansionScan.Incomplete(kind)
                : new ShellNestedExpansionScan.Invalid(kind);
    }

}

/// <summary>Controls whether a delimiter scan consumes unquoted braced parameter operands.</summary>
internal enum BracedParameterRecognition
{
    All,
    DoubleQuotedOnly,
}

/// <summary>Owns recursion bounds and temporary lookahead parses for one source traversal.</summary>
internal sealed class ShellScanContext
{
    // A token accounts for its command/word and associated collections. Word parts
    // are charged separately because one word can contain many quoted fragments.
    // Nested delimiter parsing and cached array brackets share this allowance.
    internal const int MaximumSyntaxUnits = 65536;
    internal const int MaximumCommandDepth = 64;
    private int _remainingSyntaxUnits = MaximumSyntaxUnits;
    private int _commandDepth;
    private int _nestedExpansionDepth;
    private ScanDepth _lookaheadPeak;
    private Dictionary<ReadOnlyMemory<byte>, CachedParse>? _commandSubstitutions;

    /// <summary>One reached command unit's coarse syntax and payload allowance; null denotes caller-owned parsing.</summary>
    internal MemoryCapacity? SyntaxMemory { get; set; }

    internal ByteMemoryBuilder CreateByteBuilder(int capacity) => SyntaxMemory is { } memory
        ? new ByteMemoryBuilder(capacity, memory) : ByteMemoryBuilder.CreateUnaccounted(capacity);

    internal BoundedByteMemoryBuilder CreateBoundedByteBuilder(int capacity, int maximumLength) => SyntaxMemory is { } memory
        ? new BoundedByteMemoryBuilder(capacity, maximumLength, memory)
        : BoundedByteMemoryBuilder.CreateUnaccounted(capacity, maximumLength);

    // No completed unit can need a later lookup of its consumed source positions.
    internal void ClearCommandUnitLookahead() => _commandSubstitutions = null;

    internal bool TryTakeCommandSubstitution(ShellSource source, int startIndex, out CachedParse parsed)
        => TryFindCommandSubstitution(source, startIndex, take: true, out parsed);

    internal bool TryGetCommandSubstitution(ShellSource source, int startIndex, out CachedParse parsed)
        => TryFindCommandSubstitution(source, startIndex, take: false, out parsed);

    private bool TryFindCommandSubstitution(ShellSource source, int startIndex, bool take, out CachedParse parsed)
    {
        if (_commandSubstitutions != null && (uint)startIndex < (uint)source.Length)
        {
            var key = source.Bytes.Slice(startIndex, 0);
            if (_commandSubstitutions.TryGetValue(key, out parsed) && parsed.Length <= source.Length - startIndex
                && _nestedExpansionDepth + parsed.RequiredDepth.Expansion <= ShellSourceCursor.MaximumNestedExpansionDepth
                && _commandDepth + parsed.RequiredDepth.Command <= MaximumCommandDepth)
            {
                RecordLookaheadDepth(_nestedExpansionDepth + parsed.RequiredDepth.Expansion, _commandDepth + parsed.RequiredDepth.Command);
                if (take) _commandSubstitutions.Remove(key);
                return true;
            }
        }
        parsed = default;
        return false;
    }

    internal void StoreCommandSubstitution(
        ShellSource source, int startIndex, int length, CommandSubstitutionSyntax substitution)
    {
        // A zero-length slice identifies a physical byte across source views.
        var required = new ScanDepth(_lookaheadPeak.Expansion - _nestedExpansionDepth, _lookaheadPeak.Command - _commandDepth);
        (_commandSubstitutions ??= [])[source.Bytes.Slice(startIndex, 0)] = new CachedParse(length, substitution, required);
    }

    internal readonly record struct CachedParse(int Length, CommandSubstitutionSyntax Substitution, ScanDepth RequiredDepth);

    internal readonly record struct ScanDepth(int Expansion, int Command);

    // Track the actual depths a tree needs, including any cached descendants.
    // An arithmetic lookahead can prepare a child at a different command depth.
    internal ScanDepth BeginLookahead()
    {
        var previous = _lookaheadPeak;
        _lookaheadPeak = new ScanDepth(_nestedExpansionDepth, _commandDepth);
        return previous;
    }

    internal void EndLookahead(ScanDepth previous) => RecordLookaheadDepth(previous.Expansion, previous.Command);

    private void RecordLookaheadDepth(int expansion, int command) => _lookaheadPeak =
        new ScanDepth(Math.Max(_lookaheadPeak.Expansion, expansion), Math.Max(_lookaheadPeak.Command, command));

    internal bool ParsingLimitExceeded { get; private set; }

    /// <summary>Preserves hard parsing-limit failures across nested delimiter lookahead.</summary>
    internal SyntaxError? LimitError { get; private set; }

    internal void RecordLimitError(SyntaxError error) => LimitError ??= error;

    internal bool TryReserveSyntax()
    {
        if (_remainingSyntaxUnits == 0)
        {
            ParsingLimitExceeded = true;
            return false;
        }
        SyntaxMemory?.Grow(256); // Tokens/parts, command nodes, collections and prepared navigation.
        _remainingSyntaxUnits--;
        return true;
    }

    internal bool TryEnterCommand()
    {
        if (_commandDepth == MaximumCommandDepth)
        {
            ParsingLimitExceeded = true;
            return false;
        }
        _commandDepth++;
        RecordLookaheadDepth(_nestedExpansionDepth, _commandDepth);
        return true;
    }

    internal void ExitCommand() => _commandDepth--;

    /// <summary>Reserves one recursive scan level when the bounded budget permits it.</summary>
    public bool TryEnter()
    {
        if (_nestedExpansionDepth >= ShellSourceCursor.MaximumNestedExpansionDepth
            || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return false;

        _nestedExpansionDepth++;
        RecordLookaheadDepth(_nestedExpansionDepth, _commandDepth);
        return true;
    }

    /// <summary>Returns one previously reserved recursive scan level.</summary>
    public void Exit() => _nestedExpansionDepth--;
}

/// <summary>Identifies the shell quote currently controlling delimiter recognition.</summary>
internal enum ShellQuoteKind
{
    /// <summary>No quote is open.</summary>
    None,

    /// <summary>A single quote is open; every character except the closing quote is literal.</summary>
    Single,

    /// <summary>An ANSI-C quote is open; backslash protects the next character from closing it.</summary>
    AnsiC,

    /// <summary>A double quote is open; selected dollar, backtick, and backslash processing remains active.</summary>
    Double,
}

/// <summary>Identifies the nested expansion whose delimiter scan was attempted.</summary>
internal enum ShellNestedExpansionKind
{
    /// <summary>A <c>${...}</c> parameter expansion.</summary>
    Parameter,

    /// <summary>A parenthesized or bracketed arithmetic expansion.</summary>
    Arithmetic,

    /// <summary>A <c>$(...)</c> command substitution.</summary>
    Command,
}

/// <summary>Represents every outcome of scanning one nested shell expansion.</summary>
internal abstract record ShellNestedExpansionScan
{
    private ShellNestedExpansionScan()
    {
    }

    /// <summary>The current source position does not begin a supported nested expansion.</summary>
    internal sealed record NotFound : ShellNestedExpansionScan
    {
        private NotFound()
        {
        }

        /// <summary>Gets the allocation-free result shared by all absent-prefix scans.</summary>
        internal static NotFound Instance { get; } = new();
    }

    /// <summary>A complete nested expansion was consumed.</summary>
    internal sealed record Complete(
        ShellNestedExpansionKind Kind,
        bool ContainsCommandSubstitution) : ShellNestedExpansionScan;

    /// <summary>A nested prefix was present but input ended before it closed.</summary>
    internal sealed record Incomplete(ShellNestedExpansionKind Kind) : ShellNestedExpansionScan;

    /// <summary>A nested prefix was present but contained malformed nested syntax.</summary>
    internal sealed record Invalid(ShellNestedExpansionKind Kind) : ShellNestedExpansionScan;
}

/// <summary>Reports the four semantic outcomes shared by delimiter scanners.</summary>
internal enum ShellScanStatus
{
    /// <summary>The requested position does not begin the scanner's syntax form.</summary>
    NotFound,

    /// <summary>A complete, structurally valid form and its closing delimiter were found.</summary>
    Complete,

    /// <summary>A valid prefix reaches end of input before its closing delimiter.</summary>
    Incomplete,

    /// <summary>A delimiter candidate contains malformed nested syntax.</summary>
    Invalid,
}
