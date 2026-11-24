using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

internal readonly record struct HereDocumentWarning(int Line, ShellSource Delimiter)
{
    internal int ReaderLine { get; init; }
    internal int PendingCaptureDocuments { get; init; }
}

/// <summary>
/// Collects bodies in lexical order at a physical newline, independently of command boundaries.
/// Replaces provisional redirections before publishing the AST; constructor inputs stay untouched.
/// </summary>
internal sealed class HereDocumentReader
{
    private readonly Queue<Redirection> _pending = new();
    private readonly Dictionary<Redirection, Redirection> _completed = new();
    private List<HereDocumentWarning>? _warnings;

    internal bool HasPending => _pending.Count != 0;
    internal int PendingCount => _pending.Count;
    internal IReadOnlyList<HereDocumentWarning>? Warnings => _warnings;
    internal IReadOnlyList<HereDocumentWarning>? TakeWarnings()
    {
        var warnings = _warnings;
        _warnings = null;
        return warnings;
    }

    internal void Enqueue(Redirection redirection) => _pending.Enqueue(redirection);

    internal bool TryReadBodies(Tokenizer tokenizer)
    {
        var eofLineAdvance = tokenizer.HereDocumentHeaderLineAdvance;
        while (_pending.TryDequeue(out var redirection))
        {
            bool allowExpansion;
            ShellSource delimiter;
            if (redirection.DelimiterSyntax is { } syntax)
            {
                allowExpansion = syntax.AllowsExpansion;
                delimiter = syntax.Value;
            }
            else delimiter = GetHereDocumentDelimiter(redirection.Target, tokenizer.ScanContext.SyntaxMemory, out allowExpansion);
            var bodyStart = tokenizer.Position;
            var documentLineAdvance = eofLineAdvance;
            var complete = tokenizer.TryReadHereDocument(delimiter, redirection.Kind == RedirectionKind.HereDocumentStripTabs,
                joinContinuations: allowExpansion, out var content, out var continuedFinalLine);
            if (continuedFinalLine)
                eofLineAdvance = 1;
            if (!complete)
            {
                if (!tokenizer.AllowEofHereDocuments)
                    return false;

                var memory = tokenizer.ScanContext.SyntaxMemory;
                if (_warnings == null)
                {
                    memory?.Grow(32);
                    _warnings = [];
                }
                memory?.PrepareListCapacity(_warnings, 1);
                // The reader starts after the header LF or the previous delimiter line.
                var documentLine = tokenizer.GetHereDocumentWarningLine(Math.Max(0, bodyStart - 1)) + documentLineAdvance;
                var readerLine = tokenizer.GetHereDocumentWarningLine(Math.Max(0, tokenizer.Position - 1)) + eofLineAdvance;
                _warnings.Add(new HereDocumentWarning(documentLine, delimiter) { ReaderLine = readerLine });
            }
            var kind = allowExpansion ? WordPartKind.HereDocument : WordPartKind.SingleQuoted;
            _completed.Add(redirection, new Redirection(redirection.Kind, redirection.FileDescriptor,
                new Word([WordPart.FromSource(kind, content, tokenizer.ScanContext.SyntaxMemory)]), redirection.VariableName, redirection.Target)
            {
                DelimiterSyntax = redirection.DelimiterSyntax
            });
        }
        return true;
    }

    /// <summary>Shares literal delimiter bytes and expansion policy with definition output.</summary>
    internal static ShellSource GetHereDocumentDelimiter(Word word, out bool allowExpansion) =>
        GetHereDocumentDelimiter(word, null, out allowExpansion);

    private static ShellSource GetHereDocumentDelimiter(Word word, MemoryCapacity? memory, out bool allowExpansion)
    {
        allowExpansion = true;
        var builder = memory is null ? ByteMemoryBuilder.CreateUnaccounted(0) : new ByteMemoryBuilder(0, memory);
        foreach (var part in word.Parts)
        {
            if (part.Kind != WordPartKind.Unquoted)
                allowExpansion = false;

            builder.Append(part.GetUnexpandedValue().Bytes);
        }

        return ShellSource.FromBytes(builder.ToMemory());
    }

