using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Prepares the active ignore list once for the candidate paths in an expansion.</summary>
internal sealed class GlobIgnorePatterns(ShellValue value, bool ignoreAsciiCase, EvaluationMemory memory) : IDisposable
{
    private static readonly GlobMask AllActive = new(ReadOnlyMemory<byte>.Empty);
    private MemoryCapacity? _owner;
    private List<TextGlobMatcher.Utf8PathMatcher>? _patterns;
    private ByteMemoryBuilder? _longPath;
    internal IReadOnlyList<Range>? PreparedRanges { get; init; }

    internal static List<Range>? PrepareQuotedCommandRanges(ShellValue value, EvaluationMemory memory)
    {
        var bytes = value.Bytes.Span;
        if (!bytes.Contains((byte)'"') || bytes.IndexOf("$("u8) < 0)
            return null;

        // Retain only the pattern boundaries. The command reader's scratch ends at each delimiter.
        memory.Charge(32);
        List<Range> ranges = [];
        using var groups = new PatternListGroups(stackalloc byte[32], value.Bytes, memory.Budget, memory.ActiveCommand?.Scope);
        var cursor = 0;
        while (groups.TryReadPattern(ref cursor, memory.CancellationToken, out var range))
        {
            memory.PrepareListCapacity(ranges, 1);
            ranges.Add(range);
        }
        return ranges;
    }

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
        if (PreparedRanges is { } ranges)
        {
            foreach (var range in ranges)
                AddPattern(range);
        }
        else
        {
            using var groups = new PatternListGroups(stackalloc byte[32], value.Bytes, memory.Budget, diagnosticScope: null);
            var cursor = 0;
            while (groups.TryReadPattern(ref cursor, cancellationToken, out var range))
                AddPattern(range);
        }
        return patterns;

