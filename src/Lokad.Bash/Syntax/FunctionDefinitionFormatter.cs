using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Renders retained syntax and its line layout without evaluation.</summary>
internal static class FunctionDefinitionFormatter
{
    private enum Format { Display, Export, BraceSource, CaptureCoordinates }

    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppend(FunctionDefinition definition, ByteMemoryBuilder output) =>
        TryAppend(new Step.Function(definition, 0, Nested: false), output, output.Budget, Format.Display, CancellationToken.None);

    /// <summary>Omits presentation-only tokens, indentation and wrapper groups before child parsing.</summary>
    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppendExport(FunctionDefinition definition, BoundedByteMemoryBuilder output) =>
        TryAppend(new Step.Function(definition, 0, Nested: false), output, output.Budget, Format.Export, CancellationToken.None);

    /// <summary>Renders the word's lexical structure, retaining its quotes and escapes.</summary>
    internal static bool TryAppendWordSource(Word word, IByteMemoryWriter output, IMemoryBudget budget, CancellationToken cancellationToken) =>
        TryAppend(new Step.Word(word), output, budget, Format.BraceSource, cancellationToken);

    /// <summary>Locates commands in Bash's rendered capture body without materializing or parsing it again.</summary>
    internal static void ProjectCaptureLocations(ParsedScript script, IMemoryBudget? budget)
    {
        if (script.Locations is not { } locations)
        {
            script.RenderedLineBreaks = 0;
            return;
        }

        var counter = new LineCounter();
        TryAppend(new Step.Script(script), counter, budget, Format.CaptureCoordinates, CancellationToken.None, locations);
        script.RenderedLineBreaks = counter.Line - 1;
    }

    internal static bool TryAppendSubstitutionSource(
        CommandSubstitutionSyntax substitution, IByteMemoryWriter output, IMemoryBudget? budget, CancellationToken cancellationToken)
    {
        if (substitution is CommandSubstitutionSyntax.Legacy legacy)
            return output.TryAppend(legacy.Spelling.AsSpan());
        if (substitution is CommandSubstitutionSyntax.Parsed
            { Failure: SubstitutionSyntaxFailure.None, PreserveAdjacentOpening: false } parsed)
            return output.TryAppend(StartsWithParenthesis(parsed.Script) ? "$( "u8 : "$("u8)
                && TryAppend(new Step.Script(parsed.Script), output, budget, Format.BraceSource, cancellationToken)
                && output.TryAppend((byte)')');
        return output.TryAppend("$("u8) && output.TryAppend(substitution.Source.AsSpan()) && output.TryAppend((byte)')');

        static bool StartsWithParenthesis(ParsedScript script)
        {
            if (script.Commands.Count == 0)
                return false;
            // A leading literal parenthesis after the opener would select arithmetic.
            var first = script.Commands[0];
            while (true)
            {
                switch (first)
                {
                    case SubshellCommand or ArithmeticCommand: return true;
                    case RedirectedCommand redirected: first = redirected.Body; break;
                    case ListCommand list: first = list.Left; break;
                    case PipelineCommand { Negated: false } pipe: first = pipe.Left; break;
                    default: return false;
                }
            }
        }
    }