    /// <summary>Rebuilds only changed nodes, without recursive traversal of left-associated command lists.</summary>
    internal ICommand Complete(ICommand root, CommandLocations? locations, IMemoryBudget? budget)
    {
        if (_completed.Count == 0) return root;

        using var scratch = budget == null ? null : new MemoryCapacity(budget, 0);
        var replacements = new Dictionary<ICommand, ICommand>(ReferenceEqualityComparer.Instance);
        var work = new List<(ICommand Command, bool Visited)>();
        Push(root, false);
        while (work.Count != 0)
        {
            var (command, visited) = work[^1];
            work.RemoveAt(work.Count - 1);
            if (!visited)
            {
                Push(command, true);
                switch (command)
                {
                    case ListCommand list: Visit(list.Left); Visit(list.Right); break;
                    case PipelineCommand pipe: Visit(pipe.Left); Visit(pipe.Right); break;
                    case GroupCommand group: Visit(group.Body); break;
                    case SubshellCommand subshell: Visit(subshell.Body); break;
                    case RedirectedCommand redirected: Visit(redirected.Body); break;
                    case NegatedCommand negated: Visit(negated.Inner); break;
                    case TimedCommand timed: Visit(timed.Inner); break;
                    case FunctionDefinition function: Visit(function.Body); break;
                    case ForCommand loop: Visit(loop.Body); break;
                    case ArithmeticForCommand loop: Visit(loop.Body); break;
                    case WhileCommand loop: Visit(loop.Condition); Visit(loop.Body); break;
                    case UntilCommand loop: Visit(loop.Condition); Visit(loop.Body); break;
                    case IfCommand conditional:
                        Visit(conditional.Condition); Visit(conditional.ThenBody);
                        foreach (var clause in conditional.ElifClauses) { Visit(clause.Condition); Visit(clause.Body); }
                        if (conditional.ElseBody is { } elseBody) Visit(elseBody);
                        break;
                    case CaseCommand selection:
                        foreach (var item in selection.Items) Visit(item.Body);
                        break;
                }
                continue;
            }

            var result = command switch
            {
                SimpleCommand simple when ReplaceRedirections(simple.Redirections) is { } redirections => simple with { Redirections = redirections },
                ListCommand list when TryReplacePair(list.Left, list.Right, out var left, out var right) =>
                    list with { Left = left, Right = right },
                PipelineCommand pipe when TryReplacePair(pipe.Left, pipe.Right, out var left, out var right) =>
                    pipe with { Left = left, Right = right },
                GroupCommand group when replacements.TryGetValue(group.Body, out var body) => group with { Body = body },
                SubshellCommand subshell when replacements.TryGetValue(subshell.Body, out var body) => subshell with { Body = body },
                RedirectedCommand redirected => ReplaceRedirected(redirected),
                NegatedCommand negated when replacements.TryGetValue(negated.Inner, out var inner) => negated with { Inner = inner },
                TimedCommand timed when replacements.TryGetValue(timed.Inner, out var inner) => timed with { Inner = inner },
                FunctionDefinition function when replacements.TryGetValue(function.Body, out var body) => function with { Body = body },
                ForCommand loop when replacements.TryGetValue(loop.Body, out var body) => loop with { Body = body },
                ArithmeticForCommand loop when replacements.TryGetValue(loop.Body, out var body) => loop with { Body = body },
                WhileCommand loop when TryReplacePair(loop.Condition, loop.Body, out var condition, out var body) =>
                    loop with { Condition = condition, Body = body },
                UntilCommand loop when TryReplacePair(loop.Condition, loop.Body, out var condition, out var body) =>
                    loop with { Condition = condition, Body = body },
                IfCommand conditional => ReplaceIf(conditional),
                CaseCommand selection when ReplaceItems(selection.Items, replacements, static (item, commands) =>
                    commands.TryGetValue(item.Body, out var body)
                    ? new CaseItem(item.Patterns, body) { Continuation = item.Continuation }
                    : item) is { } items => selection with { Items = items },
                _ => command
            };
            if (!ReferenceEquals(result, command))
            {
                // Two references, hash and chain fields, bucket storage and alignment.
                scratch?.PrepareDictionaryCapacity(replacements, bytesPerEntry: 32);
                replacements.Add(command, result);
            }
        }
        // Publish location changes once all scratch growth has succeeded.
        if (locations != null)
            foreach (var replacement in replacements)
                locations.Replace(replacement.Key, replacement.Value);
        return Get(root);

        void Visit(ICommand command) => Push(command, false);
        void Push(ICommand command, bool visited)
        {
            scratch?.PrepareListCapacity(work, 1);
            work.Add((command, visited));
        }
        ICommand Get(ICommand command) => replacements.TryGetValue(command, out var replacement) ? replacement : command;

        bool TryReplacePair(ICommand first, ICommand second, out ICommand newFirst, out ICommand newSecond)
        {
            newFirst = Get(first);
            newSecond = Get(second);
            return !ReferenceEquals(first, newFirst) || !ReferenceEquals(second, newSecond);
        }

        IReadOnlyList<Redirection>? ReplaceRedirections(IReadOnlyList<Redirection> redirections) =>
            ReplaceItems(redirections, _completed, static (redirection, completed) =>
                completed.TryGetValue(redirection, out var replacement) ? replacement : redirection);

        ICommand ReplaceRedirected(RedirectedCommand command)
        {
            var redirections = ReplaceRedirections(command.Redirections);
            var body = Get(command.Body);
            return redirections != null || !ReferenceEquals(command.Body, body)
                ? command with { Body = body, Redirections = redirections ?? command.Redirections }
                : command;
        }

        ICommand ReplaceIf(IfCommand command)
        {
            var clauses = ReplaceItems(command.ElifClauses, replacements, static (clause, commands) =>
            {
                var conditionChanged = commands.TryGetValue(clause.Condition, out var condition);
                var bodyChanged = commands.TryGetValue(clause.Body, out var body);
                return conditionChanged || bodyChanged
                    ? new ElifClause(condition ?? clause.Condition, body ?? clause.Body)
                    : clause;
            });
            var condition = Get(command.Condition);
            var thenBody = Get(command.ThenBody);
            var elseBody = command.ElseBody is { } body ? Get(body) : null;
            return clauses != null || !ReferenceEquals(command.Condition, condition)
                || !ReferenceEquals(command.ThenBody, thenBody) || !ReferenceEquals(command.ElseBody, elseBody)
                ? command with { Condition = condition, ThenBody = thenBody,
                    ElifClauses = clauses ?? command.ElifClauses, ElseBody = elseBody }
                : command;
        }

        static IReadOnlyList<T>? ReplaceItems<T, TState>(IReadOnlyList<T> items, TState state, Func<T, TState, T> replace)
            where T : class
        {
            List<T>? result = null;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var replacement = replace(item, state);
                if (!ReferenceEquals(replacement, item))
                {
                    if (result == null)
                    {
                        result = new List<T>(items.Count);
                        for (var previous = 0; previous < index; previous++) result.Add(items[previous]);
                    }
                    result.Add(replacement);
                }
                else result?.Add(item);
            }
            return result;
        }
    }
}
