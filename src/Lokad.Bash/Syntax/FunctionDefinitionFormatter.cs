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
    private enum Format { Display, Export, ImportValidation, BraceSource, SubstitutionSource, SubstitutionDisplay, SubstitutionDiagnostic, CaptureCoordinates }
    private enum CaptureSpelling { Source, Display, Diagnostic }

    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppend(FunctionDefinition definition, ByteMemoryBuilder output) =>
        TryAppend(new Step.Function(definition, 0, Nested: false), output, output.Budget, Format.Display, CancellationToken.None);

    /// <summary>Omits presentation-only tokens, indentation and wrapper groups before child parsing.</summary>
    /// <remarks>False leaves partial output; callers must discard it.</remarks>
    public static bool TryAppendExport(FunctionDefinition definition, BoundedByteMemoryBuilder output) =>
        TryAppend(new Step.Function(definition, 0, Nested: false), output, output.Budget, Format.Export, CancellationToken.None);

    internal static bool TryAppendImportValidation(FunctionDefinition definition, BoundedByteMemoryBuilder output,
        CancellationToken cancellationToken) =>
        TryAppend(new Step.Function(definition, 0, Nested: false), output, output.Budget, Format.ImportValidation, cancellationToken);

    /// <summary>Renders the word's lexical structure, retaining its quotes and escapes.</summary>
    internal static bool TryAppendWordSource(Word word, IByteMemoryWriter output, IMemoryBudget budget,
        CancellationToken cancellationToken, ShellScanContext? captures = null) =>
        TryAppend(new Step.Word(word), output, budget, Format.BraceSource, cancellationToken, captures: captures);

    internal static bool TryAppendWordDisplay(Word word, IByteMemoryWriter output, IMemoryBudget budget,
        CancellationToken cancellationToken) =>
        TryAppend(new Step.Word(word), output, budget, Format.SubstitutionDiagnostic, cancellationToken);

    /// <summary>Renders a lexical suffix while borrowing the original word parts.</summary>
    internal static bool TryAppendWordSource(IReadOnlyList<WordPart> parts, int firstPart, IByteMemoryWriter output,
        IMemoryBudget budget, CancellationToken cancellationToken) =>
        TryAppend(new Step.WordTail(parts, firstPart), output, budget, Format.BraceSource, cancellationToken);

    /// <summary>Locates commands in Bash's rendered capture body without materializing or parsing it again.</summary>
    internal static IReadOnlyList<Range>? ProjectCaptureLocations(ParsedScript script, MemoryCapacity? memory,
        bool collectOmittedSeparators)
    {
        if (script.Locations is not { } locations)
        {
            script.RenderedLineBreaks = 0;
            return null;
        }

        var counter = new LineCounter(collectOmittedSeparators, memory);
        TryAppend(new Step.Commands(script.Commands), counter, memory?.Budget, Format.CaptureCoordinates, CancellationToken.None, locations);
        script.RenderedLineBreaks = counter.Line - 1;
        script.RenderedFunctionBraceLine = counter.FunctionBraceLine;
        return counter.OmittedSeparators;
    }

    internal static bool TryAppendSubstitutionSource(
        CommandSubstitutionSyntax substitution, IByteMemoryWriter output, IMemoryBudget? budget,
        CancellationToken cancellationToken) => TryAppendSubstitution(substitution, output, budget, cancellationToken, CaptureSpelling.Source);

    internal static bool TryAppendSubstitutionDisplay(
        CommandSubstitutionSyntax substitution, IByteMemoryWriter output, IMemoryBudget? budget,
        CancellationToken cancellationToken) => TryAppendSubstitution(substitution, output, budget, cancellationToken, CaptureSpelling.Diagnostic);

    internal static bool TryAppendDiagnosticSpelling(SourceSpelling spelling, IByteMemoryWriter output, IMemoryBudget? budget) =>
        AppendSpelling(spelling, 0, output, budget, CancellationToken.None, CaptureSpelling.Diagnostic);

    private static bool TryAppendSubstitution(CommandSubstitutionSyntax substitution, IByteMemoryWriter output,
        IMemoryBudget? budget, CancellationToken cancellationToken, CaptureSpelling style)
    {
        if (substitution is CommandSubstitutionSyntax.Legacy legacy)
            return output.TryAppend(legacy.Spelling.AsSpan());
        if (substitution is CommandSubstitutionSyntax.Parsed { AdjacentSpelling: { } spelling } adjacent
            && (style != CaptureSpelling.Source || !adjacent.PreserveAdjacentOpening))
            return output.TryAppend("$("u8) && AppendSpelling(spelling, 0, output, budget, cancellationToken, style)
                && output.TryAppend((byte)')');
        if (substitution is CommandSubstitutionSyntax.Parsed parsed && CanRender(parsed, style))
        {
            var commands = parsed.Script.AuthoredCaptureCommands ?? parsed.Script.Commands;
            return output.TryAppend(StartsWithParenthesis(commands) ? "$( "u8 : "$("u8)
                && TryAppend(new Step.Commands(commands), output, budget, style switch
                {
                    CaptureSpelling.Display => Format.SubstitutionDisplay,
                    CaptureSpelling.Diagnostic => Format.SubstitutionDiagnostic,
                    _ => Format.SubstitutionSource
                }, cancellationToken)
                && output.TryAppend((byte)')');
        }
        return output.TryAppend("$("u8) && output.TryAppend(substitution.Source.AsSpan()) && output.TryAppend((byte)')');

        static bool StartsWithParenthesis(IReadOnlyList<ICommand> commands)
        {
            if (commands.Count == 0)
                return false;
            // A leading literal parenthesis after the opener would select arithmetic.
            var first = commands[0];
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

    private static bool CanRender(CommandSubstitutionSyntax.Parsed parsed, CaptureSpelling style) =>
        style == CaptureSpelling.Source ? parsed.CanRenderBody
            : parsed.CanDisplayBody || style == CaptureSpelling.Diagnostic && !parsed.PreserveAdjacentOpening && parsed.HasDiagnosticSpelling;

    private static bool AppendSpelling(SourceSpelling spelling, int sourceOffset, IByteMemoryWriter output,
        IMemoryBudget? budget, CancellationToken cancellationToken, CaptureSpelling style)
    {
        var source = spelling.Source.AsSpan();
        var written = 0;
        if (spelling.Captures is { } nested)
            foreach (var substitution in nested)
            {
                var offset = substitution.Offset - sourceOffset;
                if (offset < 0 || offset + substitution.Length > source.Length)
                    continue;
                if (!output.TryAppend(source[written..offset])
                    || !TryAppendSubstitution(substitution.Syntax, output, budget, cancellationToken, style))
                    return false;
                written = offset + substitution.Length;
            }
        return output.TryAppend(source[written..]);
    }

    private static bool TryAppend(Step initial, IByteMemoryWriter output, IMemoryBudget? budget, Format format,
        CancellationToken cancellationToken, CommandLocations? locations = null, ShellScanContext? captures = null)
    {
        var display = format == Format.Display;
        var importValidation = format == Format.ImportValidation;
        var captureSpelling = format switch
        {
            Format.Display or Format.SubstitutionDisplay or Format.ImportValidation => CaptureSpelling.Display,
            Format.SubstitutionDiagnostic => CaptureSpelling.Diagnostic,
            _ => CaptureSpelling.Source
        };
        var substitutionSource = format is Format.SubstitutionSource or Format.SubstitutionDisplay or Format.SubstitutionDiagnostic;
        var displayRedirections = display || substitutionSource;
        var inlineSource = format == Format.BraceSource || substitutionSource;
        var lines = output as LineCounter;
        var capture = lines != null;
        var functionDepth = 0;
        var readerDepth = 0;
        ICommand? unitFirstCommand = null;
        var flushedHereDocuments = false;
        // Explicit work items keep long left-associated lists off the CLR call stack.
        using var workMemory = budget == null ? null : new MemoryCapacity(budget, 0);
        using var documentMemory = budget == null ? null : new MemoryCapacity(budget, 0);
        var work = new Stack<Step>();
        var hereDocuments = new Queue<Redirection>();
        List<ICommand>? compoundDocumentLocations = null;
        var workCapacity = 0;
        var documentCapacity = 0;
        byte[] encodedFragment = [];
        PrepareWork(inlineSource || capture ? 1 : 2);
        if (!inlineSource && !capture)
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
                    if (display || inlineSource || capture || hereDocuments.Count != 0)
                        PushStep(new Step.Line(indent, Terminate: false));
                    else if (!Append(" ")) return false;
                    break;
                case Step.Line(var indent, var terminate):
                    if (!WriteLine(indent, terminate)) return false;
                    break;
                case Step.EnterFunction:
                    functionDepth++;
                    break;
                case Step.LeaveFunction(var completedFunction, var resetHereDocuments):
                    functionDepth--;
                    if (lines != null && locations?.TryGet(completedFunction, out var definitionLocation) == true)
                        locations.Update(completedFunction, definitionLocation with { FunctionBraceLine = lines.FunctionBraceLine });
                    if (resetHereDocuments) flushedHereDocuments = false;
                    break;
                case Step.FlushDocuments:
                    if (!FlushCaptureDocuments()) return false;
                    break;
                case Step.CompactEnd(var closer, var terminate):
                    if (hereDocuments.Count != 0)
                    {
                        if (!Append("\n") || !FlushHereDocuments()) return false;
                    }
                    else if (terminate && !Append(";")) return false;
                    if (!Append(closer)) return false;
                    break;
                case Step.Location(var command, var edge):
                    if (edge == LocationEdge.ReaderEnd)
                        readerDepth--;
                    if (lines != null && locations?.TryGet(command, out var location) == true)
                        locations.Update(command, edge == LocationEdge.FirstElement
                            ? location with { CommandLine = lines.Line }
                            : location with { EndLine = lines.Line });
                    break;
                case Step.Word(var word):
                    if (!AppendWordParts(word.Parts, 0)) return false;
                    break;
                case Step.WordTail(var parts, var firstPart):
                    if (!AppendWordParts(parts, firstPart)) return false;
                    break;
                case Step.Commands(var commands):
                    PrepareWork(Math.Max(0, checked(2 * commands.Count - 1)));
                    for (var index = commands.Count - 1; index >= 0; index--)
                    {
                        work.Push(new Step.Command(commands[index], 0));
                        if (index != 0)
                            work.Push(new Step.CommandSeparator(Newline: false, default, 0, FollowsCommandChain: false));
                    }
                    break;
                case Step.CommandSeparator(var newline, var separator, var indent, var followsCommandChain):
                    var pendingDocuments = hereDocuments.Count != 0;
                    var followsFlushedDocuments = !pendingDocuments && flushedHereDocuments;
                    if ((capture || displayRedirections) && followsFlushedDocuments)
                        flushedHereDocuments = false;
                    if (lines != null)
                    {
                        if (!FlushCaptureDocuments()) return false;
                        // A separator inside a compound still belongs to its enclosing reader unit.
                        if (readerDepth == 0 && (newline || pendingDocuments))
                        {
                            CompleteCompoundDocumentLocations(lines.Line - (pendingDocuments ? 1 : 0));
                            if (functionDepth == 0)
                                CompleteCapturedUnit();
                        }
                        if (followsFlushedDocuments && !newline && functionDepth == 0)
                            lines.RecordOmittedSeparator(separator);
                        var breaks = functionDepth != 0
                            ? newline && !pendingDocuments && !followsFlushedDocuments ? 2 : 1
                            : newline ? 1 : 0;
                        if (!output.TryAppend(breaks, (byte)'\n')) return false;
                    }
                    else if (display || importValidation)
                    {
                        // A body printed inside the left command suppresses this connector's semicolon.
                        if (!WriteLine(indent, terminate: !followsFlushedDocuments)) return false;
                    }
                    else if (substitutionSource && functionDepth != 0)
                    {
                        if (newline && !pendingDocuments && !followsFlushedDocuments && !Append("\n")) return false;
                        PushStep(new Step.Line(indent, Terminate: !newline && !followsFlushedDocuments));
                    }
                    else if (substitutionSource)
                    {
                        if (pendingDocuments && !newline)
                        {
                            if (!FlushCaptureDocuments() || !Append(" ")) return false;
                        }
                        else if (pendingDocuments)
                        {
                            if (!WriteLine(indent, terminate: false)) return false;
                        }
                        else if (!Append(newline ? "\n" : followsFlushedDocuments ? " " : "; ")) return false;
                    }
                    else if (!pendingDocuments)
                    {
                        if (!Append("; ")) return false;
                    }
                    else if (!Append("\n") || !FlushHereDocuments() || !Append("\n")) return false;
                    // A separator after a chain drains its pending body without affecting the next
                    // connector. A flush after the first command can still suppress a later separator.
                    if ((capture || substitutionSource) && followsCommandChain && pendingDocuments)
                        flushedHereDocuments = false;
                    break;
                case Step.LogicalSeparator(var operation):
                    if (capture)
                    {
                        if (!FlushCaptureDocuments()) return false;
                    }
                    else
                    {
                        if (!Append(operation == ListOperator.And ? " && " : " || ")) return false;
                        if (substitutionSource && hereDocuments.Count != 0
                            && (!Append("\n") || !FlushHereDocuments() || !Append(" "))) return false;
                    }
                    break;
                case Step.Pipe(var operation):
                    if (capture)
                    {
                        if (!FlushCaptureDocuments()) return false;
                        break;
                    }
                    if (!Append(operation == PipelineOperator.PipeAndError
                        ? display || substitutionSource ? " 2>&1 |" : " |&" : " |")) return false;
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
                        if (!Append("(")) return false;
                        for (var index = 0; index < values.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (index != 0 && !Append(" ")) return false;
                            if (!AppendWordParts(values[index].Word.Parts, 0)) return false;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!Append(")")) return false;
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
                case Step.Redirections(var redirections, var leadingSpace, var firstElement):
                    if ((capture || displayRedirections) && redirections.Count != 0)
                        flushedHereDocuments = false;
                    PrepareWork(redirections.Count + (firstElement != null ? 1 : 0));
                    for (var index = redirections.Count - 1; index >= 0; index--)
                    {
                        if (index == 0 && firstElement != null)
                            work.Push(new Step.Location(firstElement, LocationEdge.FirstElement));
                        work.Push(new Step.Redirection(redirections[index], leadingSpace || index != 0));
                    }
                    break;
                case Step.Redirection(var redirection, var leadingSpace):
                    if (leadingSpace && !Append(" ")) return false;
                    var literalDescriptor = 0;
                    var printLiteral = displayRedirections
                        && redirection.Kind is RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput
                        && redirection.TryGetLiteralDescriptor(out literalDescriptor);
                    if (redirection.VariableName.Length != 0)
                    {
                        if (!Append("{") || !Append(redirection.VariableName) || !Append("}")) return false;
                    }
                    else
                    {
                        var defaultDescriptor = redirection.DefaultFileDescriptor;
                        var descriptor = redirection.FileDescriptor ?? defaultDescriptor;
                        var printDefault = redirection.Kind switch
                        {
                            RedirectionKind.CloseInput or RedirectionKind.CloseOutput => true,
                            RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput => !displayRedirections || printLiteral,
                            RedirectionKind.InputOutput => displayRedirections || redirection.FileDescriptor.HasValue,
                            RedirectionKind.Input or RedirectionKind.Output or RedirectionKind.OutputAppend or RedirectionKind.ForceOutput
                                or RedirectionKind.HereString or RedirectionKind.HereDocument or RedirectionKind.HereDocumentStripTabs =>
                                !displayRedirections && redirection.FileDescriptor.HasValue,
                            _ => redirection.FileDescriptor.HasValue
                        };
                        if ((printDefault || descriptor != defaultDescriptor)
                            && !Append(descriptor.ToString(CultureInfo.InvariantCulture))) return false;
                    }
                    var redirectionOperator = redirection.Kind switch
                    {
                        RedirectionKind.Input or RedirectionKind.AllocateInput => "< ",
                        RedirectionKind.Output or RedirectionKind.AllocateOutput => "> ",
                        RedirectionKind.OutputAppend => ">> ",
                        RedirectionKind.InputOutput => "<> ",
                        RedirectionKind.ForceOutput => ">| ",
                        RedirectionKind.DuplicateOutput or RedirectionKind.CloseOutput => ">&",
                        RedirectionKind.CloseInput when displayRedirections => ">&",
                        RedirectionKind.DuplicateInput or RedirectionKind.CloseInput => "<&",
                        RedirectionKind.OutputAndError => "&> ",
                        RedirectionKind.OutputAndErrorAppend => "&>> ",
                        RedirectionKind.HereString => "<<< ",
                        RedirectionKind.HereDocument => "<<",
                        RedirectionKind.HereDocumentStripTabs => "<<-",
                        _ => throw new ArgumentOutOfRangeException(nameof(redirection.Kind))
                    };
                    if (!Append(redirectionOperator)) return false;
                    if (printLiteral)
                    {
                        if (!Append(literalDescriptor.ToString(CultureInfo.InvariantCulture))
                            || redirection.MovesDescriptor && !Append("-")) return false;
                    }
                    else if (redirection.HereDocumentDelimiter is { } delimiterWord)
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
                    if (((display || substitutionSource) && nested || function.Name.Syntax.HasFlag(FunctionNameSyntax.KeywordOnly))
                        && !Append("function ")) return false;
                    if (!output.TryAppend(function.Name.Value.Bytes.Span) || !Append(" () ")) return false;
                    var body = !(display || substitutionSource) || function.Body is GroupCommand or RedirectedCommand { Body: GroupCommand }
                        ? function.Body : new GroupCommand(function.Body);
                    if (capture)
                    {
                        // Bash emits an outer group even for a function with a different compound body.
                        var grouped = body is GroupCommand or RedirectedCommand { Body: GroupCommand };
                        if (lines != null)
                            lines.RecordFunctionBraceLine(lines.Line + 1);
                        Push(new Step.Text(grouped ? "\n" : "\n{\n"), new Step.EnterFunction(),
                            new Step.Command(body, indent), new Step.FlushDocuments(),
                            new Step.LeaveFunction(function, body is not RedirectedCommand { Body: GroupCommand }),
                            new Step.Text(grouped ? "" : "\n}"));
                    }
                    else if (substitutionSource || display && nested)
                        Push(new Step.Layout(indent), new Step.EnterFunction(), new Step.Command(body, indent),
                            new Step.LeaveFunction(function, body is not RedirectedCommand { Body: GroupCommand }));
                    else Push(new Step.Layout(indent), new Step.Command(body, indent));
                    break;
                case Step.IfArm(var conditional, var arm, var indent):
                    var condition = arm < 0 ? conditional.Condition : conditional.ElifClauses[arm].Condition;
                    var thenBody = arm < 0 ? conditional.ThenBody : conditional.ElifClauses[arm].Body;
                    if (arm < 0 || capture || substitutionSource) PushStep(new Step.Text("fi"));
                    if (arm + 1 < conditional.ElifClauses.Count)
                    {
                        // Capture layout nests elif as an if in the else body.
                        if (substitutionSource)
                            Push(new Step.Text("else"), new Step.Layout(indent + 4), new Step.IfArm(conditional, arm + 1, indent + 4),
                                new Step.Line(indent, Terminate: true));
                        else if (capture)
                            Push(new Step.Text("else\n"), new Step.IfArm(conditional, arm + 1, indent), new Step.Text("\n"));
                        else PushStep(new Step.IfArm(conditional, arm + 1, indent));
                    }
                    else if (conditional.ElseBody is { } elseBody)
                    {
                        Push(new Step.Text("else"), new Step.Layout(indent + 4),
                            new Step.Command(elseBody, indent + 4), new Step.Line(indent, Terminate: true));
                    }
                    Push(new Step.Text(arm < 0 || capture || substitutionSource ? "if " : "elif "), new Step.Command(condition, indent), new Step.Text("; then"),
                        new Step.Layout(indent + 4), new Step.Command(thenBody, indent + 4), new Step.Line(indent, Terminate: true));
                    break;
                case Step.Command(var command, var indent):
                    if (lines != null && command is not (ListCommand or NegatedCommand))
                        unitFirstCommand ??= command;
                    if (lines != null && locations?.TryGet(command, out var physical) == true)
                    {
                        locations.Update(command, new CommandLocation(lines.Line, lines.Line, lines.Line));
                        var opensReader = command is GroupCommand or SubshellCommand or IfCommand or CaseCommand
                            or ForCommand or ArithmeticForCommand or WhileCommand or UntilCommand;
                        if (opensReader)
                            readerDepth++;
                        PushStep(new Step.Location(command, opensReader ? LocationEdge.ReaderEnd : LocationEdge.CommandEnd));
                        if (physical.CompoundHereDocumentLine != 0)
                        {
                            if (compoundDocumentLocations == null)
                            {
                                documentMemory?.Grow(32);
                                compoundDocumentLocations = [];
                            }
                            documentMemory?.PrepareListCapacity(compoundDocumentLocations, 1);
                            compoundDocumentLocations.Add(command);
                        }
                    }
                    switch (command)
                    {
                        case SimpleCommand simple:
                            PrepareWork(Math.Max(1, checked(2 * (simple.Words.Count + simple.Assignments.Count))) + (capture ? 1 : 0));
                            var hasCommandElements = simple.Words.Count != 0 || simple.Assignments.Count != 0;
                            work.Push(new Step.Redirections(simple.Redirections, !displayRedirections || hasCommandElements,
                                capture && !hasCommandElements ? simple : null));
                            var commandLineWordIndex = Math.Min(simple.Words.Count - 1, 1);
                            for (var index = simple.Words.Count - 1; index >= 0; index--)
                            {
                                if (capture && index == commandLineWordIndex && simple.Assignments.Count == 0)
                                    work.Push(new Step.Location(simple, LocationEdge.FirstElement));
                                work.Push(simple.DeclarationAssignments.TryGetValue(simple.Words[index], out var assignment)
                                    ? new Step.Assignment(assignment) : new Step.Word(simple.Words[index]));
                                if (index != 0 || simple.Assignments.Count != 0) work.Push(new Step.Text(" "));
                            }
                            for (var index = simple.Assignments.Count - 1; index >= 0; index--)
                            {
                                if (capture && index == 0)
                                    work.Push(new Step.Location(simple, LocationEdge.FirstElement));
                                work.Push(new Step.Assignment(simple.Assignments[index]));
                                if (index != 0) work.Push(new Step.Text(" "));
                            }
                            break;
                        case ListCommand list:
                            Push(new Step.Command(list.Left, indent), list.Operator == ListOperator.Sequence
                                    ? display || inlineSource || capture || importValidation
                                        ? new Step.CommandSeparator(list.HasNewlineConnector, list.SeparatorSource, indent,
                                            list.Left is ListCommand or PipelineCommand or TimedCommand { Inner: PipelineCommand })
                                        : new Step.Line(indent, Terminate: true)
                                    : new Step.LogicalSeparator(list.Operator),
                                new Step.Command(list.Right, indent));
                            break;
                        case GroupCommand group:
                            if (capture)
                                Push(new Step.Text(functionDepth == 0 ? "{ " : "{\n"), new Step.Command(group.Body, indent + 4),
                                    new Step.FlushDocuments(), new Step.Text(functionDepth == 0 ? "}" : "\n}"));
                            else if (substitutionSource && functionDepth == 0)
                                Push(new Step.Text("{ "), new Step.Command(group.Body, indent), new Step.CompactEnd(" }", Terminate: true));
                            else Push(new Step.Text("{ "), new Step.Layout(indent + 4),
                                    new Step.Command(group.Body, indent + 4), new Step.Line(indent, Terminate: false), new Step.Text("}"));
                            break;
                        case SubshellCommand subshell:
                            if (capture)
                                Push(new Step.Command(subshell.Body, indent), new Step.FlushDocuments());
                            else Push(new Step.Text("( "), new Step.Command(subshell.Body, indent),
                                displayRedirections ? new Step.CompactEnd(" )", Terminate: false) : new Step.Text(" )"));
                            break;
                        case RedirectedCommand redirected:
                            Push(new Step.Command(redirected.Body, indent),
                                new Step.Redirections(redirected.Redirections, LeadingSpace: true, FirstElement: null));
                            break;
                        case NegatedCommand { Inner: TimedCommand { Inner: SimpleCommand { IsEmpty: true } } timed }
                            when captureSpelling == CaptureSpelling.Diagnostic:
                            if (!Append(timed.Portable ? "time -p ! " : "time ! ")) return false;
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
                            else if (!Append("[[ ") || !AppendSpelling(conditional.Spelling, 0, output, budget, cancellationToken, captureSpelling)
                                || !Append(" ]]"))
                                return false;
                            break;
                        case CaseCommand selection:
                            PushStep(new Step.Text("esac"));
                            for (var index = selection.Items.Count - 1; index >= 0; index--)
                            {
                                var item = selection.Items[index];
                                var terminator = item.Continuation switch
                                {
                                    CaseContinuation.FallThrough => ";&",
                                    CaseContinuation.Retest => ";;&",
                                    _ => ";;"
                                };
                                var emptyBody = item.Body is SimpleCommand
                                    { Assignments.Count: 0, Words.Count: 0, Redirections.Count: 0 };
                                PrepareWork(checked((emptyBody ? 6 : 7) + Math.Max(0, 2 * item.Patterns.Count - 1)));
                                work.Push(new Step.Layout(indent));
                                work.Push(new Step.Text(terminator));
                                work.Push(new Step.Layout(indent + 4));
                                if (!emptyBody)
                                    work.Push(new Step.Command(item.Body, indent + 8));
                                work.Push(new Step.Layout(emptyBody ? 0 : indent + 8));
                                work.Push(new Step.Text(")"));
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
        if (lines != null)
        {
            var pendingDocuments = hereDocuments.Count != 0;
            if (!FlushCaptureDocuments()) return false;
            CompleteCompoundDocumentLocations(lines.Line - (pendingDocuments ? 1 : 0));
            CompleteCapturedUnit();
            return true;
        }

        void CompleteCapturedUnit()
        {
            if (unitFirstCommand is { } first && lines != null && locations != null)
                lines.RecordUnitFunctionBrace(locations, first);
            unitFirstCommand = null;
        }
        return !inlineSource || hereDocuments.Count == 0 || Append("\n") && FlushHereDocuments();

        void CompleteCompoundDocumentLocations(int line)
        {
            if (locations == null || compoundDocumentLocations == null)
                return;
            foreach (var command in compoundDocumentLocations)
                if (locations.TryGet(command, out var location))
                    locations.Update(command, location with { CompoundHereDocumentLine = line });
            compoundDocumentLocations.Clear();
        }

        bool AppendWordParts(IReadOnlyList<WordPart> parts, int firstPart)
        {
            var doubleQuoted = false;
            for (var index = firstPart; index < parts.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = parts[index];
                var content = part.GetUnexpandedValue().Bytes.Span;
                var quotedPart = part.IsDoubleQuoted;
                if (doubleQuoted && quotedPart && part.DoubleQuote != DoubleQuoteBoundary.Continuation
                    && !Append("\"\"")) return false;
                if (quotedPart != doubleQuoted && !Append("\"")) return false;
                doubleQuoted = quotedPart;
                if (lines != null && part is SourceWordPart sourcePart)
                    CountWordFunctionBraces(sourcePart, lines);
                var success = part.Kind switch
                {
                    WordPartKind.Unquoted or WordPartKind.DoubleQuoted when !capture && part is SourceWordPart
                        { SpellingCaptures: { } nested } projected =>
                        AppendSpelling(new SourceSpelling(projected.Source, nested), projected.SourceOffset,
                            output, budget, cancellationToken, captureSpelling),
                    WordPartKind.Unquoted or WordPartKind.DoubleQuoted when captures != null && part is SourceWordPart source =>
                        TryAppendSourceWithCaptures(source.Source, captures, output, budget, cancellationToken),
                    WordPartKind.Unquoted => output.TryAppend(content),
                    // ANSI-C quoting keeps embedded apostrophes in one literal part on import.
                    WordPartKind.SingleQuoted when (format == Format.Export || importValidation) && content.Contains((byte)'\'') =>
                        ShellArgumentEscaping.TryAppendQuoted(output, content, ShellQuoteStyle.AnsiC),
                    WordPartKind.SingleQuoted => ShellArgumentEscaping.TryAppendLiteralSingleQuoted(output, content),
                    WordPartKind.DoubleQuoted => output.TryAppend(content),
                    WordPartKind.Escaped => Append("\\") && output.TryAppend(content),
                    WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted
                        when capture && part is CommandSubstitutionWordPart command => CountCaptureWord(command.Substitution),
                    WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted
                        when part is CommandSubstitutionWordPart command
                            && (inlineSource || captureSpelling != CaptureSpelling.Source
                                && command.Substitution is CommandSubstitutionSyntax.Parsed displayed && CanRender(displayed, captureSpelling)
                                || command.Substitution is CommandSubstitutionSyntax.Legacy
                                or CommandSubstitutionSyntax.AdjacentSubshell
                                or CommandSubstitutionSyntax.Parsed { CanRenderBody: true }
                                or CommandSubstitutionSyntax.Parsed { AdjacentSpelling: not null }
                                or CommandSubstitutionSyntax.Parsed { PreserveAdjacentOpening: true }
                                or CommandSubstitutionSyntax.Parsed { Script.HasReadAheadHereDocuments: true }) =>
                        TryAppendSubstitution(command.Substitution, output, budget, cancellationToken, captureSpelling),
                    WordPartKind.CommandSubstitution or WordPartKind.CommandSubstitutionQuoted =>
                        AppendCommandSubstitution(content),
                    _ => throw new InvalidOperationException("A here-document body is not an ordinary word.")
                };
                if (!success) return false;
                if (lines != null && part is SourceWordPart { RenderedLineAdjustment: var adjustment })
                    lines.Adjust(adjustment);
            }
            return !doubleQuoted || Append("\"");
        }

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
            if (substitution is CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } breaks } parsed)
            {
                if (lines != null && parsed.Script.RenderedFunctionBraceLine > 0)
                    lines.RecordFunctionBraceLine(lines.Line + parsed.Script.RenderedFunctionBraceLine - 1);
                return output.TryAppend(breaks, (byte)'\n');
            }
            return output.TryAppend(substitution.Source.AsSpan());
        }

        static void CountWordFunctionBraces(SourceWordPart word, LineCounter counter)
        {
            if (word.SpellingCaptures is not { } captures)
                return;

            var source = word.Source.AsSpan();
            var written = 0;
            var line = counter.Line;
            foreach (var capture in captures)
            {
                var offset = capture.Offset - word.SourceOffset;
                if (offset < written || offset + capture.Length > source.Length)
                    continue;
                line += source[written..offset].Count((byte)'\n');
                written = offset + capture.Length;
                if (capture.Syntax is CommandSubstitutionSyntax.Parsed { Script.RenderedLineBreaks: { } breaks } parsed)
                {
                    if (parsed.Script.RenderedFunctionBraceLine > 0)
                        counter.RecordFunctionBraceLine(line + parsed.Script.RenderedFunctionBraceLine - 1);
                    line += breaks;
                }
                else
                    line += source[offset..written].Count((byte)'\n');
            }
        }

        bool FlushCaptureDocuments()
        {
            if (hereDocuments.Count == 0) return true;
            return Append("\n") && FlushHereDocuments();
        }

        bool WriteLine(int indent, bool terminate)
        {
            if ((displayRedirections || importValidation) && terminate && hereDocuments.Count == 0 && !Append(";")) return false;
            if (!Append("\n")) return false;
            // The layout line resumes after the body, so its LF also counts in capture coordinates.
            if (hereDocuments.Count != 0
                && (!FlushHereDocuments() || (displayRedirections || capture) && !Append("\n"))) return false;
            return !(display || inlineSource) || output.TryAppend(indent, (byte)' ');
        }

        bool FlushHereDocuments()
        {
            if (hereDocuments.Count != 0)
                flushedHereDocuments = true;
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

    /// <summary>Appends source with cached command substitutions rendered at their original positions.</summary>
    internal static bool TryAppendSourceWithCaptures(ShellSource source, ShellScanContext captures,
        IByteMemoryWriter output, IMemoryBudget? budget, CancellationToken cancellationToken)
    {
        var bytes = source.AsSpan();
        var written = 0;
        while (true)
        {
            var index = FindNextCapture(source, captures, written, readAheadOnly: false, cancellationToken, out var parsed);
            if (index < 0)
                return output.TryAppend(bytes[written..]);
            if (!output.TryAppend(bytes[written..index])
                || !TryAppendSubstitutionSource(parsed.Substitution, output, budget, cancellationToken))
                return false;
            written = index + parsed.Length;
        }
    }

    /// <summary>Completes captures whose bodies were read outside their original operand spelling.</summary>
    internal static ShellSource RenderReadAheadCaptures(ShellSource source, ShellScanContext context)
    {
        if (context.HereDocumentReadAhead is not { } readAhead)
            return source;

        var index = FindNextCapture(source, context, 0, readAheadOnly: true, CancellationToken.None, out var cached);
        if (index < 0)
            return readAhead.CopyWithoutBodies(source, context);

        using var buffer = context.CreateByteBuilder(0);
        var written = 0;
        do
        {
            readAhead.AppendWithoutBodies(buffer, source.Slice(written, index - written));
            if (!TryAppendSubstitutionSource(cached.Substitution, buffer, context.SyntaxMemory?.Budget, CancellationToken.None))
                throw new ByteMemoryBuilder.LimitExceededException();
            written = index + cached.Length;
            index = FindNextCapture(source, context, written, readAheadOnly: true, CancellationToken.None, out cached);
        }
        while (index >= 0);
        readAhead.AppendWithoutBodies(buffer, source.Slice(written));
        return ShellSource.FromBytes(buffer.ToCompactMemory(context.SyntaxMemory));
    }

    private static int FindNextCapture(ShellSource source, ShellScanContext context, int start, bool readAheadOnly,
        CancellationToken cancellationToken, out ShellScanContext.CachedParse parsed)
    {
        var bytes = source.AsSpan();
        if (!context.HasCommandSubstitutionLookahead)
        {
            if (start < bytes.Length)
                cancellationToken.ThrowIfCancellationRequested();
            parsed = default;
            return -1;
        }
        for (var position = start; position < bytes.Length; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = bytes[position..].IndexOf((byte)'$');
            if (relative < 0)
                break;
            position += relative;
            // A modern capture starts with $(, possibly separated by a physical line continuation.
            if (position + 1 < bytes.Length && bytes[position + 1] is (byte)'(' or (byte)'\\'
                && context.TryGetCommandSubstitution(source, position, out parsed)
                && (!readAheadOnly || parsed.Substitution is CommandSubstitutionSyntax.Parsed { Script.HasReadAheadHereDocuments: true }))
                return position;
        }
        parsed = default;
        return -1;
    }

    internal static void AddDiagnosticCaptureEdits(ShellSource source, int start, int end, ShellScanContext context,
        ref BracedParameterScanner.ParameterSpellingEdits edits)
    {
        var slice = source.Slice(start, end - start);
        var next = 0;
        while (true)
        {
            var position = FindNextCapture(slice, context, next, readAheadOnly: false, CancellationToken.None, out var parsed);
            if (position < 0)
                return;
            next = position + parsed.Length;
            if (parsed.Substitution is CommandSubstitutionSyntax.Parsed { HasDiagnosticSpelling: true })
                edits.AddCapture(context, start + position, start + next, parsed.Substitution);
        }
    }

    private sealed class LineCounter(bool collectOmittedSeparators, MemoryCapacity? memory) : IByteMemoryWriter
    {
        internal int Line { get; private set; } = 1;
        internal int FunctionBraceLine { get; private set; }
        private bool _unitReadFunctionBrace;

        internal void RecordFunctionBraceLine(int line)
        {
            FunctionBraceLine = line;
            _unitReadFunctionBrace = true;
        }

        internal void RecordUnitFunctionBrace(CommandLocations locations, ICommand first)
        {
            if (!_unitReadFunctionBrace)
                return;
            _unitReadFunctionBrace = false;
            var diagnostics = locations.GetReadDiagnostics(first) with { FunctionBraceLine = FunctionBraceLine };
            locations.SetReadDiagnostics(first, diagnostics, memory);
        }
        internal List<Range>? OmittedSeparators { get; private set; }

        internal void RecordOmittedSeparator(Range separator)
        {
            if (!collectOmittedSeparators || separator.End.Value == separator.Start.Value)
                return;
            memory?.Grow(OmittedSeparators == null ? 128 : 32);
            (OmittedSeparators ??= []).Add(separator);
        }

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

    private enum LocationEdge { FirstElement, CommandEnd, ReaderEnd }

    /// <summary>Deferred syntax output; line steps also flush the current command line's here-documents.</summary>
    private abstract record Step
    {
        public sealed record Text(string Value) : Step;
        public sealed record Source(ShellSource Value) : Step;
        public sealed record Layout(int Indent) : Step;
        public sealed record Line(int Indent, bool Terminate) : Step;
        public sealed record CommandSeparator(bool Newline, Range SeparatorRange, int Indent, bool FollowsCommandChain) : Step;
        public sealed record LogicalSeparator(ListOperator Operator) : Step;
        public sealed record FlushDocuments : Step;
        public sealed record CompactEnd(string Closer, bool Terminate) : Step;
        public sealed record EnterFunction : Step;
        public sealed record LeaveFunction(FunctionDefinition Definition, bool ResetHereDocuments) : Step;
        public sealed record Location(ICommand Node, LocationEdge Edge) : Step;
        public sealed record Commands(IReadOnlyList<ICommand> Value) : Step;
        public sealed record Pipe(PipelineOperator Operator) : Step;
        public sealed record Word(Syntax.Word Value) : Step;
        public sealed record WordTail(IReadOnlyList<WordPart> Parts, int FirstPart) : Step;
        public sealed record Condition(ConditionalExpression Value) : Step;
        public sealed record Assignment(AssignmentWord Value) : Step;
        public sealed record Redirection(Syntax.Redirection Value, bool LeadingSpace) : Step;
        public sealed record Redirections(IReadOnlyList<Syntax.Redirection> Values, bool LeadingSpace, SimpleCommand? FirstElement) : Step;
        public sealed record Function(FunctionDefinition Value, int Indent, bool Nested) : Step;
        public sealed record IfArm(IfCommand Value, int Arm, int Indent) : Step;
        public sealed record Command(ICommand Value, int Indent) : Step;
    }
}