    private static bool TryAppend(Step initial, IByteMemoryWriter output, IMemoryBudget? budget, Format format,
        CancellationToken cancellationToken, CommandLocations? locations = null)
    {
        var display = format == Format.Display;
        var braceSource = format == Format.BraceSource;
        var lines = output as LineCounter;
        var capture = lines != null;
        var functionDepth = 0;
        var flushedHereDocuments = false;
        // Explicit work items keep long left-associated lists off the CLR call stack.
        using var workMemory = budget == null ? null : new MemoryCapacity(budget, 0);
        using var documentMemory = budget == null ? null : new MemoryCapacity(budget, 0);
        var work = new Stack<Step>();
        var hereDocuments = new Queue<Redirection>();
        var workCapacity = 0;
        var documentCapacity = 0;
        byte[] encodedFragment = [];
        PrepareWork(braceSource || capture ? 1 : 2);
        if (!braceSource && !capture)
            work.Push(new Step.Layout(0));
        work.Push(initial);
        while (work.TryPop(out var step))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (step)
            {
                case Step.Text(var text):
                    if (!Append(text)) return false;
                    break;
                case Step.Source(var source):
                    if (!output.TryAppend(source.AsSpan())) return false;
                    break;
                case Step.Layout(var indent):
                    if (display || braceSource || capture || hereDocuments.Count != 0)
                        PushStep(new Step.Line(indent, Terminate: false));
                    else if (!Append(" ")) return false;
                    break;
                case Step.Line(var indent, var terminate):
                    if (display && terminate && hereDocuments.Count == 0 && !Append(";")) return false;
                    if (!Append("\n")) return false;
                    if (hereDocuments.Count != 0)
                    {
                        if (!FlushHereDocuments()) return false;
                        if (display && !Append("\n")) return false;
                    }
                    if ((display || braceSource) && !output.TryAppend(indent, (byte)' ')) return false;
                    break;
                case Step.FunctionDepth(var change):
                    functionDepth += change;
                    break;
                case Step.FlushDocuments:
                    if (!FlushCaptureDocuments()) return false;
                    break;
                case Step.Location(var command, var firstElement):
                    if (locations!.TryGet(command, out var location))
                        locations.Update(command, firstElement
                            ? location with { FirstElementEndLine = lines!.Line }
                            : location with { EndLine = lines!.Line });
                    break;
                case Step.Word(var word):
                    var doubleQuoted = false;
                    foreach (var part in word.Parts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var content = part.GetUnexpandedValue().Bytes.Span;
                        var quotedPart = part.IsDoubleQuoted;
                        if (doubleQuoted && quotedPart && part.DoubleQuote != DoubleQuoteBoundary.Continuation
                            && !Append("\"\"")) return false;
                        if (quotedPart != doubleQuoted && !Append("\"")) return false;
                        doubleQuoted = quotedPart;
                        var success = part.Kind switch
                        {
                            WordPartKind.Unquoted => output.TryAppend(content),
                            // ANSI-C quoting keeps embedded apostrophes in one literal part on import.
                            WordPartKind.SingleQuoted when format == Format.Export && content.Contains((byte)'\'') =>
                                ShellArgumentEscaping.TryAppendQuoted(output, content, ShellQuoteStyle.AnsiC),
                            WordPartKind.SingleQuoted => ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, content),
                            WordPartKind.DoubleQuoted => output.TryAppend(content),
                            WordPartKind.Escaped => Append("\\") && output.TryAppend(content),
                            WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted
                                when capture && part is CommandSubstitutionWordPart command => CountCaptureWord(command.Substitution),
                            WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted
                                when part is CommandSubstitutionWordPart command
                                    && (braceSource || command.Substitution is CommandSubstitutionSyntax.Legacy
                                        or CommandSubstitutionSyntax.AdjacentSubshell
                                        or CommandSubstitutionSyntax.Parsed { PreserveAdjacentOpening: true }) =>
                                TryAppendSubstitutionSource(command.Substitution, output, budget, cancellationToken),
                            WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted =>
                                AppendCommandSubstitution(content),
                            _ => throw new InvalidOperationException("A here-document body is not an ordinary word.")
                        };
                        if (!success) return false;
                        if (capture && part is SourceWordPart { RenderedLineAdjustment: var adjustment })
                            lines!.Adjust(adjustment);
                    }
                    if (doubleQuoted && !Append("\"")) return false;
                    break;
                case Step.Script(var script):
                    PrepareWork(Math.Max(0, checked(2 * script.Commands.Count - 1)));
                    for (var index = script.Commands.Count - 1; index >= 0; index--)
                    {
                        work.Push(new Step.Command(script.Commands[index], 0));
                        if (index != 0)
                            work.Push(new Step.CommandSeparator(Newline: false));
                    }
                    break;
                case Step.CommandSeparator(var newline):
                    if (capture)
                    {
                        var afterDocuments = flushedHereDocuments || hereDocuments.Count != 0;
                        if (!FlushCaptureDocuments()) return false;
                        flushedHereDocuments = false;
                        var breaks = functionDepth != 0 ? newline && !afterDocuments ? 2 : 1 : newline ? 1 : 0;
                        if (!output.TryAppend(breaks, (byte)'\n')) return false;
                    }
                    else if (hereDocuments.Count == 0)
                    {
                        if (!Append("; ")) return false;
                    }
                    else if (!Append("\n") || !FlushHereDocuments() || !Append("\n")) return false;
                    break;
                case Step.LogicalSeparator:
                    if (!FlushCaptureDocuments()) return false;
                    break;
                case Step.Pipe(var operation):
                    if (capture)
                    {
                        if (!FlushCaptureDocuments()) return false;
                        break;
                    }
                    if (!Append(operation == PipelineOperator.PipeAndError ? display ? " 2>&1 |" : " |&" : " |")) return false;
                    if (hereDocuments.Count != 0)
                    {
                        if (!Append("\n") || !FlushHereDocuments() || !Append("  ")) return false;
                    }
                    else if (!Append(" ")) return false;
                    break;
                case Step.Assignment(var assignment):
                    if (!output.TryAppend(assignment.Target.AsSpan()) || !Append(assignment.Operator == AssignmentOperator.Append ? "+=" : "=")) return false;
                    if (assignment.ArrayValues is { } values)
                    {
                        PrepareWork(Math.Max(1, checked(2 * values.Count)));
                        work.Push(new Step.Text(")"));
                        for (var index = values.Count - 1; index >= 0; index--)
                        {
                            work.Push(new Step.Word(values[index].Word));
                            if (index != 0) work.Push(new Step.Text(" "));
                        }
                        if (!Append("(")) return false;
                    }
                    else PushStep(new Step.Word(assignment.Value));
                    break;
                case Step.Condition(var expression):
                    // Conditional operators render inline. The operands retain their own quoted and nested lines.
                    switch (expression)
                    {
                        case ConditionalExpression.NonEmpty nonEmpty:
                            PushStep(new Step.Word(nonEmpty.Operand));
                            break;
                        case ConditionalExpression.Unary unary:
                            PushStep(new Step.Word(unary.Operand));
                            break;
                        case ConditionalExpression.VariableElement element:
                            Push(new Step.Word(element.Name), new Step.Word(element.Selector));
                            break;
                        case ConditionalExpression.Binary binary:
                            Push(new Step.Word(binary.Left), new Step.Word(binary.Right));
                            break;
                        case ConditionalExpression.Negated negated:
                            PushStep(new Step.Condition(negated.Inner));
                            break;
                        case ConditionalExpression.Logical logical:
                            Push(new Step.Condition(logical.Left), new Step.Condition(logical.Right));
                            break;
                    }
                    break;
                case Step.Redirections(var redirections, var firstElement):
                    PrepareWork(redirections.Count + (firstElement != null ? 1 : 0));
                    for (var index = redirections.Count - 1; index >= 0; index--)
                    {
                        if (index == 0 && firstElement != null)
                            work.Push(new Step.Location(firstElement, FirstElement: true));
                        work.Push(new Step.Redirection(redirections[index]));
                    }
                    break;
                case Step.Redirection(var redirection):
                    if (!Append(" ")) return false;
                    if (redirection.VariableName.Length != 0)
                    {
                        if (!Append("{") || !Append(redirection.VariableName) || !Append("}")) return false;
                    }
                    else if (redirection.FileDescriptor is { } descriptor)
                    {
                        if (!Append(descriptor.ToString(CultureInfo.InvariantCulture))) return false;
                    }
                    else if (redirection.Kind is RedirectionKind.DuplicateOutput or RedirectionKind.CloseOutput)
                    {
                        if (!Append("1")) return false;
                    }
                    else if (redirection.Kind is RedirectionKind.DuplicateInput or RedirectionKind.CloseInput)
                    {
                        if (!Append("0")) return false;
                    }
                    var redirectionOperator = redirection.Kind switch
                    {
                        RedirectionKind.Input or RedirectionKind.AllocateInput => "< ",
                        RedirectionKind.Output or RedirectionKind.AllocateOutput => "> ",
                        RedirectionKind.OutputAppend => ">> ",
                        RedirectionKind.InputOutput => "<> ",
                        RedirectionKind.ForceOutput => ">| ",
                        RedirectionKind.DuplicateOutput or RedirectionKind.CloseOutput => ">&",
                        RedirectionKind.DuplicateInput or RedirectionKind.CloseInput => "<&",
                        RedirectionKind.OutputAndError => "&> ",
                        RedirectionKind.OutputAndErrorAppend => "&>> ",
                        RedirectionKind.HereString => "<<< ",
                        RedirectionKind.HereDocument => "<<",
                        RedirectionKind.HereDocumentStripTabs => "<<-",
                        _ => throw new ArgumentOutOfRangeException(nameof(redirection.Kind))
                    };
                    if (!Append(redirectionOperator)) return false;
                    if (redirection.HereDocumentDelimiter is { } delimiterWord)
                    {
                        QueueDocument(redirection);
                        PushStep(redirection.DelimiterSyntax is { } syntax
                            ? new Step.Source(syntax.Spelling)
                            : new Step.Word(delimiterWord));
                    }
                    else PushStep(new Step.Word(redirection.Target));
                    break;
                case Step.Function(var function, var indent, var nested):
                    // Keep keyword-only names reparsable, even when Bash's printed assignment-like name is not.
                    if ((display && nested || function.Name.Syntax.HasFlag(FunctionNameSyntax.KeywordOnly)) && !Append("function ")) return false;
                    if (!output.TryAppend(function.Name.Value.Bytes.Span) || !Append(" () ")) return false;
                    var body = !display || function.Body is GroupCommand or RedirectedCommand { Body: GroupCommand }
                        ? function.Body : new GroupCommand(function.Body);
                    if (capture)
                    {
                        // Bash emits an outer group even for a function with a different compound body.
                        var grouped = body is GroupCommand or RedirectedCommand { Body: GroupCommand };
                        Push(new Step.Text(grouped ? "\n" : "\n{\n"), new Step.FunctionDepth(1),
                            new Step.Command(body, indent), new Step.FunctionDepth(-1), new Step.FlushDocuments(),
                            new Step.Text(grouped ? "" : "\n}"));
                    }
                    else Push(new Step.Layout(indent), new Step.Command(body, indent));
                    break;
                case Step.IfArm(var conditional, var arm, var indent):
                    var condition = arm < 0 ? conditional.Condition : conditional.ElifClauses[arm].Condition;
                    var thenBody = arm < 0 ? conditional.ThenBody : conditional.ElifClauses[arm].Body;
                    if (arm < 0 || capture) PushStep(new Step.Text("fi"));
                    if (arm + 1 < conditional.ElifClauses.Count)
                    {
                        // Capture layout nests elif as an if in the else body.
                        if (capture)
                            Push(new Step.Text("else\n"), new Step.IfArm(conditional, arm + 1, indent), new Step.Text("\n"));
                        else PushStep(new Step.IfArm(conditional, arm + 1, indent));
                    }
                    else if (conditional.ElseBody is { } elseBody)
                    {
                        Push(new Step.Text("else"), new Step.Layout(indent + 4),
                            new Step.Command(elseBody, indent + 4), new Step.Line(indent, Terminate: true));
                    }
                    Push(new Step.Text(arm < 0 || capture ? "if " : "elif "), new Step.Command(condition, indent), new Step.Text("; then"),
                        new Step.Layout(indent + 4), new Step.Command(thenBody, indent + 4), new Step.Line(indent, Terminate: true));
                    break;
                case Step.Command(var command, var indent):
                    if (locations?.TryGet(command, out _) == true)
                    {
                        locations.Update(command, new CommandLocation(lines!.Line, lines.Line, lines.Line));
                        PushStep(new Step.Location(command, FirstElement: false));
                    }
                    switch (command)
                    {
                        case SimpleCommand simple:
                            PrepareWork(Math.Max(1, checked(2 * (simple.Words.Count + simple.Assignments.Count))) + (capture ? 1 : 0));
                            work.Push(new Step.Redirections(simple.Redirections,
                                capture && simple.Words.Count == 0 && simple.Assignments.Count == 0 ? simple : null));
                            for (var index = simple.Words.Count - 1; index >= 0; index--)
                            {
                                if (capture && index == 0 && simple.Assignments.Count == 0)
                                    work.Push(new Step.Location(simple, FirstElement: true));
                                work.Push(simple.DeclarationAssignments.TryGetValue(simple.Words[index], out var assignment)
                                    ? new Step.Assignment(assignment) : new Step.Word(simple.Words[index]));
                                if (index != 0 || simple.Assignments.Count != 0) work.Push(new Step.Text(" "));
                            }
                            for (var index = simple.Assignments.Count - 1; index >= 0; index--)
                            {
                                if (capture && index == 0)
                                    work.Push(new Step.Location(simple, FirstElement: true));
                                work.Push(new Step.Assignment(simple.Assignments[index]));
                                if (index != 0) work.Push(new Step.Text(" "));
                            }
                            break;
                        case ListCommand list:
                            if (capture) PushStep(new Step.FlushDocuments());
                            Push(new Step.Command(list.Left, indent), list.Operator == ListOperator.Sequence
                                    ? braceSource || capture ? new Step.CommandSeparator(list.HasNewlineConnector) : new Step.Line(indent, Terminate: true)
                                    : capture ? new Step.LogicalSeparator() : new Step.Text(list.Operator == ListOperator.And ? " && " : " || "),
                                new Step.Command(list.Right, indent));
                            break;
                        case GroupCommand group:
                            if (capture)
                                Push(new Step.Text(functionDepth == 0 ? "{ " : "{\n"), new Step.Command(group.Body, indent + 4),
                                    new Step.FlushDocuments(), new Step.Text(functionDepth == 0 ? "}" : "\n}"));
                            else Push(new Step.Text("{ "), new Step.Layout(indent + 4),
                                    new Step.Command(group.Body, indent + 4), new Step.Line(indent, Terminate: false), new Step.Text("}"));
                            break;
                        case SubshellCommand subshell:
                            if (capture)
                                Push(new Step.Command(subshell.Body, indent), new Step.FlushDocuments());
                            else Push(new Step.Text("( "), new Step.Command(subshell.Body, indent), new Step.Text(" )"));
                            break;
                        case RedirectedCommand redirected:
                            Push(new Step.Command(redirected.Body, indent), new Step.Redirections(redirected.Redirections));
                            break;
                        case NegatedCommand negated:
                            Push(new Step.Text("! "), new Step.Command(negated.Inner, indent));
                            break;
                        case TimedCommand timed:
                            Push(new Step.Text(timed.Portable ? "time -p " : "time "), new Step.Command(timed.Inner, indent));
                            break;
                        case PipelineCommand pipeline:
                            if (pipeline.Negated && !Append("! ")) return false;
                            if (capture) PushStep(new Step.FlushDocuments());
                            Push(new Step.Command(pipeline.Left, indent), new Step.Pipe(pipeline.Operator),
                                new Step.Command(pipeline.Right, indent));
                            break;
                        case IfCommand conditional:
                            PushStep(new Step.IfArm(conditional, -1, indent));
                            break;
                        case WhileCommand loop:
                            ScheduleLoop("while", loop.Condition, loop.Body, indent);
                            break;
                        case UntilCommand loop:
                            ScheduleLoop("until", loop.Condition, loop.Body, indent);
                            break;
                        case ForCommand loop:
                            Push(new Step.Text(";"), new Step.Layout(indent), new Step.Text("do"),
                                new Step.Layout(indent + 4), new Step.Command(loop.Body, indent + 4),
                                new Step.Line(indent, Terminate: true), new Step.Text("done"));
                            PrepareWork(Math.Max(0, checked(2 * loop.Items.Count - 1)));
                            for (var index = loop.Items.Count - 1; index >= 0; index--)
                            {
                                work.Push(new Step.Word(loop.Items[index]));
                                if (index != 0) work.Push(new Step.Text(" "));
                            }
                            if (!Append("for ")
                                || !(loop.InvalidName.IsEmpty ? Append(loop.Name) : output.TryAppend(loop.InvalidName.Bytes.Span))
                                || !Append(" in ")) return false;
                            break;
                        case ArithmeticForCommand loop:
                            loop.Split(out var init, out var loopCondition, out var update);
                            Push(new Step.Text("for (("), new Step.Source(init), new Step.Text("; "),
                                new Step.Source(loopCondition), new Step.Text("; "), new Step.Source(update), new Step.Text("))"),
                                new Step.Line(indent, Terminate: false), new Step.Text("do"), new Step.Layout(indent + 4),
                                new Step.Command(loop.Body, indent + 4),
                                new Step.Line(indent, Terminate: true), new Step.Text("done"));
                            break;
                        case ArithmeticCommand arithmetic:
                            Push(new Step.Text("(("), new Step.Source(arithmetic.Expression), new Step.Text("))"));
                            break;
                        case ConditionalCommand conditional:
                            if (capture) PushStep(new Step.Condition(conditional.Condition));
                            else Push(new Step.Text("[[ "), new Step.Source(conditional.Spelling), new Step.Text(" ]]"));
                            break;
                        case CaseCommand selection:
                            PushStep(new Step.Text("esac"));
                            for (var index = selection.Items.Count - 1; index >= 0; index--)
                            {
                                var item = selection.Items[index];
                                PrepareWork(checked(7 + Math.Max(0, 2 * item.Patterns.Count - 1)));
                                Push(new Step.Text(")"), new Step.Layout(indent + 8), new Step.Command(item.Body, indent + 8),
                                    new Step.Layout(indent + 4), new Step.Text(";;"), new Step.Layout(indent));
                                for (var pattern = item.Patterns.Count - 1; pattern >= 0; pattern--)
                                {
                                    work.Push(new Step.Word(item.Patterns[pattern]));
                                    if (pattern != 0) work.Push(new Step.Text(" | "));
                                }
                                // A bare esac at the start of an arm would close the case.
                                work.Push(new Step.Text(item.Patterns.Count > 0 && Parser.IsKeyword(item.Patterns[0], "esac")
                                    ? "    (" : "    "));
                            }
                            Push(new Step.Text("case "), new Step.Word(selection.Subject), new Step.Text(" in "), new Step.Layout(indent));
                            break;
                        case FunctionDefinition function:
                            PushStep(new Step.Function(function, indent, Nested: true));
                            break;
                        default:
                            throw new NotSupportedException($"Cannot render command kind {command.GetType().Name}.");
                    }
                    break;
            }
        }
        if (capture) return FlushCaptureDocuments();
        return !braceSource || hereDocuments.Count == 0 || Append("\n") && FlushHereDocuments();

        bool Append(string text)
        {
            if (lines != null)
            {
                lines.Adjust(text.AsSpan().Count('\n'));
                return true;
            }

            const int shortFragmentLength = 64;
            var characters = text.AsSpan();
            if (characters.Length > shortFragmentLength)
            {
                var required = Utf8Text.StrictUtf8.GetByteCount(characters);
                if (required > encodedFragment.Length)
                {
                    var previous = encodedFragment.Length;
                    var next = NextCapacity(previous, required);
                    workMemory?.Grow(MemoryCapacity.ArrayBackingBytes<byte>(next));
                    encodedFragment = new byte[next];
                    workMemory?.Resize(workMemory.Bytes - MemoryCapacity.ArrayBackingBytes<byte>(previous));
                }

                Utf8Text.StrictUtf8.GetBytes(characters, encodedFragment);
                return output.TryAppend(encodedFragment.AsSpan(0, required));
            }

            // A UTF-16 code unit needs at most three UTF-8 bytes, including pairs.
            Span<byte> bytes = stackalloc byte[3 * shortFragmentLength];
            var written = Utf8Text.StrictUtf8.GetBytes(characters, bytes);
            return output.TryAppend(bytes[..written]);
        }

        bool AppendCommandSubstitution(ReadOnlySpan<byte> source)
        {
            // Synthetic deferred payloads may start with a subshell or end in a comment without LF.
            var first = ShellSourceCursor.SkipLineContinuations(source, 0);
            return Append(first < source.Length && source[first] == (byte)'(' ? "$( " : "$(")
                && output.TryAppend(source)
                && (source.EndsWith("\n"u8) || Append("\n"))
                && Append(")");
        }

        bool CountCaptureWord(CommandSubstitutionSyntax substitution)
        {
            if (substitution is CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } breaks })
                return output.TryAppend(breaks, (byte)'\n');
            return output.TryAppend(substitution.Source.AsSpan());
        }

        bool FlushCaptureDocuments()
        {
            if (hereDocuments.Count == 0) return true;
            flushedHereDocuments = true;
            return Append("\n") && FlushHereDocuments();
        }

        bool FlushHereDocuments()
        {
            while (hereDocuments.TryDequeue(out var redirection))
            {
                foreach (var part in redirection.Target.Parts)
                    if (!output.TryAppend(part.GetUnexpandedValue().Bytes.Span)) return false;
                var delimiter = redirection.HereDocumentDelimiter
                    ?? throw new InvalidOperationException("A here-document requires its delimiter syntax.");
                var literal = redirection.DelimiterSyntax?.Value ?? HereDocumentReader.GetHereDocumentDelimiter(delimiter, out _);
                if (!output.TryAppend(literal.AsSpan()) || !Append("\n")) return false;
            }
            return true;
        }

        void Push(params Step[] steps)
        {
            PrepareWork(steps.Length);
            for (var index = steps.Length - 1; index >= 0; index--) work.Push(steps[index]);
        }

        void PushStep(Step step)
        {
            PrepareWork(1);
            work.Push(step);
        }

        void PrepareWork(int additional)
        {
            var required = checked(work.Count + additional);
            if (required <= workCapacity)
                return;

            // Each slot can retain a work record as well as its reference. Only
            // the backing arrays overlap during growth; they share the records.
            const long workItemBytes = 32;
            var next = NextCapacity(workCapacity, required);
            workMemory?.Grow(MemoryCapacity.ArrayBackingBytes<Step>(next) + workItemBytes * (next - workCapacity));
            work.EnsureCapacity(next);
            workMemory?.Resize(workMemory.Bytes - MemoryCapacity.ArrayBackingBytes<Step>(workCapacity));
            workCapacity = next;
        }

        void QueueDocument(Redirection redirection)
        {
            if (hereDocuments.Count == documentCapacity)
            {
                var next = NextCapacity(documentCapacity, checked(hereDocuments.Count + 1));
                documentMemory?.Grow(MemoryCapacity.ArrayBackingBytes<Redirection>(next));
                hereDocuments.EnsureCapacity(next);
                documentMemory?.Resize(documentMemory.Bytes - MemoryCapacity.ArrayBackingBytes<Redirection>(documentCapacity));
                documentCapacity = next;
            }
            hereDocuments.Enqueue(redirection);
        }

        static int NextCapacity(int previous, int required) =>
            (int)Math.Max(required, Math.Min(Array.MaxLength, Math.Max(4L, 2L * previous)));

        void ScheduleLoop(string keyword, ICommand condition, ICommand body, int indent) =>
            Push(new Step.Text(keyword + " "), new Step.Command(condition, indent), new Step.Text("; do"),
                new Step.Layout(indent + 4), new Step.Command(body, indent + 4),
                new Step.Line(indent, Terminate: true), new Step.Text("done"));
    }

    private sealed class LineCounter : IByteMemoryWriter
    {
        internal int Line { get; private set; } = 1;

        public bool TryAppend(ReadOnlySpan<byte> bytes)
        {
            Line += bytes.Count((byte)'\n');
            return true;
        }

        public bool TryAppend(byte value) => TryAppend(1, value);

        public bool TryAppend(int count, byte value)
        {
            if (value == (byte)'\n') Line += count;
            return true;
        }

        internal void Adjust(int lineBreaks) => Line += lineBreaks;
    }

    /// <summary>Deferred syntax output; line steps also flush the current command line's here-documents.</summary>
    private abstract record Step
    {
        public sealed record Text(string Value) : Step;
        public sealed record Source(ShellSource Value) : Step;
        public sealed record Layout(int Indent) : Step;
        public sealed record Line(int Indent, bool Terminate) : Step;
        public sealed record CommandSeparator(bool Newline) : Step;
        public sealed record LogicalSeparator : Step;
        public sealed record FlushDocuments : Step;
        public sealed record FunctionDepth(int Change) : Step;
        public sealed record Location(ICommand Node, bool FirstElement) : Step;
        public sealed record Script(ParsedScript Value) : Step;
        public sealed record Pipe(PipelineOperator Operator) : Step;
        public sealed record Word(Syntax.Word Value) : Step;
        public sealed record Condition(ConditionalExpression Value) : Step;
        public sealed record Assignment(AssignmentWord Value) : Step;
        public sealed record Redirection(Syntax.Redirection Value) : Step;
        public sealed record Redirections(IReadOnlyList<Syntax.Redirection> Values, SimpleCommand? FirstElement = null) : Step;
        public sealed record Function(FunctionDefinition Value, int Indent, bool Nested) : Step;
        public sealed record IfArm(IfCommand Value, int Arm, int Indent) : Step;
        public sealed record Command(ICommand Value, int Indent) : Step;
    }
}