        void AddPattern(Range range)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AllActive.TryUnescape(ShellValue.FromBytes(value.Bytes[range]), memory, owner,
                    cancellationToken, out var pattern, out var mask))
            {
                memory.PrepareListCapacity(owner, patterns, 1);
                patterns.Add(new TextGlobMatcher.Utf8PathMatcher(pattern.Bytes, mask, owner, ignoreAsciiCase));
            }
        }
    }

    /// <summary>Locates list separators in the stored pattern spelling without interpreting the groups.</summary>
    private ref struct PatternListGroups(Span<byte> inline, ReadOnlyMemory<byte> spelling, IMemoryBudget budget,
        ScopedEvaluation? diagnosticScope)
    {
        private readonly Span<byte> _inline = inline;
        private ByteMemoryBuilder? _overflow;
        private int _inlineCount;
        private Group _group;

        private enum Group : byte
        {
            None, SingleQuote = (byte)'\'', DoubleQuote = (byte)'"', Backquote = (byte)'`',
            Bracket = (byte)']', Parentheses = (byte)')', ParameterName = (byte)'}',
            ParameterWord = 1, NestedCommand = 2
        }

        public bool TryReadPattern(ref int cursor, CancellationToken cancellationToken, out Range range)
        {
            var start = cursor;
            var end = spelling.Length;
            for (var index = start; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Consumes(ref index, cancellationToken))
                    continue;
                cursor = index + 1;
                if (index > start)
                {
                    range = start..index;
                    return true;
                }
                start = cursor;
            }
            cursor = end;
            range = start..end;
            return start < end;
        }

        private bool Consumes(ref int index, CancellationToken cancellationToken)
        {
            var bytes = spelling.Span;
            var value = bytes[index];
            var group = _group;
            if (group == Group.SingleQuote)
            {
                if (value == (byte)'\'')
                    Pop();
                return true;
            }

            var following = index < bytes.Length - 1 ? bytes[index + 1] : (byte)0;
            if (value == (byte)'\\' && index < bytes.Length - 1)
            {
                index++;
                return true;
            }
            if (ClosesGroup(group, value))
            {
                Pop();
                return true;
            }
            if (group == Group.Backquote)
                return true;

            switch (value)
            {
                case (byte)':' when group == Group.None:
                    return false;
                case (byte)'"' or (byte)'`':
                case (byte)'\'' when group != Group.DoubleQuote:
                    Push((Group)value);
                    break;
                case (byte)'[' when group is Group.None or Group.Bracket or Group.ParameterName:
                    Push(Group.Bracket);
                    break;
                case (byte)'(' when group is Group.Parentheses or Group.NestedCommand:
                    Push(group);
                    break;
                case (byte)'$' when group == Group.DoubleQuote && following == (byte)'(':
                    // In this position Bash uses the command reader to locate the closing delimiter.
                    index = ReadQuotedCommand(index, cancellationToken) - 1;
                    break;
                case (byte)'$' when group != Group.Bracket && following == (byte)'(':
                    Push(group == Group.None ? Group.Parentheses : Group.NestedCommand);
                    index++;
                    break;
                case (byte)'$' when group is not (Group.Bracket or Group.Parentheses) && following == (byte)'{':
                    Push(Group.ParameterName);
                    index++;
                    break;
                case (byte)'<' or (byte)'>' when (group is Group.None or Group.ParameterName or Group.ParameterWord)
                    && following == (byte)'(':
                    Push(Group.Parentheses);
                    index++;
                    break;
                case (byte)'#' when (group is Group.Parentheses or Group.NestedCommand)
                    && bytes[index - 1] is (byte)' ' or (byte)'\t' or (byte)'\n':
                    // A comment inside a parenthesized shell spelling ends at physical LF.
                    while (index < bytes.Length - 1 && bytes[index + 1] != (byte)'\n')
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        index++;
                    }
                    break;
                case (byte)'#' or (byte)'%' or (byte)'^' or (byte)',' or (byte)'~' or (byte)':' or (byte)'-'
                    or (byte)'=' or (byte)'?' or (byte)'+' or (byte)'/' when group == Group.ParameterName:
                    // A bracket in the parameter name groups a subscript. An operator starts its word.
                    _group = Group.ParameterWord;
                    break;
            }
            return true;

            static bool ClosesGroup(Group group, byte value) => group switch
            {
                Group.None => false,
                Group.ParameterWord => value == (byte)'}',
                Group.NestedCommand => value == (byte)')',
                _ => value == (byte)group
            };
        }

        private int ReadQuotedCommand(int start, CancellationToken cancellationToken)
        {
            using var scratch = new MemoryCapacity(budget, 0);
            var source = ShellSource.FromBytes(spelling);
            var cursor = new ShellSourceCursor(source, start, new ShellScanContext
            {
                ScriptInput = source,
                SyntaxMemory = scratch,
                LegacyOrigin = LegacySubstitutionOrigin.Script
            });
            var scan = cursor.SkipNestedExpansion(BracedParameterRecognition.All);
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnosticScope is { } scope)
            {
                var failure = scan switch
                {
                    ShellNestedExpansionScan.Invalid invalid => invalid.Failure,
                    ShellNestedExpansionScan.Incomplete incomplete => incomplete.Failure,
                    _ => null
                };
                if (failure != null)
                    scope.Effects.RecordGlobIgnoreDiagnostic(source, failure, scope);
            }
            return scan is ShellNestedExpansionScan.Complete ? cursor.Position : source.Length;
        }

        private void Push(Group delimiter)
        {
            if (_group != Group.None)
            {
                if (_inlineCount < _inline.Length)
                    _inline[_inlineCount++] = (byte)_group;
                else
                    (_overflow ??= new ByteMemoryBuilder(0, budget)).Append((byte)_group);
            }
            _group = delimiter;
        }

        private void Pop()
        {
            if (_overflow is { Length: > 0 } overflow)
            {
                _group = (Group)overflow.ToMemory().Span[^1];
                overflow.Truncate(overflow.Length - 1);
            }
            else
                _group = _inlineCount == 0 ? Group.None : (Group)_inline[--_inlineCount];
        }

        public void Dispose() => _overflow?.Dispose();
    }

    public void Dispose()
    {
        _longPath?.Dispose();
        _owner?.Dispose();
    }
}
