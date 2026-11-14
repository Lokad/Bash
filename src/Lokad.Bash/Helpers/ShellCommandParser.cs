using System;
using System.Collections.Generic;
using System.Linq;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Inspects commands and pipelines for display without evaluation. Flattened stages omit
/// prefix assignments and redirections; they are not executable command serialization.
/// </summary>
public static class ShellCommandParser
{
    /// <summary>
    /// Attempts to parse <paramref name="commandLine"/> as a single simple command
    /// for display, excluding pipes, sub-commands, and command substitutions in words.
    /// </summary>
    /// <param name="commandLine">The raw shell command line to parse.</param>
    /// <returns>
    /// The command name and argument list if the command is a single simple command
    /// with UTF-8 words containing no command substitutions; <c>null</c> otherwise.
    /// </returns>
    public static (string Name, IReadOnlyList<string> Arguments)? TryParse(string commandLine)
    {
        if (!Parser.TryParse(commandLine, out var script, out _))
            return null;

        if (script.Commands.Count != 1)
            return null;

        if (script.Commands[0] is not SimpleCommand simple)
            return null;

        return TryFlattenSimpleCommand(simple, out var result) ? result : null;
    }

    /// <summary>
    /// Attempts to parse <paramref name="commandLine"/> as a single simple command or a plain,
    /// left-associative pipeline of simple commands (segments joined by <c>|</c>), with no
    /// sequences, subshells, negation, or command substitutions in words. For display only.
    /// </summary>
    /// <param name="commandLine">The raw shell command line to parse.</param>
    /// <returns>
    /// The pipeline stages in left-to-right order, each as a command name and unquoted argument
    /// list; <c>null</c> if the command line isn't a single simple command or plain pipeline
    /// of simple commands with UTF-8 arguments.
    /// </returns>
    public static IReadOnlyList<(string Name, IReadOnlyList<string> Arguments)>? TryParsePipeline(string commandLine)
    {
        if (!Parser.TryParse(commandLine, out var script, out _))
            return null;

        if (script.Commands.Count != 1)
            return null;

        var stages = new List<(string Name, IReadOnlyList<string> Arguments)>();
        return TryFlattenPipeline(script.Commands[0], stages) ? stages : null;
    }

    /// <summary>
    /// Parses a chain for display, retaining each pipeline's own source text across
    /// <c>;</c>, newline, <c>&amp;&amp;</c>, and <c>||</c> boundaries. This does not
    /// evaluate conditional operators or imply that every displayed segment executes.
    /// </summary>
    /// <returns>
    /// Ordered segments with flattened simple pipeline stages where possible. Valid
    /// compound commands and substitutions retain their source but have null stages;
    /// headers sharing pending here-document bodies form one unflattened segment.
    /// Malformed or unaccounted-for input returns null for the whole chain.
    /// </returns>
    public static IReadOnlyList<ShellCommandChainSegment>? TryParseChain(string commandLine)
    {
        // Token source ranges survive speculative lookahead and pushback. Reuse the
        // grammar's quote, expansion, comment, group, and heredoc handling instead of
        // maintaining a second character walker or reparsing each sliced pipeline.
        var source = ShellSource.FromText(commandLine);
        var tokenizer = new Tokenizer(source);
        var result = new List<ShellCommandChainSegment>();
        var start = 0;
        var requiresCommand = false;
        var pipelines = 0;
        IReadOnlyList<(string Name, IReadOnlyList<string> Arguments)>? singleStages = null;
        while (true)
        {
            if (!tokenizer.TryPeekToken(out var next, out _))
                return null;
            if (next.Kind == TokenKind.Newline)
            {
                if (pipelines != 0 && !requiresCommand)
                    AddSegment(next.Source.Start.Value);
                tokenizer.NextTokenCore();
                if (pipelines == 0)
                    start = next.Source.End.Value;
                continue;
            }

            if (next.Kind == TokenKind.Semicolon)
                return null;

            if (next.Kind == TokenKind.EndOfInput)
                return requiresCommand ? null : result;

            if (!Parser.TryParsePipeline(tokenizer, out var command, out _) || command is null)
                return null;

            pipelines++;
            if (pipelines == 1)
            {
                var stages = new List<(string Name, IReadOnlyList<string> Arguments)>();
                singleStages = TryFlattenPipeline(command, stages) ? stages : null;
            }

            if (!tokenizer.TryPeekToken(out var boundary, out _))
                return null;
            if (boundary.Kind is not (TokenKind.Newline or TokenKind.Semicolon or TokenKind.ListOperator or TokenKind.EndOfInput))
                return null;

            // Keep the whole source region until its pending bodies have been consumed.
            // Splitting earlier would attach those bodies to an unrelated later command.
            if (tokenizer.HereDocuments is not { HasPending: true })
                AddSegment(boundary.Source.Start.Value);

            if (boundary.Kind == TokenKind.EndOfInput)
                return result;

            tokenizer.NextTokenCore();
            if (pipelines == 0)
                start = boundary.Source.End.Value;
            requiresCommand = boundary.Kind == TokenKind.ListOperator;
        }

        void AddSegment(int end)
        {
            result.Add(new ShellCommandChainSegment(source.Slice(start, end - start).ToUtf8String().Trim(' ', '\t', '\n'),
                pipelines == 1 ? singleStages : null));
            pipelines = 0;
            singleStages = null;
        }
    }

