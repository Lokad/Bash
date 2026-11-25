using System.Collections.Generic;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Relates the active syntax tree's lines to the current script invocation.</summary>
internal readonly record struct CommandCoordinates(CommandLocations? Locations, int Offset, int Line, int EndLine)
{
    internal int RedirectionDiagnosticLine { get; init; }
    internal int? RedirectionOpenLine { get; init; }
    internal int CompoundHereDocumentLine { get; init; }
    /// <summary>Overrides a here-document warning's prefix while retaining the document's own line.</summary>
    internal int HereDocumentWarningLine { get; init; }
    /// <summary>Identifies the capture reader and the function depth where it began.</summary>
    internal int? CaptureFunctionDepth { get; init; }
    internal int FunctionBodyLine { get; init; }
    /// <summary>The absolute function brace line when the command unit began, or zero before any brace.</summary>
    internal int? FunctionBraceBeforeUnit { get; init; }
    /// <summary>The entry line inherited by non-forking compound redirections.</summary>
    internal int? CompoundOpenLine { get; init; }

    internal CommandCoordinates At(ICommand command, CommandLocation location) => this with
    {
        Line = Offset + (command switch
        {
            SimpleCommand or ForCommand => location.CommandLine,
            ArithmeticCommand or ConditionalCommand => location.EndLine,
            _ => location.StartLine
        }),
        EndLine = Offset + location.EndLine,
        RedirectionDiagnosticLine = location.RedirectionDiagnosticLine == 0 ? 0 : Offset + location.RedirectionDiagnosticLine,
        RedirectionOpenLine = command switch
        {
            FunctionDefinition => location.FunctionBraceLine == 0 ? FunctionBraceBeforeUnit.GetValueOrDefault() : Offset + location.FunctionBraceLine,
            RedirectedCommand { Body: GroupCommand or ForCommand or WhileCommand or UntilCommand or IfCommand or CaseCommand }
                when CompoundOpenLine.HasValue => CompoundOpenLine,
            _ when location.RedirectionOpenLine != 0 => Offset + location.RedirectionOpenLine,
            RedirectedCommand when location.CompoundHereDocumentLine != 0 => Offset + location.CompoundHereDocumentLine,
            RedirectedCommand => Offset + location.EndLine,
            _ => null
        },
        CompoundHereDocumentLine = location.CompoundHereDocumentLine == 0 ? 0 : Offset + location.CompoundHereDocumentLine
    };
}

