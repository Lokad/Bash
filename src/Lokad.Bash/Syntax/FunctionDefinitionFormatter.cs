using System;
using System.Collections.Generic;
using System.Globalization;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Syntax;

/// <summary>Renders retained syntax for display or explicitly bounded child-parser input, without evaluation.</summary>
internal static class FunctionDefinitionFormatter
{
    private enum Format { Display, Export }

    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppend(FunctionDefinition definition, ByteMemoryBuilder output) =>
        TryAppend(definition, output, Format.Display);

    /// <summary>Omits presentation-only tokens, indentation and wrapper groups before child parsing.</summary>
    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppendExport(FunctionDefinition definition, BoundedByteMemoryBuilder output) =>
        TryAppend(definition, output, Format.Export);

    private static bool TryAppend(FunctionDefinition definition, IByteMemoryWriter output, Format format)
    {
        var display = format == Format.Display;
        // Explicit work items keep long left-associated lists off the CLR call stack.
        var work = new Stack<Step>();
        var hereDocuments = new Queue<Redirection>();
        Push(new Step.Function(definition, 0, Nested: false), new Step.Layout(0));
        while (work.TryPop(out var step))
        {
            switch (step)
            {
                case Step.Text(var text):
                    if (!Append(text)) return false;
                    break;
                case Step.Source(var source):
                    if (!output.TryAppend(source.AsSpan())) return false;
                    break;
                case Step.Layout(var indent):
                    if (display || hereDocuments.Count != 0)
                        work.Push(new Step.Line(indent, Terminate: false));
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
                    if (display && !output.TryAppend(indent, (byte)' ')) return false;
                    break;
                case Step.Word(var word):
                    var doubleQuoted = false;
                    var previousKind = WordPartKind.Unquoted;
                    foreach (var part in word.Parts)
                    {
                        var content = part.GetUnexpandedValue().Bytes.Span;
                        var quotedPart = part.Kind is WordPartKind.DoubleQuoted or WordPartKind.CommandSubstitutionQuoted
                            || doubleQuoted && part.Kind == WordPartKind.Escaped && content is [(byte)'$' or (byte)'`' or (byte)'"' or (byte)'\\'];
                        // Joining source parts can extend $name or turn a literal dollar into expansion syntax.
                        if (doubleQuoted && previousKind == WordPartKind.DoubleQuoted
                            && part.Kind is WordPartKind.DoubleQuoted or WordPartKind.CommandSubstitutionQuoted
                            && !Append("\"\"")) return false;
                        if (quotedPart != doubleQuoted && !Append("\"")) return false;
                        doubleQuoted = quotedPart;
                        var success = part.Kind switch
                        {
                            WordPartKind.Unquoted => output.TryAppend(content),
                            // ANSI-C quoting keeps embedded apostrophes in one literal part on import.
                            WordPartKind.SingleQuoted when !display && content.Contains((byte)'\'') =>
                                ShellArgumentEscaping.TryAppendQuoted(output, content, ShellQuoteStyle.AnsiC),
                            WordPartKind.SingleQuoted => ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, content),
                            WordPartKind.DoubleQuoted => output.TryAppend(content),
                            WordPartKind.Escaped => Append("\\") && output.TryAppend(content),
                            WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted =>
                                AppendCommandSubstitution(content),
                            _ => throw new InvalidOperationException("A here-document body is not an ordinary word.")
                        };
                        if (!success) return false;
                        previousKind = part.Kind;
                    }
                    if (doubleQuoted && !Append("\"")) return false;
                    break;
                case Step.Pipe(var operation):
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
                        work.Push(new Step.Text(")"));
                        for (var index = values.Count - 1; index >= 0; index--)
                        {
                            work.Push(new Step.Word(values[index].Word));
                            if (index != 0) work.Push(new Step.Text(" "));
                        }
                        if (!Append("(")) return false;
                    }
                    else work.Push(new Step.Word(assignment.Value));
                    break;
                case Step.Redirections(var redirections):
                    for (var index = redirections.Count - 1; index >= 0; index--)
                        work.Push(new Step.Redirection(redirections[index]));
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
                        hereDocuments.Enqueue(redirection);
                        work.Push(new Step.Word(delimiterWord));
                    }
                    else work.Push(new Step.Word(redirection.Target));
                    break;
                case Step.Function(var function, var indent, var nested):
                    // Keep keyword-only names reparsable, even when Bash's printed assignment-like name is not.
                    if ((display && nested || function.Name.Syntax.HasFlag(FunctionNameSyntax.KeywordOnly)) && !Append("function ")) return false;
                    if (!output.TryAppend(function.Name.Value.Bytes.Span) || !Append(" () ")) return false;
                    var body = !display || function.Body is GroupCommand or RedirectedCommand { Body: GroupCommand }
                        ? function.Body : new GroupCommand(function.Body);
                    Push(new Step.Layout(indent), new Step.Command(body, indent));
                    break;
                case Step.IfArm(var conditional, var arm, var indent):
                    var condition = arm < 0 ? conditional.Condition : conditional.ElifClauses[arm].Condition;
                    var thenBody = arm < 0 ? conditional.ThenBody : conditional.ElifClauses[arm].Body;
                    if (arm < 0) work.Push(new Step.Text("fi"));
                    if (arm + 1 < conditional.ElifClauses.Count)
                    {
                        // Keep flat chains flat: extra nesting can make a valid definition fail to import.
                        work.Push(new Step.IfArm(conditional, arm + 1, indent));
                    }
                    else if (conditional.ElseBody is { } elseBody)
                    {
                        Push(new Step.Text("else"), new Step.Layout(indent + 4),
                            new Step.Command(elseBody, indent + 4), new Step.Line(indent, Terminate: true));
                    }
                    Push(new Step.Text(arm < 0 ? "if " : "elif "), new Step.Command(condition, indent), new Step.Text("; then"),
                        new Step.Layout(indent + 4), new Step.Command(thenBody, indent + 4),
                        new Step.Line(indent, Terminate: true));
                    break;
                case Step.Command(var command, var indent):
                    switch (command)
                    {
                        case SimpleCommand simple:
                            work.Push(new Step.Redirections(simple.Redirections));
                            for (var index = simple.Words.Count - 1; index >= 0; index--)
                            {
                                work.Push(simple.DeclarationAssignments.TryGetValue(simple.Words[index], out var assignment)
                                    ? new Step.Assignment(assignment) : new Step.Word(simple.Words[index]));
                                if (index != 0 || simple.Assignments.Count != 0) work.Push(new Step.Text(" "));
                            }
                            for (var index = simple.Assignments.Count - 1; index >= 0; index--)
                            {
                                work.Push(new Step.Assignment(simple.Assignments[index]));
                                if (index != 0) work.Push(new Step.Text(" "));
                            }
                            break;
                        case ListCommand list:
                            Push(new Step.Command(list.Left, indent), list.Operator == ListOperator.Sequence
                                    ? new Step.Line(indent, Terminate: true)
                                    : new Step.Text(list.Operator == ListOperator.And ? " && " : " || "),
                                new Step.Command(list.Right, indent));
                            break;
                        case GroupCommand group:
                            Push(new Step.Text("{ "), new Step.Layout(indent + 4),
                                new Step.Command(group.Body, indent + 4), new Step.Line(indent, Terminate: false), new Step.Text("}"));
                            break;
                        case SubshellCommand subshell:
                            Push(new Step.Text("( "), new Step.Command(subshell.Body, indent), new Step.Text(" )"));
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
                            Push(new Step.Command(pipeline.Left, indent), new Step.Pipe(pipeline.Operator),
                                new Step.Command(pipeline.Right, indent));
                            break;
                        case IfCommand conditional:
                            work.Push(new Step.IfArm(conditional, -1, indent));
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
                            for (var index = loop.Items.Count - 1; index >= 0; index--)
                            {
                                work.Push(new Step.Word(loop.Items[index]));
                                if (index != 0) work.Push(new Step.Text(" "));
                            }
                            if (!Append("for ") || !Append(loop.Name) || !Append(" in ")) return false;
                            break;
                        case ArithmeticForCommand loop:
                            loop.Split(out var init, out var loopCondition, out var update);
                            Push(new Step.Text("for (("), new Step.Source(init), new Step.Text("; "),
                                new Step.Source(loopCondition), new Step.Text("; "), new Step.Source(update), new Step.Text("))"),
                                new Step.Line(indent, Terminate: false), new Step.Text("do"), new Step.Layout(indent + 4),
                                new Step.Command(loop.Body, indent + 4), new Step.Line(indent, Terminate: true), new Step.Text("done"));
                            break;
                        case ArithmeticCommand arithmetic:
                            Push(new Step.Text("(("), new Step.Source(arithmetic.Expression), new Step.Text("))"));
                            break;
                        case ConditionalCommand conditional:
                            Push(new Step.Text("[[ "), new Step.Source(conditional.Expression), new Step.Text(" ]]"));
                            break;
                        case CaseCommand selection:
                            work.Push(new Step.Text("esac"));
                            for (var index = selection.Items.Count - 1; index >= 0; index--)
                            {
                                var item = selection.Items[index];
                                Push(new Step.Text(")"), new Step.Layout(indent + 8), new Step.Command(item.Body, indent + 8),
                                    new Step.Layout(indent + 4), new Step.Text(";;"), new Step.Layout(indent));
                                for (var pattern = item.Patterns.Count - 1; pattern >= 0; pattern--)
                                {
                                    work.Push(new Step.Word(item.Patterns[pattern]));
                                    if (pattern != 0) work.Push(new Step.Text(" | "));
                                }
                                work.Push(new Step.Text("    "));
                            }
                            Push(new Step.Text("case "), new Step.Word(selection.Subject), new Step.Text(" in "), new Step.Layout(indent));
                            break;
                        case FunctionDefinition function:
                            work.Push(new Step.Function(function, indent, Nested: true));
                            break;
                        default:
                            throw new NotSupportedException($"Cannot render command kind {command.GetType().Name}.");
                    }
                    break;
            }
        }
        return true;

        bool Append(string text) => output.TryAppend(Utf8Text.Encode(text).Span);

        bool AppendCommandSubstitution(ReadOnlySpan<byte> source)
        {
            // Backtick payloads may start with a subshell or end in a comment without LF.
            var first = ShellSourceCursor.SkipLineContinuations(source, 0);
            return Append(first < source.Length && source[first] == (byte)'(' ? "$( " : "$(")
                && output.TryAppend(source)
                && (source.EndsWith("\n"u8) || Append("\n"))
                && Append(")");
        }

        bool FlushHereDocuments()
        {
            while (hereDocuments.TryDequeue(out var redirection))
            {
                foreach (var part in redirection.Target.Parts)
                    if (!output.TryAppend(part.GetUnexpandedValue().Bytes.Span)) return false;
                var delimiter = redirection.HereDocumentDelimiter
                    ?? throw new InvalidOperationException("A here-document requires its delimiter syntax.");
                if (!output.TryAppend(HereDocumentReader.GetHereDocumentDelimiter(delimiter, out _).AsSpan()) || !Append("\n")) return false;
            }
            return true;
        }

        void Push(params Step[] steps)
        {
            for (var index = steps.Length - 1; index >= 0; index--) work.Push(steps[index]);
        }

        void ScheduleLoop(string keyword, ICommand condition, ICommand body, int indent) =>
            Push(new Step.Text(keyword + " "), new Step.Command(condition, indent), new Step.Text("; do"),
                new Step.Layout(indent + 4), new Step.Command(body, indent + 4),
                new Step.Line(indent, Terminate: true), new Step.Text("done"));
    }

    /// <summary>Deferred syntax output; line steps also flush the current command line's here-documents.</summary>
    private abstract record Step
    {
        public sealed record Text(string Value) : Step;
        public sealed record Source(ShellSource Value) : Step;
        public sealed record Layout(int Indent) : Step;
        public sealed record Line(int Indent, bool Terminate) : Step;
        public sealed record Pipe(PipelineOperator Operator) : Step;
        public sealed record Word(Syntax.Word Value) : Step;
        public sealed record Assignment(AssignmentWord Value) : Step;
        public sealed record Redirection(Syntax.Redirection Value) : Step;
        public sealed record Redirections(IReadOnlyList<Syntax.Redirection> Values) : Step;
        public sealed record Function(FunctionDefinition Value, int Indent, bool Nested) : Step;
        public sealed record IfArm(IfCommand Value, int Arm, int Indent) : Step;
        public sealed record Command(ICommand Value, int Indent) : Step;
    }
}
