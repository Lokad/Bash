using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Evaluation;

/// <summary>Parses test/[ arguments with argument-count rules and strict decimal integers.</summary>
public sealed class TestEvaluation
{
    private readonly IReadOnlyList<ShellValue> _arguments;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _errexitEnabled;
    // Standalone parsing has no execution owner; the evaluator supplies its command workspace.
    private readonly EvaluationMemory? _memory;
    private readonly List<Step> _steps = [];
    private readonly List<UnresolvedFileTest> _files = [];
    private int _index;
    private int _end;
    private int _stackDepth;
    private int _maximumStackDepth;

    private TestEvaluation(IReadOnlyList<ShellValue> arguments, int firstArgument, int argumentLimit,
        CancellationToken cancellationToken, bool errexitEnabled, EvaluationMemory? memory)
    {
        _arguments = arguments;
        _cancellationToken = cancellationToken;
        _errexitEnabled = errexitEnabled;
        _memory = memory;
        _index = firstArgument;
        _end = argumentLimit;
        ParseArguments();
        if (!HasParseError && _index != _end)
        {
            if (_index < _end && _arguments[_index].Bytes.Span.StartsWith("-"u8))
                Fail("syntax error: `", _arguments[_index], "' unexpected");
            else
                Fail("too many arguments");
        }
    }

    /// <summary>Gets whether arguments contain a syntax, integer or text-boundary error.</summary>
    public bool HasParseError => ParseError.HasValue;
    internal (string Prefix, ShellValue Operand, string Suffix)? ParseError { get; private set; }
    internal bool HasVariablePredicates { get; private set; }

    /// <summary>Encodes text arguments and parses them through the byte-valued test grammar.</summary>
    public static TestEvaluation Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new ShellValue[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
            values[index] = ShellValue.FromText(arguments[index]);
        return new TestEvaluation(values, firstArgument: 0, values.Length, CancellationToken.None, false, null);
    }

    /// <summary>Borrows the expanded operands in the given range through predicate evaluation.</summary>
    internal static TestEvaluation ParseValues(IReadOnlyList<ShellValue> arguments, int firstArgument, int argumentLimit,
        CancellationToken cancellationToken, bool errexitEnabled, EvaluationMemory memory) =>
        new(arguments, firstArgument, argumentLimit, cancellationToken, errexitEnabled, memory);

    /// <summary>Collects host predicates; pure predicates are already validated during parsing.</summary>
    internal IReadOnlyList<UnresolvedFileTest> CollectFileTests(out bool? result)
    {
        result = !HasParseError && _files.Count == 0 ? CompleteFileTests([]) : null;
        return HasParseError ? [] : _files;
    }

    /// <summary>Completes the parsed expression using filesystem predicate results in collection order.</summary>
    public bool CompleteFileTests(IReadOnlyList<bool> fileResults)
    {
        ArgumentNullException.ThrowIfNull(fileResults);
        return CompletePredicates(fileResults);
    }