/// <summary>
/// Selects active bindings and lexical context for evaluator-owned effects.
/// Creating a scope neither clones state nor transfers ownership.
/// </summary>
internal readonly record struct ScopedEvaluation(
    Evaluation Effects,
    Variables Variables,
    Evaluation.EvaluationContext Context)
{
    /// <summary>Bounds nested numeric evaluation and shares its stack-safe work pump.</summary>
    internal ArithmeticContext? Arithmetic { get; init; }

    /// <summary>
    /// Receives prefix-RHS writes in the caller, or caller redirection writes in temporary command bindings.
    /// Child evaluations construct their own scopes and do not inherit this target.
    /// </summary>
    public ExpansionBindings? ExpansionWriteTarget { get; init; }

    /// <summary>Retains builtin selector writes that belong to the caller rather than a temporary prefix.</summary>
    internal Variables.CommandBindings? SelectorBindings { get; init; }

    /// <summary>Diagnostic expansion retains command substitution spelling.</summary>
    internal bool PreserveCommandSubstitutionSpelling { get; init; }

    /// <summary>Retains the enclosing lexical word through nested operand expansion for diagnostics.</summary>
    internal Word? ExpansionWord { get; init; }

    /// <summary>Overrides the assignment default for a rejected selector's parameter diagnostic.</summary>
    internal bool? SelectorParameterDiagnostics { get; init; }

    /// <summary>Retains the parenthesized scalar spelling of a compound prefix during item expansion.</summary>
    internal IReadOnlyList<CompoundArrayItem>? CompoundPrefixItems { get; init; }

    /// <summary>Source role of words read during expansion, including nested operands.</summary>
    internal LegacySubstitutionOrigin ExpansionOrigin { get; init; }

    /// <summary>The nearest in-process simple invocation with redirections to restore.</summary>
    internal Evaluation.RedirectionScope? ErrexitRedirections { get; init; }

    internal Evaluation.ActiveTiming? Timing { get; init; }

    /// <summary>Separates timers started by the active reader from its callers' timers.</summary>
    internal Evaluation.ActiveTiming? ReaderEntryTiming { get; init; }

    /// <summary>Eval and source use a command-unit boundary even in a child shell.</summary>
    internal bool InNestedCommandUnits { get; init; }

    /// <summary>The descriptor depth at the inner reader's entry for ordinary errexit.</summary>
    internal int? InnerReaderRedirectionDepth { get; init; }

    /// <summary>Reader entries within the current command substitution.</summary>
    internal CaptureReaderFrame? CaptureReader { get; init; }

    /// <summary>A directly piped source or eval invocation catches ordinary errexit with status 1.</summary>
    internal bool InDirectPipelineReader { get; init; }

    /// <summary>Identifies a reached command from an eval reader for expansion diagnostics.</summary>
    internal bool InEvalReader { get; init; }

    /// <summary>Inherited syntax status and the function scope active when the reader entered.</summary>
    internal int? LoadedSyntaxFunctionDepth { get; init; }

    /// <summary>Records whether a loaded reader was invoked as a tested command.</summary>
    internal bool LoadedReaderIgnoresErrexit { get; init; }

    /// <summary>The first reader's entry in this shell, for inherited EXIT after pipeline expansion.</summary>
    internal PipelineExitBoundary PipelineExitReader { get; init; }

    /// <summary>Redirections and builtin execution run after a pipeline stage resets its EXIT action.</summary>
    internal bool AfterPipelineExitReset { get; init; }

    /// <summary>A builtin's closed diagnostic output stops its expansion without making later commands fatal.</summary>
    internal bool CanExpand => !Effects.HasPendingExpansionError && Effects.BuiltinDiagnosticsOpen;

    /// <summary>Copies visible and hidden bindings under the surrounding command's admission.</summary>
    internal (Variables Variables, Evaluation.EvaluationContext Context) ForkBindings() =>
        Context.Fork(Variables, Effects.Memory, Effects.CancellationToken);

    /// <summary>The current simple command owns the prefix-name set, adding names as their bindings are prepared.</summary>
    internal readonly record struct ExpansionBindings(Variables Variables, HashSet<BindingName> PrefixNames)
    {
        internal bool PreparingPrefixes { get; init; }
    }

    public ShellValue SetExpansionValueIgnoringReadonly(BindingName name, ShellValue value)
    {
        if (SelectorBindings is { } bindings && Context.FunctionContext?.IsLocal(name) == true
            && Variables.TryGetNameref(name, out _))
        {
            var resolved = Variables.ResolveNamerefName(name);
            if (!resolved.IsEmpty && bindings.RetainSelectorBinding(resolved, Effects.Memory, Effects.CancellationToken) is { } retained)
            {
                // Scalar writes through local namerefs reach the caller; subsequent reads still see the prefix.
                return retained.SetResolvedValuePreservingArray(resolved, value, Effects.Memory);
            }
        }

        var stored = Variables.SetExpansionValueIgnoringReadonly(name, value, Effects.Memory);
        if (SelectorBindings != null)
        {
            var resolved = Variables.ResolveNamerefName(name);
            GetSelectorWriteTarget(name, resolved)?.SetResolvedValuePreservingArray(resolved, value, Effects.Memory);
        }
        GetExpansionWriteTarget(name)?.SetExpansionValueIgnoringReadonly(name, value, Effects.Memory);
        return stored;
    }

    /// <summary>Retains selected builtin writes, except indirect writes into temporary prefixes.</summary>
    internal Variables? GetSelectorWriteTarget(BindingName sourceName, BindingName selectedName)
    {
        if (SelectorBindings is not { } bindings || IsIndirectPrefixWrite(sourceName, bindings.PrefixNames))
            return null;
        return bindings.RetainSelectorBinding(selectedName, Effects.Memory, Effects.CancellationToken);
    }

    /// <summary>Selects the additional bindings receiving this expansion write, if any.</summary>
    public Variables? GetExpansionWriteTarget(BindingName name)
    {
        if (ExpansionWriteTarget is not { } target)
            return null;

        return IsIndirectPrefixWrite(name, target.PrefixNames) ? null : target.Variables;
    }

    // Except for the local scalar case above, a nameref to a prefix updates only the expansion's own scope.
    // Direct writes cross the boundary, including assignments of the same value.
    private bool IsIndirectPrefixWrite(BindingName name, IReadOnlySet<BindingName> prefixes) =>
        Variables.TryGetNameref(name, out _)
        && Variables.TryResolveBinding(name, selectOwner: null, out var binding)
        && prefixes.Contains(binding.Name);
}

internal sealed record CaptureReaderFrame(int RedirectionDepth, int FunctionDepth, CaptureReaderFrame? Enclosing);

/// <summary>Enumerates the descriptor ranges unwound on ordinary errexit, from newest to oldest.</summary>
internal struct LoadedExitRedirections(ScopedEvaluation scope, int reachedDepth)
{
    private readonly PipelineExitBoundary _outermost = scope.PipelineExitReader;
    private CaptureReaderFrame? _reader = scope.CaptureReader;
    private Evaluation.FunctionContext? _function = scope.Context.FunctionContext;
    private int _depth = reachedDepth;
    private bool _retainsReaderCommands;

    internal bool TryTake(out int outerDepth, out int innerDepth)
    {
        while (_reader is { } reader)
        {
            _reader = reader.Enclosing;
            innerDepth = reader.RedirectionDepth;
            outerDepth = _reader?.RedirectionDepth ?? _outermost.RedirectionDepth;
            _depth = outerDepth;
            var outerFunctionDepth = _reader?.FunctionDepth ?? _outermost.FunctionDepth.GetValueOrDefault();
            var unwindsFunction = false;
            for (; _function is { } frame && frame.Depth > outerFunctionDepth; _function = frame.Enclosing)
                unwindsFunction |= frame.Depth <= reader.FunctionDepth && frame.CallerPositionals != null;

            // The first reader clears the active function context. A later unwind that
            // pops a function frame from that cleared context makes the next enclosing
            // reader discard its command's cleanup instead of running it.
            var restore = !_retainsReaderCommands;
            _retainsReaderCommands = restore && unwindsFunction;
            if (restore && innerDepth != outerDepth)
                return true;
        }

        innerDepth = _depth;
        outerDepth = _outermost.RedirectionDepth;
        _depth = outerDepth;
        return innerDepth != outerDepth;
    }
}

internal readonly record struct PipelineExitBoundary(int RedirectionDepth, int? FunctionDepth, Evaluation.SourceCallerFrame? Source)
{
    internal bool KeepsReachedBindings { get; init; }
}

/// <summary>Shares one pump across a numeric expression and its nested selectors.</summary>
internal sealed record ArithmeticContext(int Depth)
{
    public ArithmeticWorkQueue Work { get; } = new();
}
