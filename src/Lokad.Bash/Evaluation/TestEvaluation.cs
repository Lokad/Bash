using System;
using System.Collections.Generic;
using System.Threading;
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

    private TestEvaluation(IReadOnlyList<ShellValue> arguments, CancellationToken cancellationToken, bool errexitEnabled, EvaluationMemory? memory)
    {
        _arguments = arguments;
        _cancellationToken = cancellationToken;
        _errexitEnabled = errexitEnabled;
        _memory = memory;
        _end = arguments.Count;
        ParseArguments();
        if (!HasParseError && _index != _end)
            Fail(null, "too many arguments");
    }

    /// <summary>Gets whether arguments contain a syntax, integer or text-boundary error.</summary>
    public bool HasParseError => ErrorMessage.Length != 0;
    internal ShellValue? ErrorOperand { get; private set; }
    internal string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Encodes text arguments and parses them through the byte-valued test grammar.</summary>
    public static TestEvaluation Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new ShellValue[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
            values[index] = ShellValue.FromText(arguments[index]);
        return new TestEvaluation(values, CancellationToken.None, false, null);
    }

    internal static TestEvaluation ParseValues(IReadOnlyList<ShellValue> arguments, CancellationToken cancellationToken,
        bool errexitEnabled, EvaluationMemory memory) => new(arguments, cancellationToken, errexitEnabled, memory);

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
        if (HasParseError)
            return false;
        var values = new bool[_steps.Count];
        var count = 0;
        foreach (var step in _steps)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            switch (step.Kind)
            {
                case StepKind.Constant: values[count++] = step.Value != 0; break;
                case StepKind.File: values[count++] = fileResults[step.Value]; break;
                case StepKind.Not: values[count - 1] = !values[count - 1]; break;
                case StepKind.And:
                case StepKind.Or:
                    var right = values[--count];
                    values[count - 1] = step.Kind == StepKind.And
                        ? values[count - 1] && right : values[count - 1] || right;
                    break;
            }
        }
        return count == 1 && values[0];
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
                else if (TryGetUnaryPredicate(first.Bytes.Span, out var unary))
                    Unary(unary, Next());
                else
                    Fail(first, "unary operator expected");
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
                goto case 4;
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
            _steps.Add(new(StepKind.Or, 0));
        }
    }

    private void ParseAnd(int depth)
    {
        ParseTerm(depth);
        while (!HasParseError && Consume("-a"u8))
        {
            ParseTerm(depth);
            _steps.Add(new(StepKind.And, 0));
        }
    }

    private void ParseTerm(int depth)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var negated = false;
        while (_index + 1 < _end && Consume("!"u8))
        {
            _cancellationToken.ThrowIfCancellationRequested();
            negated = !negated;
        }
        if (_index >= _end)
        {
            Fail(null, "argument expected");
            return;
        }
        if (Consume("("u8))
        {
            if (depth >= ShellSourceCursor.MaximumNestedExpansionDepth)
                Fail(null, "expression nesting limit exceeded");
            else
            {
                ParseOr(depth + 1);
                if (!Consume(")"u8))
                    Fail(null, "closing parenthesis expected");
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
            if (_index < _end && TryGetUnaryPredicate(first.Bytes.Span, out var unary))
                Unary(unary, Next());
            else
                Constant(!first.IsEmpty);
        }
        if (negated)
            _steps.Add(new(StepKind.Not, 0));
    }

    private void Unary(UnaryPredicate predicate, ShellValue operand)
    {
        if (predicate.IsFilePredicate)
            File(predicate.FileKind, operand, null);
        else if (predicate.Operator == UnaryPredicateKind.OptionEnabled)
            Constant(IsOptionEnabled(operand.Bytes.Span, _errexitEnabled));
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
                Fail(left, "integer expression expected");
            else if (!TryInteger(right, out var r))
                Fail(right, "integer expression expected");
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
            Fail(left, "file operand is not valid UTF-8");
            return;
        }
        string? otherPath = null;
        if (right.HasValue && !right.Value.TryGetUtf8Text(out otherPath))
        {
            Fail(right.Value, "file operand is not valid UTF-8");
            return;
        }
        _steps.Add(new(StepKind.File, _files.Count));
        _files.Add(new(kind, path, otherPath));
    }

    private static bool TryTestBinary(ShellValue token, out BinaryPredicate predicate) =>
        TryGetBinaryPredicate(token.Bytes.Span, out predicate) && predicate.Operator != BinaryPredicateKind.RegexMatch;

    private ShellValue Next() => _arguments[_index++];
    private static bool Is(ShellValue value, ReadOnlySpan<byte> token) => value.Bytes.Span.SequenceEqual(token);
    private bool Consume(ReadOnlySpan<byte> token)
    {
        if (_index >= _end || !Is(_arguments[_index], token))
            return false;
        _index++;
        return true;
    }
    private void Constant(bool value) => _steps.Add(new(StepKind.Constant, value ? 1 : 0));
    private void Fail(ShellValue? operand, string message)
    {
        if (HasParseError)
            return;
        ErrorOperand = operand;
        ErrorMessage = message;
    }
    private enum StepKind { Constant, File, Not, And, Or }
    private readonly record struct Step(StepKind Kind, int Value);
}