    private bool CompletePredicates(IReadOnlyList<bool> fileResults)
    {
        if (HasParseError)
            return false;

        var values = new bool[_maximumStackDepth];
        var count = 0;
        foreach (var step in _steps)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (step.Kind)
            {
                case StepKind.Constant: values[count++] = step.Value != 0; break;
                case StepKind.File: values[count++] = fileResults[step.Value]; break;
                case StepKind.Not: Negate(values, count); break;
                case StepKind.And:
                case StepKind.Or: Combine(values, --count, step.Kind == StepKind.And); break;
            }
        }
        return count == 1 && values[0];
    }

    /// <summary>Evaluates reached leaves in argument order, including both sides of -a and -o.</summary>
    internal async Task<bool> EvaluateVariablePredicatesAsync(
        ScopedEvaluation scope, Func<UnresolvedFileTest, Task<bool>> testFile)
    {
        // A parse error still allows earlier parsed predicates to run. Its partial
        // step sequence can contain operators without all their operands.
        var reduce = !HasParseError;
        var values = reduce ? new bool[_maximumStackDepth] : [];
        var count = 0;
        foreach (var step in _steps)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (!scope.CanExpand)
                return false;
            bool value;
            switch (step.Kind)
            {
                case StepKind.Constant:
                    value = step.Value != 0;
                    break;
                case StepKind.File:
                    value = await testFile(_files[step.Value]).ConfigureAwait(false);
                    break;
                case StepKind.VariableIsSet:
                case StepKind.NamerefIsSet:
                    var kind = step.Kind == StepKind.VariableIsSet ? UnaryPredicateKind.VariableIsSet : UnaryPredicateKind.NamerefIsSet;
                    value = await WordExpansion.TestVariableAsync(kind, _arguments[step.Value], scope)
                        .ConfigureAwait(false);
                    break;
                case StepKind.Not:
                    if (reduce)
                        Negate(values, count);
                    continue;
                case StepKind.And:
                case StepKind.Or:
                    if (reduce)
                        Combine(values, --count, step.Kind == StepKind.And);
                    continue;
                default: throw new InvalidOperationException("Unsupported test predicate step.");
            }
            if (reduce)
                values[count++] = value;
        }
        if (reduce)
            _cancellationToken.ThrowIfCancellationRequested();
        return count == 1 && values[0];
    }

    private static void Negate(bool[] values, int count) => values[count - 1] = !values[count - 1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Combine(bool[] values, int rightIndex, bool conjunction)
    {
        var right = values[rightIndex];
        values[rightIndex - 1] = conjunction ? values[rightIndex - 1] && right : values[rightIndex - 1] || right;
    }

    // The zero-to-four argument rules precede the ordinary -a/-o expression grammar.
    private void ParseArguments()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        switch (_end - _index)
        {
            case 0: Constant(false); return;
            case 1: Constant(!Next().IsEmpty); return;
            case 2:
                var first = Next();
                if (Is(first, "!"u8))
                    Constant(Next().IsEmpty);
                else if (TryUnary(first, out var unary))
                    Unary(unary);
                else
                    Fail(first, ": unary operator expected");
                return;
            case 3:
                var op = _arguments[_index + 1];
                if (TryTestBinary(op, out var binary))
                {
                    var left = Next();
                    _index++;
                    Binary(binary, left, Next());
                    return;
                }
                if (Is(op, "-a"u8) || Is(op, "-o"u8))
                {
                    var left = !Next().IsEmpty;
                    _index++;
                    var right = !Next().IsEmpty;
                    Constant(Is(op, "-a"u8) ? left && right : left || right);
                    return;
                }
                if (Is(_arguments[_index], "!"u8))
                    goto case 4;
                if (_arguments[_index].Bytes.Span.StartsWith("("u8) && _arguments[_end - 1].Bytes.Span.StartsWith(")"u8))
                {
                    Constant(!op.IsEmpty);
                    _index = _end;
                    return;
                }
                Fail(op, ": binary operator expected");
                return;
            case 4:
                if (Is(_arguments[_index], "!"u8))
                {
                    _index++;
                    ParseArguments();
                    _steps.Add(new(StepKind.Not, 0));
                    return;
                }
                if (Is(_arguments[_index], "("u8) && Is(_arguments[_end - 1], ")"u8))
                {
                    _index++;
                    _end--;
                    ParseArguments();
                    _end++;
                    _index++;
                    return;
                }
                break;
        }
        ParseOr(0);
    }

    private void ParseOr(int depth)
    {
        ParseAnd(depth);
        while (!HasParseError && Consume("-o"u8))
        {
            ParseAnd(depth);
            AppendBinaryOperator(StepKind.Or);
        }
    }

    private void ParseAnd(int depth)
    {
        ParseTerm(depth);
        while (!HasParseError && Consume("-a"u8))
        {
            ParseTerm(depth);
            AppendBinaryOperator(StepKind.And);
        }
    }

    private void ParseTerm(int depth)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var negated = false;
        while (Consume("!"u8))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            negated = !negated;
        }
        if (_index >= _end)
        {
            Fail("argument expected");
            return;
        }
        if (Consume("("u8))
        {
            if (depth >= ShellSourceCursor.MaximumNestedExpansionDepth)
                Fail("expression nesting limit exceeded");
            else
            {
                ParseOr(depth + 1);
                if (!Consume(")"u8))
                {
                    // The closing bracket stays available for diagnostics after the expression ends.
                    if (_index < _arguments.Count)
                        Fail("`)' expected, found ", _arguments[_index], string.Empty);
                    else
                        Fail("`)' expected");
                }
            }
        }
        else if (_index + 2 < _end && TryTestBinary(_arguments[_index + 1], out var binary))
        {
            var left = Next();
            _index++;
            Binary(binary, left, Next());
        }
        else
        {
            var first = Next();
            if (_index < _end && TryUnary(first, out var unary))
                Unary(unary);
            else
                Constant(!first.IsEmpty);
        }
        if (negated)
            _steps.Add(new(StepKind.Not, 0));
    }

    private void Unary(UnaryPredicate predicate)
    {
        var operandIndex = _index;
        var operand = Next();
        if (predicate.IsFilePredicate)
            File(predicate.FileKind, operand, null);
        else if (predicate.Operator == UnaryPredicateKind.OptionEnabled)
            Constant(IsOptionEnabled(operand.Bytes.Span, _errexitEnabled));
        else if (predicate.IsVariablePredicate)
        {
            HasVariablePredicates = true;
            AppendPredicate(predicate.Operator == UnaryPredicateKind.VariableIsSet ? StepKind.VariableIsSet : StepKind.NamerefIsSet,
                operandIndex);
        }
        else
            Constant(predicate.Operator == UnaryPredicateKind.StringLengthZero ? operand.IsEmpty : !operand.IsEmpty);
    }

    private void Binary(BinaryPredicate predicate, ShellValue left, ShellValue right)
    {
        if (predicate.IsFilePredicate)
            File(predicate.FileKind, left, right);
        else if (predicate.IsNumeric)
        {
            if (!TryInteger(left, out var l))
                Fail(left, ": integer expression expected");
            else if (!TryInteger(right, out var r))
                Fail(right, ": integer expression expected");
            else
                Constant(CompareNumbers(predicate.Operator, l, r));
        }
        else
        {
            var comparison = ShellValue.ByteOrder.Compare(left, right);
            Constant(predicate.Operator switch
            {
                BinaryPredicateKind.StringEqual => comparison == 0,
                BinaryPredicateKind.StringNotEqual => comparison != 0,
                BinaryPredicateKind.StringLess => comparison < 0,
                BinaryPredicateKind.StringGreater => comparison > 0,
                _ => throw new InvalidOperationException("Unsupported test predicate.")
            });
        }

        bool TryInteger(ShellValue value, out long number)
        {
            var span = value.Bytes.Span;
            var start = 0;
            var end = span.Length;
            while (start < end && AsciiByte.IsPosixWhitespace(span[start]))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                start++;
            }
            while (end > start && AsciiByte.IsPosixWhitespace(span[end - 1]))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                end--;
            }
            return Integers.TryParseNumber(span[start..end], out number);
        }
    }

    private void File(FilePredicateKind kind, ShellValue left, ShellValue? right)
    {
        _memory?.Charge(checked(4L * (left.Bytes.Length + (long)(right?.Bytes.Length ?? 0))));
        if (!left.TryGetUtf8Text(out var path))
        {
            Fail(left, ": file operand is not valid UTF-8");
            return;
        }
        string? otherPath = null;
        if (right.HasValue && !right.Value.TryGetUtf8Text(out otherPath))
        {
            Fail(right.Value, ": file operand is not valid UTF-8");
            return;
        }
        AppendPredicate(StepKind.File, _files.Count);
        _files.Add(new(kind, path, otherPath));
    }

    private static bool TryTestBinary(ShellValue token, out BinaryPredicate predicate) =>
        TryGetBinaryPredicate(token.Bytes.Span, out predicate) && predicate.Operator != BinaryPredicateKind.RegexMatch;

    // The public file-only adapter has no shell bindings or selector executor.
    private bool TryUnary(ShellValue token, out UnaryPredicate predicate) =>
        TryGetUnaryPredicate(token.Bytes.Span, out predicate) && (!predicate.IsVariablePredicate || _memory != null);

    private ShellValue Next() => _arguments[_index++];
    private static bool Is(ShellValue value, ReadOnlySpan<byte> token) => value.Bytes.Span.SequenceEqual(token);
    private bool Consume(ReadOnlySpan<byte> token)
    {
        if (_index >= _end || !Is(_arguments[_index], token))
            return false;
        _index++;
        return true;
    }
    private void Constant(bool value) => AppendPredicate(StepKind.Constant, value ? 1 : 0);

    private void AppendPredicate(StepKind kind, int operand)
    {
        _steps.Add(new(kind, operand));
        _maximumStackDepth = Math.Max(_maximumStackDepth, ++_stackDepth);
    }

    private void AppendBinaryOperator(StepKind kind)
    {
        _steps.Add(new(kind, 0));
        _stackDepth--;
    }
    private void Fail(string message) => Fail(message, ShellValue.Empty, string.Empty);

    private void Fail(ShellValue operand, string suffix) => Fail(string.Empty, operand, suffix);

    private void Fail(string prefix, ShellValue operand, string suffix) => ParseError ??= (prefix, operand, suffix);
    private enum StepKind { Constant, File, VariableIsSet, NamerefIsSet, Not, And, Or }
    private readonly record struct Step(StepKind Kind, int Value);
}