    /// <summary>
    /// Flattens a left-associative <c>|</c>-pipeline of simple commands into ordered stages.
    /// Returns false for anything else (negation, <c>|&amp;</c>, subshells, control structures).
    /// </summary>
    private static bool TryFlattenPipeline(ICommand command, List<(string Name, IReadOnlyList<string> Arguments)> stages)
    {
        // Parsed pipelines have a left spine; display extraction additionally
        // rejects operators/negation that execution supports.
        while (command is PipelineCommand pipeline)
        {
            if (pipeline is not { Operator: PipelineOperator.Pipe, Negated: false }
                || !AppendStage(pipeline.Right))
                return false;
            command = pipeline.Left;
        }

        if (!AppendStage(command))
            return false;
        stages.Reverse();
        return true;

        bool AppendStage(ICommand item)
        {
            if (item is not SimpleCommand simple || !TryFlattenSimpleCommand(simple, out var stage))
                return false;
            stages.Add(stage);
            return true;
        }
    }

    /// <summary>
    /// Flattens a simple command's words to a name and unquoted argument list, ignoring
    /// prefix assignments and redirections. Compound operands retain every item in an
    /// unquoted display form, not syntax intended for execution.
    /// </summary>
    private static bool TryFlattenSimpleCommand(SimpleCommand simple, out (string Name, IReadOnlyList<string> Arguments) result)
    {
        result = default;
        if (simple.Words.Count == 0)
            return false;

        var words = new List<string>(simple.Words.Count);
        foreach (var word in simple.Words)
        {
            if (!TryFlattenWord(word, out var text))
                return false;
            if (simple.DeclarationAssignments.TryGetValue(word, out var assignment)
                && assignment.ArrayValues is { } items)
            {
                var compound = new System.Text.StringBuilder(text).Append('(');
                for (var index = 0; index < items.Count; index++)
                {
                    if (!TryFlattenWord(items[index].Word, out var item))
                        return false;
                    if (index != 0) compound.Append(' ');
                    compound.Append(item);
                }
                text = compound.Append(')').ToString();
            }
            words.Add(text);
        }

        result = (words[0], words.Skip(1).ToList());
        return true;
    }

    /// <summary>
    /// Flattens a word to its unquoted string value.
    /// Returns false for command substitutions, malformed nested expansion or a non-UTF-8 result.
    /// </summary>
    private static bool TryFlattenWord(Word word, out string result)
    {
        var bytes = ByteMemoryBuilder.CreateUnaccounted(0);
        foreach (var part in word.Parts)
        {
            switch (part.Kind)
            {
                case WordPartKind.CommandSubstitution:
                case WordPartKind.CommandSubstitutionQuoted:
                    result = "";
                    return false;
                case WordPartKind.Unquoted:
                case WordPartKind.DoubleQuoted:
                    if (ContainsNestedCommandSubstitutionOrMalformedExpansion(((SourceWordPart)part).Source))
                    {
                        result = "";
                        return false;
                    }

                    break;
            }
            bytes.Append(part.GetUnexpandedValue().Bytes);
        }
        return ShellValue.FromBytes(bytes.ToMemory()).TryGetUtf8Text(out result);

        static bool ContainsNestedCommandSubstitutionOrMalformedExpansion(ShellSource text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (ArithmeticExpansionScanner.HasPrefix(text, index))
                {
                    if (!ArithmeticExpansionScanner.TryScan(
                            text,
                            index,
                            out var endIndex,
                            out _,
                            out var containsCommandSubstitution))
                    {
                        return true;
                    }

                    if (containsCommandSubstitution)
                        return true;

                    index = endIndex;
                    continue;
                }

                if (BracedParameterScanner.HasPrefix(text, index))
                {
                    if (!BracedParameterScanner.TryScan(
                            text,
                            index,
                            out var endIndex,
                            out _,
                            out var containsCommandSubstitution))
                    {
                        return true;
                    }

                    if (containsCommandSubstitution)
                        return true;

                    index = endIndex;
                    continue;
                }

                if (CommandSubstitutionScanner.HasPrefix(text, index))
                    return true;
            }

            return false;
        }
    }
}
