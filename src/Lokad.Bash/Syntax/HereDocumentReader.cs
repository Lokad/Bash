using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Collects bodies in lexical order at a physical newline, independently of command boundaries.
/// Replaces provisional redirections before publishing the AST; constructor inputs stay untouched.
/// </summary>
internal sealed class HereDocumentReader
{
    private readonly Queue<Redirection> _pending = new();
    private readonly Dictionary<Redirection, Redirection> _completed = new();

    internal bool HasPending => _pending.Count != 0;

    internal void Enqueue(Redirection redirection) => _pending.Enqueue(redirection);

    internal bool TryReadBodies(Tokenizer tokenizer)
    {
        while (_pending.TryDequeue(out var redirection))
        {
            var delimiter = GetHereDocumentDelimiter(redirection.Target, out var allowExpansion);
            if (!tokenizer.TryReadHereDocument(delimiter, redirection.Kind == RedirectionKind.HereDocumentStripTabs, out var content))
                return false;
            var kind = allowExpansion ? WordPartKind.HereDocument : WordPartKind.SingleQuoted;
            _completed.Add(redirection, new Redirection(redirection.Kind, redirection.FileDescriptor,
                new Word([WordPart.FromSource(kind, content)]), redirection.VariableName, redirection.Target));
        }
        return true;
    }

    /// <summary>Shares literal delimiter bytes and expansion policy with definition output.</summary>
    internal static ShellSource GetHereDocumentDelimiter(Word word, out bool allowExpansion)
    {
        allowExpansion = true;
        var builder = ByteMemoryBuilder.CreateUnaccounted(0);
        foreach (var part in word.Parts)
        {
            if (part.Kind != WordPartKind.Unquoted)
                allowExpansion = false;

            builder.Append(part.GetUnexpandedValue().Bytes);
        }

        return ShellSource.FromBytes(builder.ToMemory());
    }

    /// <summary>Rebuilds only changed nodes, without recursive traversal of left-associated command lists.</summary>
    internal ICommand Complete(ICommand root)
    {
        if (_completed.Count == 0) return root;

        var replacements = new Dictionary<ICommand, ICommand>(ReferenceEqualityComparer.Instance);
        var work = new Stack<(ICommand Command, bool Visited)>();
        work.Push((root, false));
        while (work.TryPop(out var step))
        {
            var command = step.Command;
            if (!step.Visited)
            {
                work.Push((command, true));
                switch (command)
                {
                    case ListCommand list: Visit(list.Left); Visit(list.Right); break;
                    case PipelineCommand pipe: Visit(pipe.Left); Visit(pipe.Right); break;
                    case GroupCommand group: Visit(group.Body); break;
                    case SubshellCommand subshell: Visit(subshell.Body); break;
                    case RedirectedCommand redirected: Visit(redirected.Body); break;
                    case NegatedCommand negated: Visit(negated.Inner); break;
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
                SimpleCommand simple when ReplaceItems(simple.Redirections, ReplaceRedirection) is { } redirections => simple with { Redirections = redirections },
                ListCommand list when Changed(list.Left) || Changed(list.Right) => list with { Left = Get(list.Left), Right = Get(list.Right) },
                PipelineCommand pipe when Changed(pipe.Left) || Changed(pipe.Right) => pipe with { Left = Get(pipe.Left), Right = Get(pipe.Right) },
                GroupCommand group when Changed(group.Body) => group with { Body = Get(group.Body) },
                SubshellCommand subshell when Changed(subshell.Body) => subshell with { Body = Get(subshell.Body) },
                RedirectedCommand redirected => ReplaceRedirected(redirected),
                NegatedCommand negated when Changed(negated.Inner) => negated with { Inner = Get(negated.Inner) },
                FunctionDefinition function when Changed(function.Body) => function with { Body = Get(function.Body) },
                ForCommand loop when Changed(loop.Body) => loop with { Body = Get(loop.Body) },
                ArithmeticForCommand loop when Changed(loop.Body) => loop with { Body = Get(loop.Body) },
                WhileCommand loop when Changed(loop.Condition) || Changed(loop.Body) => loop with { Condition = Get(loop.Condition), Body = Get(loop.Body) },
                UntilCommand loop when Changed(loop.Condition) || Changed(loop.Body) => loop with { Condition = Get(loop.Condition), Body = Get(loop.Body) },
                IfCommand conditional => ReplaceIf(conditional),
                CaseCommand selection when ReplaceItems(selection.Items, item => Changed(item.Body)
                    ? new CaseItem(item.Patterns, Get(item.Body)) : item) is { } items => selection with { Items = items },
                _ => command
            };
            if (!ReferenceEquals(result, command)) replacements.Add(command, result);
        }
        return Get(root);

        void Visit(ICommand command) => work.Push((command, false));
        bool Changed(ICommand command) => replacements.ContainsKey(command);
        ICommand Get(ICommand command) => replacements.TryGetValue(command, out var replacement) ? replacement : command;

        Redirection ReplaceRedirection(Redirection redirection) =>
            _completed.TryGetValue(redirection, out var replacement) ? replacement : redirection;

        ICommand ReplaceRedirected(RedirectedCommand command)
        {
            var redirections = ReplaceItems(command.Redirections, ReplaceRedirection);
            return redirections != null || Changed(command.Body)
                ? command with { Body = Get(command.Body), Redirections = redirections ?? command.Redirections }
                : command;
        }

        ICommand ReplaceIf(IfCommand command)
        {
            var clauses = ReplaceItems(command.ElifClauses, clause => Changed(clause.Condition) || Changed(clause.Body)
                ? new ElifClause(Get(clause.Condition), Get(clause.Body)) : clause);
            return clauses != null || Changed(command.Condition) || Changed(command.ThenBody)
                || command.ElseBody is { } elseBody && Changed(elseBody)
                ? command with { Condition = Get(command.Condition), ThenBody = Get(command.ThenBody),
                    ElifClauses = clauses ?? command.ElifClauses, ElseBody = command.ElseBody is { } body ? Get(body) : null }
                : command;
        }

        static IReadOnlyList<T>? ReplaceItems<T>(IReadOnlyList<T> items, Func<T, T> replace) where T : class
        {
            List<T>? result = null;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var replacement = replace(item);
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
