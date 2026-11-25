using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Formatting;
using Lokad.Bash.Memory;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using BindingAttributes = Lokad.Bash.Evaluation.Variables.BindingAttributes;

namespace Lokad.Bash.Evaluation;

/// <summary>
/// Tracks progress through a parsed script and yields the next operation to execute.
/// </summary>
/// <remarks>
/// Evaluates deterministic shell syntax and emits host operations for I/O.
/// Lexical scopes own variable restoration and descriptor cleanup.
/// </remarks>
public sealed partial class Evaluation : IFileTester
{
    /// <summary> Holds the active mutable evaluation state; temporarily replaced while evaluating subshells. </summary>
    private EvaluationState _state;
    // In-process subshells borrow parent maps while their state is installed.
    private long _savedDescriptorMemoryBytes;
    private long _savedDirectoryStackBytes;

    internal long RetainedDescriptorMemoryBytes => checked(_savedDescriptorMemoryBytes + _state.RetainedDescriptorMemoryBytes);
    internal long RetainedDirectoryStackBytes => checked(_savedDirectoryStackBytes + _state.RetainedDirectoryStackBytes);
    /// <summary> Holds the active variable state; temporarily replaced while evaluating subshells. </summary>
    private Variables _vars;
    /// <summary> Stores the base STDIN descriptor for command invocations. </summary>
    private readonly FileDescriptor _stdIn;
    /// <summary> Stores the base STDOUT descriptor for command invocations. </summary>
    private readonly FileDescriptor _stdOut;
    /// <summary> Stores the base STDERR descriptor for command invocations. </summary>
    private readonly FileDescriptor _stdErr;
    /// <summary> Coordinates producer/consumer handoff of the next operation between evaluation and callers. </summary>
    private readonly NextStepTracker _nextStepTracker = new();
    /// <summary> Publishes each new operation to external observers when provided. </summary>
    private readonly Action<EvaluationOperation?>? _forwardNextStep;
    /// <summary> Invoked when the evaluation reaches a terminal state. </summary>
    private readonly Action _onComplete;
    /// <summary>
    /// Tracks the running evaluation task; set on first GetNextStep call and reused to avoid duplicate execution.
    /// </summary>
    private Task? _running;
    /// <summary>
    /// Tracks the cancellation token driving this evaluation, if any.
    /// </summary>
    private CancellationToken _cancellationToken;
    /// <summary>Owns command workspace and retained values under the caller's live-memory allowance.</summary>
    internal EvaluationMemory Memory { get; }
    /// <summary>
    /// Carries the shell context visible when this evaluation starts.
    /// </summary>
    private readonly EvaluationContext _initialContext;
    // Forks borrow the run's cache for caller-prepared syntax. Runtime sources own their own entries.
    private readonly Evaluation _preparedSyntaxRoot;
    private SourceMemory? _preparedSubstitutionMemory;
    /// <summary>
    /// Indicates whether flow escaping the root script should be treated as invalid.
    /// </summary>
    private readonly RootFlowHandling _rootFlowHandling;
    // Pipelines retain completed evaluators until all stages and descriptor cleanup finish.
    private bool _retainCompletedMemory;
    // Simple stages can inherit the status rules of the reader that started the pipeline.
    private bool _pipelineUsesLoadedUnitStatus;
    private int? _loadedSyntaxFunctionDepth;
    private bool _loadedReaderIgnoresErrexit;
    // A |& stage may replace its root command to append a redirection while borrowing the original locations.
    private ICommand? _pipelineStageSource;
    private SimpleCommand? _finalCaptureInput;
    /// <summary>
    /// Tracks descriptors inherited by a nested evaluator and therefore not owned by it.
    /// </summary>
    private readonly IReadOnlyList<FileDescriptor> _borrowedDescriptors;
    /// <summary>
    /// Tracks descriptors borrowed temporarily by an in-process subshell.
    /// </summary>
    private readonly Stack<HashSet<FileDescriptor>> _subshellBorrowedDescriptorScopes = new();
    /// <summary> Retains owned descriptors until their terminal close completes. </summary>
    private readonly OrderedDescriptorSet _pendingDescriptorCleanup = new();
    // One serialized cleanup batch; capacity is prepared alongside pending ownership before acquisition.
    private readonly OrderedDescriptorSet _descriptorCloseBuffer = new();
    // This execution owner survives workspace retirement through Engine's final closes.
    private (IMemoryBudget Budget, MemoryCapacity Capacity)? _descriptorCleanupMemory;
    private Dictionary<FileDescriptor, PipeInputWriter>? _pipeInputs;

    /// <summary>
    /// Initializes a new instance of the <see cref="Evaluation"/> class.
    /// </summary>
    /// <param name="script">The parsed script to evaluate.</param>
    /// <param name="stdOutOverride">
    /// Optional STDOUT descriptor for the root evaluation.
    /// </param>
    /// <param name="publishNextStep">
    /// Optional forwarding callback for emitted operations.
    /// </param>
    /// <param name="onComplete">
    /// Optional callback invoked when the evaluation reaches completion. When omitted,
    /// completion emits a terminal <c>null</c> operation through <see cref="PublishOperation"/>.
    /// </param>
    /// <param name="options"> Additional configuration options. </param>
    public Evaluation(
        ParsedScript script,
        FileDescriptor? stdOutOverride = null,
        Action<EvaluationOperation?>? publishNextStep = null,
        Action? onComplete = null,
        EvaluationOptions? options = null)
        : this(
            script,
            stdOutOverride.HasValue
                ? new EvaluationDescriptorSetup(DescriptorTable.Snapshot.Standard.WithStandardStreams(
                    FileDescriptor.StdIn, stdOutOverride.Value, FileDescriptor.StdErr), [])
                : EvaluationDescriptorSetup.Standard,
            publishNextStep,
            onComplete,
            options ?? EvaluationOptions.Default,
            new MemoryGovernor(long.MaxValue), sourceMemory: null)
    {
    }

    internal Evaluation(
        ParsedScript script,
        EvaluationDescriptorSetup descriptorSetup,
        Action<EvaluationOperation?>? publishNextStep,
        Action? onComplete,
        EvaluationOptions options,
        IMemoryBudget memoryBudget,
        SourceMemory? sourceMemory)
        : this(
            new EvaluationState(script, options.CurrentDirectory, descriptorSetup.Bindings.Fork()),
            global::Lokad.Bash.Evaluation.Variables.CreateEmpty(),
            descriptorSetup,
            options,
            new EvaluationContext(0, null, ReturnTargetKind.None)
            {
                SourceMemory = sourceMemory,
                Coordinates = new CommandCoordinates(script.Locations, 0, 1, 1)
            },
            RootFlowHandling.Normalize,
            publishNextStep,
            onComplete,
            memoryBudget,
            preparedSyntaxRoot: null)
    {
        _vars.TrySetScalar("PWD", ShellValue.FromText(_state.CurrentDirectory.Path));
        _vars.SetExported("PWD", true);
        _vars.TryDeclareName("OLDPWD");
        _vars.SetExported("OLDPWD", true);
        var one = ShellValue.FromText("1");
        _vars.TrySetScalar("OPTIND", one);
        _vars.SetIntegerBinding("OPTIND", true);
        _vars.TrySetScalar("OPTERR", one);
        _vars.InitializeLineNumber();
    }

    /// <summary>Forks bindings and shell state; descriptors stay borrowed and live memory shares the caller's allowance.</summary>
    internal static Evaluation CreateCommandSubstitution(
        ScopedEvaluation scope,
        ParsedScript script,
        FileDescriptor output,
        int hereDocumentWarningLine,
        SimpleCommand? finalInput)
    {
        var outer = scope.Effects;
        var descriptors = outer.CreateCommandSubstitutionDescriptorSetup(output);
        var (variables, context) = scope.ForkBindings();
        var line = Math.Max(1, scope.Context.Coordinates.Line);
        context = context.WithCoordinates(new CommandCoordinates(script.Locations, line - 1, line, line)
        {
            CaptureFunctionDepth = context.FunctionContext?.Depth ?? 0,
            HereDocumentWarningLine = hereDocumentWarningLine
        });
        var state = outer._state.CloneForNestedEvaluation(script, descriptors, ShellForkKind.CommandSubstitution, outer.Memory);
        return new Evaluation(
            state,
            variables,
            descriptors,
            outer.Options with { CurrentDirectory = state.CurrentDirectory },
            context,
            RootFlowHandling.Preserve,
            publishNextStep: null,
            onComplete: null,
            outer.Memory.Budget,
            outer._preparedSyntaxRoot)
        {
            _finalCaptureInput = finalInput
        };
    }

    private Evaluation(
        EvaluationState state,
        Variables variables,
        EvaluationDescriptorSetup descriptorSetup,
        EvaluationOptions options,
        EvaluationContext initialContext,
        RootFlowHandling rootFlowHandling,
        Action<EvaluationOperation?>? publishNextStep,
        Action? onComplete,
        IMemoryBudget memoryBudget,
        Evaluation? preparedSyntaxRoot)
    {
        Options = options;
        _state = state;
        _vars = variables;
        _stdIn = descriptorSetup.Bindings.StdIn;
        _stdOut = descriptorSetup.Bindings.StdOut;
        _stdErr = descriptorSetup.Bindings.StdErr;
        _forwardNextStep = publishNextStep;
        _onComplete = onComplete ?? (() => PublishOperation(null));
        _initialContext = initialContext;
        _preparedSyntaxRoot = preparedSyntaxRoot ?? this;
        _rootFlowHandling = rootFlowHandling;
        _borrowedDescriptors = descriptorSetup.BorrowedDescriptors;
        Memory = new EvaluationMemory(memoryBudget);
    }

    /// <summary> Gets evaluation options. </summary>
    public EvaluationOptions Options { get; }

    internal ShellValue ShellOptionFlags => _state.ErrexitEnabled ? ShellValue.FromText("e") : ShellValue.Empty;

    /// <summary> Gets the last recorded exit code, if any. </summary>
    public int? LastExitCode => _state.LastExitCode;

    /// <summary> Gets the current working directory. </summary>
    public ShellPath CurrentDirectory => _state.CurrentDirectory;

    /// <summary>Borrowed by word expansion on this evaluator's serialized execution path.</summary>
    internal int DirectoryStackCount => _state.DirectoryStackCount;
    internal EvaluationState.DirectoryStackEntry GetDirectoryStackEntry(int index) => _state.GetDirectoryStackEntry(index);

    /// <summary> Gets the current variable assignments. </summary>
    public IReadOnlyDictionary<BindingName, ShellValue> Variables => _vars.Scalars;

    /// <summary> Gets the exported variable names. </summary>
    public IReadOnlyCollection<BindingName> ExportedVariables => _vars.ExportedNames;

    /// <summary> Gets the readonly variable names. </summary>
    public IReadOnlyCollection<BindingName> ReadonlyVariables => _vars.ReadonlyNames;

    /// <summary>Gets a detached dense view of the indexed array values.</summary>
    /// <exception cref="InvalidOperationException">
    /// A sparse position exceeds the range of the legacy dense view.
    /// </exception>
    public IReadOnlyDictionary<BindingName, List<ShellValue>> IndexedArrays =>
        _vars.CaptureDenseIndexedArrays();

    /// <summary>Gets detached associative arrays with byte-valued keys and values.</summary>
    public IReadOnlyDictionary<BindingName, Dictionary<ShellValue, ShellValue>> AssociativeArrays =>
        _vars.CaptureAssociativeArrays();

    /// <summary>Gets detached nameref mappings, retaining unexpanded target bytes.</summary>
    public IReadOnlyDictionary<BindingName, ShellValue> Namerefs => _vars.Namerefs;

    /// <summary>Gets the live read-only positional parameters.</summary>
    public IReadOnlyList<ShellValue> PositionalParameters => _vars.PositionalParameters;

    /// <summary>Replaces positional parameters without exposing mutable storage.</summary>
    internal void ReplacePositionalParameters(IReadOnlyList<ShellValue> positionalParameters) =>
        _vars.ReplacePositionalParameters(positionalParameters);

    Task<IReadOnlyList<DirectoryEntry>> IFileTester.ListDirectoryAsync(string path) =>
        ExecuteListDirectoryAsync(path);

    async Task<bool> IFileTester.IsDirectoryAsync(string path)
    {
        CheckCancellation();
        if (!TryReadFileOperand(path, _state.CurrentDirectory, out var raw) || raw is null)
            return false;
        // The candidate owns the pathname while the host resolves it.
        var lookup = new FilePathLookup(raw, _state.CurrentDirectory, FileLinkFollowing.Follow);
        var results = await PathInfosOperation.AsTask(PublishOperation, [lookup], FilePathInfoRequest.KindOnly)
            .ConfigureAwait(false);
        CheckCancellation();
        return results[0].Error == null && results[0].Info.Kind == FilePathKind.Directory;
    }

    /// <summary>
    ///     Advance internal evaluation until it reaches the next host operation to execute,
    ///     or null when evaluation is complete.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that stops evaluation.</param>
    public Task<EvaluationOperation?> GetNextStep(CancellationToken cancellationToken = default)
    {
        _ = EnsureRunning(cancellationToken);
        return WaitForNextStepAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the next operation without changing the token that drives evaluation.
    /// </summary>
    internal Task<EvaluationOperation?> WaitForNextStepAsync(CancellationToken cancellationToken) =>
        _nextStepTracker.GetNextStep(cancellationToken);

    /// <summary>
    /// Starts evaluation if needed and returns the running task.
    /// </summary>
    internal Task EnsureRunning(CancellationToken cancellationToken)
    {
        if (_running == null)
        {
            _cancellationToken = cancellationToken;
            _running = EvaluateAsync();
        }

        return _running;
    }

    internal bool HasStarted => _running != null;

    /// <summary>
    /// Gets the evaluation task so the engine can wait for cancellation or failure cleanup state to settle.
    /// </summary>
    internal Task Completion => _running ?? Task.CompletedTask;

    /// <summary>
    /// Gets descriptors that still require host-side cleanup.
    /// </summary>
    internal IReadOnlyList<FileDescriptor> GetDescriptorsForCleanup() => _pendingDescriptorCleanup.ToArray();

    internal bool TryGetDescriptorForCleanup(out FileDescriptor descriptor) =>
        _pendingDescriptorCleanup.TryPeekFirst(out descriptor);

    internal void ReleaseDescriptorCleanupMemory()
    {
        _pendingDescriptorCleanup.ReleaseStorage();
        _descriptorCloseBuffer.ReleaseStorage();
        _subshellBorrowedDescriptorScopes.Clear();
        _subshellBorrowedDescriptorScopes.TrimExcess();
        _pipeInputs = null;
        _descriptorCleanupMemory?.Capacity.Dispose();
        _descriptorCleanupMemory = null;
    }

    /// <summary>Prepares cleanup metadata before acquiring handles; callers remain serialized across acquisition.</summary>
    internal void PrepareOwnedDescriptors(int additional)
    {
        if (additional == 0)
            return;
        var capacity = GetDescriptorCleanupCapacity();
        _pendingDescriptorCleanup.PrepareAdditional(additional, capacity);
        _descriptorCloseBuffer.PrepareAdditional(checked(_pendingDescriptorCleanup.Count + additional), capacity);
        capacity.Resize(DescriptorCleanupMemoryBytes);
        capacity.TrimExcess();
    }

    private long PipeInputMemoryBytes => _pipeInputs is { } inputs && inputs.EnsureCapacity(0) > 0
        ? checked(128L + 48L * inputs.EnsureCapacity(0)) : 0;

    private long DescriptorCleanupMemoryBytes
    {
        get
        {
            var scopeCapacity = _subshellBorrowedDescriptorScopes.EnsureCapacity(0);
            return checked(_pendingDescriptorCleanup.EstimatedMemoryBytes + _descriptorCloseBuffer.EstimatedMemoryBytes
                + PipeInputMemoryBytes + (scopeCapacity == 0 ? 0 : 32L + 8L * scopeCapacity));
        }
    }

    private MemoryCapacity GetDescriptorCleanupCapacity()
    {
        var budget = Memory.Budget;
        if (_descriptorCleanupMemory is { } current && ReferenceEquals(current.Budget, budget))
            return current.Capacity;
        var capacity = new MemoryCapacity(budget, 0);
        try { capacity.Grow(DescriptorCleanupMemoryBytes); }
        catch { capacity.Dispose(); throw; }
        _descriptorCleanupMemory?.Capacity.Dispose();
        _descriptorCleanupMemory = (budget, capacity);
        return capacity;
    }

    /// <summary>Admits registration growth before acquiring a here-input's endpoints.</summary>
    internal void PreparePipeInput()
    {
        var memory = GetDescriptorCleanupCapacity();
        var inputs = _pipeInputs ??= new Dictionary<FileDescriptor, PipeInputWriter>();
        var capacity = inputs.EnsureCapacity(0);
        if (inputs.Count < capacity)
            return;
        var target = (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * capacity));
        memory.Grow(checked(128L + 48L * (2L * target + 1)));
        inputs.EnsureCapacity(target);
        memory.Resize(DescriptorCleanupMemoryBytes);
        memory.TrimExcess();
    }

    /// <summary> Records that final host-side cleanup completed for a descriptor. </summary>
    internal void MarkDescriptorCleanupCompleted(FileDescriptor descriptor) =>
        MarkDescriptorClosed(descriptor);

    /// <summary>Transfers unfinished child cleanup to its parent after the child has settled.</summary>
    internal async ValueTask<MemoryBudgetExceededException?> TransferDescriptorCleanupToAsync(Evaluation parent)
    {
        var missing = 0;
        foreach (var descriptor in _pendingDescriptorCleanup)
            if (!parent._pendingDescriptorCleanup.Contains(descriptor))
                missing++;
        try { parent.PrepareOwnedDescriptors(missing); }
        catch (MemoryBudgetExceededException refusal)
        {
            // Refusal fails the enclosing command. The settled child keeps ownership
            // until the parent's ordinary operation path has attempted every close.
            while (TryGetDescriptorForCleanup(out var descriptor))
            {
                try { await parent.CloseOwnedDescriptorAsync(descriptor, this).ConfigureAwait(false); }
                catch { /* Preserve admission refusal after completing cleanup. */ }
            }
            ReleaseDescriptorCleanupMemory();
            return refusal;
        }
        while (TryGetDescriptorForCleanup(out var pending))
        {
            parent._pendingDescriptorCleanup.AddPrepared(pending);
            MarkDescriptorCleanupCompleted(pending);
        }
        ReleaseDescriptorCleanupMemory();
        return null;
    }

    /// <summary>Releases a private or child namespace; caller-owned prepared root state stays retained.</summary>
    internal void ReleaseFunctions() => _state.ReleaseFunctions();

    /// <summary>Returns run-owned deferred syntax storage for a caller-prepared tree.</summary>
    internal SourceMemory GetPreparedSubstitutionMemory()
    {
        var root = _preparedSyntaxRoot;
        var owner = Volatile.Read(ref root._preparedSubstitutionMemory);
        if (owner != null)
            return owner;

        var candidate = new SourceMemory(root.Memory.Budget.CreateScope());
        owner = Interlocked.CompareExchange(ref root._preparedSubstitutionMemory, candidate, null);
        if (owner == null)
            return candidate;
        candidate.Release();
        return owner;
    }

    /// <summary>
    /// Runs the evaluation loop to completion and publishes the final null operation.
    /// </summary>
    /// <remarks>
    /// This method is invoked once on the first <see cref="GetNextStep"/> call and
    /// drives evaluation of the parsed script, updating the last exit code on completion.
    /// </remarks>
    private async Task EvaluateAsync()
    {
        try
        {
            var result = EvaluationResult.FromExitCode(0);
            Exception? failure = null;
            OperationError? closeError = null;
            try
            {
                var scope = new ScopedEvaluation(this, _vars, _initialContext)
                {
                    LoadedSyntaxFunctionDepth = _loadedSyntaxFunctionDepth,
                    LoadedReaderIgnoresErrexit = _loadedReaderIgnoresErrexit
                };
                if (!await TryImportInheritedFunctionsAsync(scope).ConfigureAwait(false))
                    result = EvaluationResult.FromExitCode(2);
                else
                {
                    result = await EvaluateScriptAsync(scope, _rootFlowHandling).ConfigureAwait(false);
                    if (_state.ForkKind == ShellForkKind.Subshell)
                        result = CompleteDiscardInChild(result);
                    result = await RunExitTrapAsync(scope, result).ConfigureAwait(false);
                }
            }
            catch (ByteMemoryBuilder.LimitExceededException)
            {
                result = EvaluationResult.FromExitCode(2);
            }
            catch (SyntaxException error)
            {
                // Deferred word parsing reports its diagnostic before unwinding redirections.
                failure = error;
                result = EvaluationResult.FromExitCode(2);
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                try { closeError = await CloseEvaluationDescriptorsAsync(failure != null || result.ExitCode != 0).ConfigureAwait(false); }
                finally
                {
                    _state.PreparationDiagnosticName = default;
                    ReleaseReadWorkspace();
                    Interlocked.Exchange(ref _preparedSubstitutionMemory, null)?.Release();
                    _state.ReleaseExportedFunctionSelection();
                    Memory.Finish(_rootFlowHandling == RootFlowHandling.Normalize || _retainCompletedMemory);
                }
            }

            result = await ApplyCloseErrorAsync(result, closeError).ConfigureAwait(false);
            _state.LastExitCode = result.ExitCode;
            _onComplete();
        }
        catch (OperationCanceledException ex)
        {
            _nextStepTracker.SetCanceled(ex.CancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            _nextStepTracker.SetException(ex);
            throw;
        }

        async Task<OperationError?> CloseEvaluationDescriptorsAsync(bool operationFailed)
        {
            var descriptors = CollectRedirectionDescriptorsToClose(excluded: null);
            try { return await CloseDescriptorsAsync(descriptors, operationFailed).ConfigureAwait(false); }
            finally { _state.RetireDescriptorHandles(); }
        }
    }

    /// <summary>
    /// Applies a leading negation to an evaluation result and updates the last exit code.
    /// </summary>
    /// <param name="result">The result to negate when requested.</param>
    /// <param name="negated">Whether the result should be negated.</param>
    /// <returns>The updated evaluation result with the correct exit code and flow.</returns>
    private EvaluationResult ApplyNegation(EvaluationResult result, bool negated)
    {
        if (result.Flow != FlowControlKind.Exit)
            _state.LastExitCode = result.ExitCode;

        // Return/exit unwind past enclosing negation; their caller negates the consumed status.
        if (!negated || result.Flow is FlowControlKind.Exit or FlowControlKind.Return)
            return result;

        var exitCode = EvaluationRules.ApplyNegation(result.ExitCode, true);
        var updated = result.WithExitCode(exitCode);
        if (updated.Flow != FlowControlKind.Exit)
            _state.LastExitCode = updated.ExitCode;

        return updated;
    }

    /// <summary>
    /// Normalizes flow control that is invalid at the current evaluation depth.
    /// </summary>
    /// <remarks>
    /// When break/continue/return escape their valid scopes, they become exit codes
    /// per requirements so evaluation can continue predictably.
    /// </remarks>
    private EvaluationResult NormalizeUnhandledFlow(EvaluationResult result)
    {
        switch (result.Flow)
        {
            case FlowControlKind.Break:
                var exitCode = result.ExitCode == 0 ? 1 : result.ExitCode;
                return EvaluationResult.FromExitCode(exitCode);
            case FlowControlKind.Continue:
            case FlowControlKind.Return:
                return EvaluationResult.FromExitCode(1);
            default:
                return result;
        }
    }

    /// <summary>
    /// Applies deferred expansion errors and negation before returning.
    /// </summary>
    private Task<EvaluationResult> FinalizeResultAsync(EvaluationResult result, bool negated)
    {
        if (_state.FatalExpansion is { } failure)
            return Task.FromResult(EvaluationResult.Exit(failure.ExitCode));

        if (result.Flow != FlowControlKind.Exit && _state.TryConsumeExpansionError(out var errorExitCode))
        {
            result = result.WithExitCode(errorExitCode);
        }

        return Task.FromResult(ApplyNegation(result, negated));
    }

    /// <summary>
    /// Evaluates the top-level script using a provided evaluation context.
    /// </summary>
    private async Task<EvaluationResult> EvaluateScriptAsync(
        ScopedEvaluation scope,
        RootFlowHandling rootFlowHandling) =>
        await EvaluateParsedScriptAsync(_state.Script, scope, rootFlowHandling).ConfigureAwait(false);

    /// <summary>
    /// Evaluates each command in a parsed script sequentially, honoring flow-control outcomes.
    /// </summary>
    private async Task<EvaluationResult> EvaluateParsedScriptAsync(
        ParsedScript script,
        ScopedEvaluation scope,
        RootFlowHandling rootFlowHandling)
    {
        var exitCode = 0;
        foreach (var command in script.Commands)
        {
            CheckCancellation();
            var result = await EvaluateCommandAsync(command, scope, negated: false)
                .ConfigureAwait(false);

            if (result.DiscardsCommandUnit && rootFlowHandling == RootFlowHandling.Normalize)
                result = await RecoverDiscardedCommandUnitAsync(scope).ConfigureAwait(false);
            else if (result.Flow == FlowControlKind.Exit)
            {
                var recoveryStatus = CanRecoverCommandSubstitutionSyntax ? 1 : RecoverableRootExpansionStatus;
                if (recoveryStatus is not { } status)
                    return result;
                result = await RecoverExpansionFailureAsync(status, scope, loadedReader: false).ConfigureAwait(false);
            }

            if (result.Flow != FlowControlKind.None)
            {
                if (rootFlowHandling == RootFlowHandling.Preserve)
                    return result;

                result = NormalizeUnhandledFlow(result);
                _state.LastExitCode = result.ExitCode;
            }

            exitCode = result.ExitCode;
        }

        return EvaluationResult.FromExitCode(exitCode);
    }


    /// <summary>
    /// Evaluates a group command without creating a subshell.
    /// </summary>
    private async Task<EvaluationResult> EvaluateGroupAsync(
        GroupCommand groupCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        var result = await EvaluateCommandAsync(groupCommand.Body, scope, negated: false).ConfigureAwait(false);
        return ApplyNegation(result, negated);
    }

    /// <summary>
    /// Evaluates a redirected command by applying redirections for the duration of the inner command.
    /// </summary>
    private async Task<EvaluationResult> EvaluateRedirectedCommandAsync(
        RedirectedCommand redirectedCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        if (redirectedCommand.Body is SubshellCommand subshell)
            return await EvaluateSubshellAsync(subshell, scope, negated, redirectedCommand.Redirections).ConfigureAwait(false);
        var redirectionScope = await OpenRedirectionScopeAsync(redirectedCommand.Redirections, HereDocumentRedirectionScope(scope),
            RedirectionPersistence.AllocationsAndMoves, RedirectionLifetime.Command, RedirectionOrigin.Command,
            reportMoveRestorationFailure: !IsPipelineStageCommand(redirectedCommand))
            .ConfigureAwait(false);
        Exception? scopeFailure = null;
        var result = EvaluationResult.FromExitCode(0);
        OperationError? closeError = null;
        try
        {
            if (_state.TryConsumeExpansionError(out var redirectionExitCode, out var errorKind))
            {
                if (errorKind == ExpansionErrorKind.HereDocumentParameter)
                    result = await CompleteHereDocumentParameterFailureAsync(redirectionExitCode, scope).ConfigureAwait(false);
                else if (errorKind == ExpansionErrorKind.HereDocumentSyntax && _state.FatalExpansion == null)
                {
                    result = ReaderErrexitEnabled(scope)
                        ? await RunExitTrapAsync(scope, EvaluationResult.Exit(2), ExitTrapKind.Expansion).ConfigureAwait(false)
                        : EvaluationResult.DiscardCompoundRedirectionUnit();
                }
                else
                    result = await CompleteRedirectionFailureAsync(redirectionExitCode, errorKind, scope).ConfigureAwait(false);
            }
            else
                result = await EvaluateCommandAsync(redirectedCommand.Body, scope, negated: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scopeFailure = ex;
            throw;
        }
        finally
        {
            closeError = await redirectionScope.CloseAsync(scopeFailure != null || result.ExitCode != 0).ConfigureAwait(false);
        }
        result = await ApplyCloseErrorAsync(result, closeError).ConfigureAwait(false);
        return ApplyNegation(result, negated);

        ScopedEvaluation HereDocumentRedirectionScope(ScopedEvaluation commandScope)
        {
            var coordinates = commandScope.Context.Coordinates;
            if (coordinates.CompoundHereDocumentLine == 0
                || redirectedCommand.Body is not (ArithmeticCommand or ConditionalCommand)
                || coordinates.Locations?.TryGet(redirectedCommand.Body, out var location) != true)
                return commandScope;
            return commandScope with
            {
                Context = commandScope.Context.WithCoordinates(coordinates with
                {
                    HereDocumentWarningLine = coordinates.Offset + location.EndLine
                })
            };
        }
    }

    private ValueTask<EvaluationResult> CompleteHereDocumentParameterFailureAsync(int status, ScopedEvaluation scope)
    {
        _state.LastExitCode = status;
        PublishExpansionPipelineStatus(status, scope);
        if (ReaderErrexitEnabled(scope))
            return RunExitTrapAsync(scope, EvaluationResult.Exit(status), ExitTrapKind.Expansion);
        var result = _state.FatalExpansion is { } fatal
            ? EvaluationResult.Exit(fatal.ExitCode) : EvaluationResult.DiscardCompoundRedirectionUnit();
        return ValueTask.FromResult(result);
    }

    private ValueTask<EvaluationResult> CompleteRedirectionFailureAsync(int status, ExpansionErrorKind kind, ScopedEvaluation scope)
    {
        if (kind == ExpansionErrorKind.HereDocumentParameter && _state.FatalExpansion.HasValue)
            return CompleteHereDocumentParameterFailureAsync(status, scope);
        if (_state.FatalExpansion is { Kind: ExpansionFailureKind.ArrayRead } failure)
            return RunExitTrapAsync(scope, EvaluationResult.Exit(failure.ExitCode), ExitTrapKind.Expansion);
        return new ValueTask<EvaluationResult>(FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated: false));
    }

    /// <summary>
    /// Evaluates a subshell command and returns its exit code to the caller.
    /// </summary>
    private async Task<EvaluationResult> EvaluateSubshellAsync(
        SubshellCommand subshellCommand,
        ScopedEvaluation scope,
        bool negated,
        IReadOnlyList<Redirection>? redirections)
    {
        PreparePipelineStatusStorage(1);
        var parentState = _state;
        var parentVars = _vars;
        var available = CollectDescriptorsAvailable();
        var borrowedDescriptors = available.GetIndex();
        var scopeCapacity = _subshellBorrowedDescriptorScopes.EnsureCapacity(0);
        if (_subshellBorrowedDescriptorScopes.Count == scopeCapacity)
        {
            var target = (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * scopeCapacity));
            var memory = GetDescriptorCleanupCapacity();
            memory.Grow(checked(32L + 8L * target));
            _subshellBorrowedDescriptorScopes.EnsureCapacity(target);
            memory.Resize(DescriptorCleanupMemoryBytes);
            memory.TrimExcess();
        }
        var (childVars, childContext) = scope.ForkBindings();
        var coordinates = childContext.Coordinates;
        if (coordinates.Locations?.TryGet(subshellCommand, out var location) == true)
            childContext = childContext.WithCoordinates(coordinates with { CompoundOpenLine = coordinates.Offset + location.EndLine });
        var childState = parentState.CloneForSubshell(Memory);
        var result = EvaluationResult.FromExitCode(0);
        OperationError? closeError = null;

        var savedDescriptorBytes = parentState.RetainedDescriptorMemoryBytes;
        var savedDirectoryStackBytes = parentState.RetainedDirectoryStackBytes;
        _savedDescriptorMemoryBytes = checked(_savedDescriptorMemoryBytes + savedDescriptorBytes);
        _savedDirectoryStackBytes = checked(_savedDirectoryStackBytes + savedDirectoryStackBytes);
        _state = childState;
        _vars = childVars;
        _subshellBorrowedDescriptorScopes.Push(borrowedDescriptors);
        Exception? failure = null;
        try
        {
            var childScope = scope with
            {
                Variables = childVars,
                Context = childContext.WithLoopDepth(0),
                ErrexitRedirections = null,
                Timing = null,
                InNestedCommandUnits = false
            };
            if (redirections != null)
            {
                // The child owns this frame through EXIT and descriptor cleanup below.
                _ = await OpenRedirectionScopeAsync(redirections, childScope,
                    RedirectionPersistence.Allocations, RedirectionLifetime.Command, RedirectionOrigin.Command,
                    reportMoveRestorationFailure: false)
                    .ConfigureAwait(false);
                if (_state.TryConsumeExpansionError(out var redirectionExitCode))
                    result = EvaluationResult.FromExitCode(redirectionExitCode);
            }

            if (result.ExitCode == 0)
            {
                result = await EvaluateCommandAsync(subshellCommand.Body, childScope, negated: false)
                    .ConfigureAwait(false);
                result = CompleteDiscardInChild(result);
                result = await RunExitTrapAsync(childScope, result).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var descriptorsToClose = CollectRedirectionDescriptorsToClose(borrowedDescriptors);

            try
            {
                closeError = await CloseDescriptorsAsync(descriptorsToClose,
                    operationFailed: failure != null || result.ExitCode != 0).ConfigureAwait(false);
            }
            finally
            {
                childState.ReleaseFunctions();
                _state = parentState;
                _savedDescriptorMemoryBytes -= savedDescriptorBytes;
                _savedDirectoryStackBytes -= savedDirectoryStackBytes;
                _vars = parentVars;
                _ = _subshellBorrowedDescriptorScopes.Pop();
            }
        }

        result = await ApplyCloseErrorAsync(result, closeError).ConfigureAwait(false);
        result = EvaluationResult.FromExitCode(result.ExitCode);

        scope.Variables.SetPipelineStatuses([result.ExitCode], scope.Context, _cancellationToken);
        return ApplyNegation(result, negated);
    }


    private static readonly Redirection[] PipeStandardErrorRedirections =
    [
        new(RedirectionKind.DuplicateOutput, 2, new Word([WordPart.FromText(WordPartKind.Unquoted, "1")]),
            string.Empty, hereDocumentDelimiter: null)
    ];

    /// <summary>Borrows a stage's parsed redirections and exposes the implicit alias after them.</summary>
    private sealed class PipelineStderrRedirections(IReadOnlyList<Redirection> explicitRedirections) : IReadOnlyList<Redirection>
    {
        public int Count => checked(explicitRedirections.Count + 1);

        public Redirection this[int index] => index == explicitRedirections.Count
            ? PipeStandardErrorRedirections[0] : explicitRedirections[index];

        public IEnumerator<Redirection> GetEnumerator()
        {
            for (var index = 0; index < explicitRedirections.Count; index++)
                yield return explicitRedirections[index];
            yield return PipeStandardErrorRedirections[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private bool IsPipelineStageCommand(ICommand command) =>
        _pipelineStageSource != null && _state.Script.Commands is [var stage] && ReferenceEquals(stage, command);

    /// <summary>
    /// Evaluates a pipeline by preparing pipe descriptors, running commands, and collecting exit codes.
    /// </summary>
    private async Task<EvaluationResult> EvaluatePipelineAsync(
        PipelineCommand pipelineCommand,
        ScopedEvaluation scope,
        bool negated)
    {
        CheckCancellation();
        // Only the left spine belongs to this pipeline. A compound right operand stays one stage.
        ICommand first = pipelineCommand;
        var stageCount = 1;
        while (first is PipelineCommand link)
        {
            CheckCancellation();
            stageCount = checked(stageCount + 1);
            first = link.Left;
        }
        // The parent owns these arrays and lists through dispatch, status publication and cleanup.
        var pipeCount = stageCount - 1;
        Memory.Charge(MemoryCapacity.ArrayBackingBytes<PipelineCommand>(pipeCount));
        PreparePipelineStatusStorage(stageCount);
        Memory.Charge(checked(192L + 128L * stageCount));
        var links = new PipelineCommand[pipeCount];
        var position = links.Length;
        for (ICommand remaining = pipelineCommand; remaining is PipelineCommand link; remaining = link.Left)
        {
            CheckCancellation();
            links[--position] = link;
        }
        var pipes = new List<PipeDescriptors>(links.Length);
        var stages = new List<PipelineEvaluationStage>(stageCount);
        using var pipelineMemory = Memory.Budget.CreateScope();
        var ownershipTransferred = false;
        try
        {
            for (var index = 0; index < links.Length; index++)
            {
                var creation = await ExecuteCreatePipeAsync().ConfigureAwait(false);
                if (creation.Descriptors is not { } pipe)
                {
                    await ReportOperationErrorAsync(creation.Error.GetValueOrDefault()).ConfigureAwait(false);
                    var exitCode = EvaluationRules.ApplyNegation(1, pipelineCommand.Negated ^ negated);
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated: false)
                        .ConfigureAwait(false);
                }
                pipes.Add(pipe);
                CheckCancellation();
            }

            // All stages borrow the same parent handles. Collect them before a stage retains
            // namespace owners so refusal cannot strand a partially constructed stage.
            var borrowedDescriptors = CollectDescriptorsAvailableToNestedEvaluation();
            var bindings = CaptureDescriptorBindings();
            for (var index = 0; index < stageCount; index++)
            {
                var stdIn = index > 0 ? new DescriptorBinding(pipes[index - 1].Read)
                    : bindings.ResolveBinding(LogicalDescriptor.StdIn);
                var stdOut = index < links.Length ? new DescriptorBinding(pipes[index].Write, IsWriteOnly: true)
                    : bindings.ResolveBinding(LogicalDescriptor.StdOut);
                var sourceCommand = index == 0 ? first : links[index - 1].Right;
                var command = index < links.Length && links[index].Operator == PipelineOperator.PipeAndError
                    ? AppendPipeStandardError(sourceCommand) : sourceCommand;
                var script = new ParsedScript([command]);
                var descriptors = new EvaluationDescriptorSetup(
                    bindings.WithStandardStreams(stdIn, stdOut, bindings.ResolveBinding(LogicalDescriptor.StdErr)), borrowedDescriptors);
                var forkKind = command is SimpleCommand ? ShellForkKind.PipelineSimpleCommand : ShellForkKind.Subshell;
                var (stageVars, stageContext) = scope.ForkBindings();
                var stageState = _state.CloneForNestedEvaluation(script, descriptors, forkKind, Memory);
                var stage = new Evaluation(
                    stageState,
                    stageVars,
                    descriptors,
                    Options with { CurrentDirectory = stageState.CurrentDirectory },
                    forkKind == ShellForkKind.Subshell ? stageContext.WithLoopDepth(0) : stageContext,
                    RootFlowHandling.Preserve,
                    publishNextStep: null,
                    onComplete: null,
                    Memory.Budget,
                    _preparedSyntaxRoot);
                FileDescriptor[] descriptorsToClose = index switch
                {
                    0 => [stdOut.Handle],
                    _ when index == links.Length => [stdIn.Handle],
                    _ => [stdIn.Handle, stdOut.Handle]
                };

                stage._retainCompletedMemory = true;
                stage._pipelineStageSource = sourceCommand;
                stage._loadedSyntaxFunctionDepth = scope.LoadedSyntaxFunctionDepth;
                stage._loadedReaderIgnoresErrexit = scope.LoadedReaderIgnoresErrexit;
                stage._pipelineUsesLoadedUnitStatus = forkKind == ShellForkKind.PipelineSimpleCommand
                    && (scope.InNestedCommandUnits || _state.ForkKind == ShellForkKind.CommandSubstitution
                        || PipelineInheritsLoadedUnitStatus);
                stage.Memory.Budget = pipelineMemory;
                stages.Add(new PipelineEvaluationStage(stage, descriptorsToClose));
            }

            var pipelineTask = RunPipelineEvaluations.AsTask(PublishOperation, stages);
            ownershipTransferred = true;
            Exception? pipelineFailure = null;
            try
            {
                // The host propagates cancellation to the stages. Await their cleanup before taking ownership.
                var results = await pipelineTask.ConfigureAwait(false);
                scope.Variables.SetPipelineStatuses(results, scope.Context, _cancellationToken);
                var exitCode = ComputePipelineExitCode(results);
                var pipelineNegated = pipelineCommand.Negated ^ negated;
                var result = EvaluationResult.FromExitCode(EvaluationRules.ApplyNegation(exitCode, pipelineNegated));
                return await FinalizeResultAsync(result, negated: false).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                pipelineFailure = error;
                throw;
            }
            finally
            {
                MemoryBudgetExceededException? transferRefusal = null;
                foreach (var stage in stages)
                {
                    var refusal = await stage.Evaluation.TransferDescriptorCleanupToAsync(this).ConfigureAwait(false);
                    transferRefusal ??= refusal;
                }
                if (pipelineFailure == null && transferRefusal != null)
                    ExceptionDispatchInfo.Capture(transferRefusal).Throw();
            }
        }
        finally
        {
            try
            {
                if (!ownershipTransferred)
                    await CloseCreatedPipesPreservingFailureAsync().ConfigureAwait(false);
            }
            finally
            {
                foreach (var stage in stages)
                    stage.Evaluation.ReleaseFunctions();
            }
        }

        ICommand AppendPipeStandardError(ICommand command)
        {
            // 2>&1 runs after the stage's explicit redirections. Keep the resulting
            // alias in that redirection frame so inner commands can override it.
            Memory.Charge(96);
            return command switch
            {
                SimpleCommand simple => simple with { Redirections = Append(simple.Redirections) },
                RedirectedCommand redirected => redirected with { Redirections = Append(redirected.Redirections) },
                _ => new RedirectedCommand(command, PipeStandardErrorRedirections)
            };

            IReadOnlyList<Redirection> Append(IReadOnlyList<Redirection> redirections)
            {
                if (redirections.Count == 0)
                    return PipeStandardErrorRedirections;
                Memory.Charge(32);
                return new PipelineStderrRedirections(redirections);
            }
        }

        async Task CloseCreatedPipesPreservingFailureAsync()
        {
            for (var index = pipes.Count - 1; index >= 0; index--)
            {
                await TryCloseAsync(pipes[index].Write).ConfigureAwait(false);
                await TryCloseAsync(pipes[index].Read).ConfigureAwait(false);
            }

            async Task TryCloseAsync(FileDescriptor descriptor)
            {
                try
                {
                    await CloseOwnedDescriptorAsync(descriptor).ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the setup failure while still attempting every owned endpoint.
                }
            }
        }

        static int ComputePipelineExitCode(IReadOnlyList<int> results)
        {
            for (var index = results.Count - 1; index >= 0; index--)
            {
                if (results[index] != 0)
                    return results[index];
            }

            return 0;
        }
    }

    /// <summary>
    /// Evaluates a simple command, including assignments, builtins, functions, and external invocations.
    /// </summary>
    private async Task<EvaluationResult> EvaluateSimpleCommandAsync(
        SimpleCommand simpleCommand,
        ScopedEvaluation scope)
    {
        var directPipelineStage = IsPipelineStageCommand(simpleCommand);
        var reachedCommand = simpleCommand.Words.Count != 0 || simpleCommand.Assignments.Count != 0
            || simpleCommand.Redirections.Count != 0;
        var publishStatus = reachedCommand;
        if (publishStatus)
            PreparePipelineStatusStorage(1);
        _state.LastCommandSubstitutionExitCode = null;
        PreparedDeclaration? declaration = null;
        if (simpleCommand.DeclarationAssignments.Count > 0)
        {
            declaration = await AwaitWordExpansionAsync(PrepareDeclarationAsync(
                simpleCommand, new CommandWords(simpleCommand.Words), scope, captureCommandWords: true))
                .ConfigureAwait(false);
            simpleCommand = declaration.Command;
        }
        var commandWords = await ExpandCommandNameAsync(simpleCommand.Words).ConfigureAwait(false);
        if (_state.HasPendingExpansionError)
            return PublishStatus(await FinalizeResultAsync(EvaluationResult.FromExitCode(1), false).ConfigureAwait(false));

        if (commandWords.Count == 0)
        {
            _state.PreparationDiagnosticName = default;
            // Assignments run in the enclosing descriptor scope, before any command-local redirections.
            var exitCode = 0;
            foreach (var assignment in simpleCommand.Assignments)
            {
                var applied = await AwaitWordExpansionAsync(ApplyAssignmentAsync(assignment)).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                {
                    PublishAssignmentTargetFailure(scope);
                    return PublishStatus(await FinalizeResultAsync(EvaluationResult.FromExitCode(1), false).ConfigureAwait(false));
                }
                if (!applied)
                    exitCode = 1;
            }

            var isolateRedirections = simpleCommand.Redirections.Any(RequiresCommandlessRedirectionIsolation);
            var assignmentRedirections = isolateRedirections ? IsolateRedirectionBindings() : scope;
            var redirectionScope = await OpenRedirectionScopeAsync(simpleCommand.Redirections, assignmentRedirections,
                isolateRedirections ? RedirectionPersistence.None : RedirectionPersistence.AllocationsAndMoves,
                RedirectionLifetime.Command,
                isolateRedirections ? RedirectionOrigin.IsolatedCommandless : RedirectionOrigin.Command,
                reportMoveRestorationFailure: true)
                .ConfigureAwait(false);
            Exception? assignmentFailure = null;
            var assignmentResult = EvaluationResult.FromExitCode(exitCode);
            OperationError? assignmentCloseError = null;
            bool redirectionFailed;
            try
            {
                // An isolated redirection produces its own status, including a local expansion failure.
                if (isolateRedirections)
                    _state.FatalExpansion = null;
                redirectionFailed = _state.TryConsumeExpansionError(out var assignmentRedirectionExitCode, out var errorKind);
                if (redirectionFailed)
                    assignmentResult = await CompleteRedirectionFailureAsync(assignmentRedirectionExitCode, errorKind, scope)
                        .ConfigureAwait(false);
                else
                {
                    if (!isolateRedirections && exitCode == 0)
                        exitCode = _state.LastCommandSubstitutionExitCode ?? 0;
                    assignmentResult = await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated: false).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                assignmentFailure = ex;
                throw;
            }
            finally
            {
                assignmentCloseError = await redirectionScope.CloseAsync(
                    assignmentFailure != null || assignmentResult.ExitCode != 0).ConfigureAwait(false);
            }
            // A commandless pipeline stage publishes its assignment status after isolated
            // redirection setup. Cleanup still sees the actual setup failure.
            if (redirectionFailed && isolateRedirections && directPipelineStage)
                assignmentResult = assignmentResult.WithExitCode(exitCode);
            return await CompleteSimpleCommandAsync(assignmentResult, assignmentCloseError, ShellValue.Empty)
                .ConfigureAwait(false);
        }

        var commandValue = await ExpandWordValueAsync(commandWords[0], scope).ConfigureAwait(false);
        var commandName = string.Empty;
        if (_state.TryConsumeExpansionError(out var nameError))
            return PublishStatus(await FinalizeResultAsync(EvaluationResult.FromExitCode(nameError), false).ConfigureAwait(false));

        var preparation = await AwaitWordExpansionAsync(PrepareCommandAsync(commandWords, commandValue, scope)).ConfigureAwait(false);
        using var functionBorrow = new FunctionBinding.Borrow(preparation is PreparedCommand.Executable
            { Resolution: CommandResolution.Function selectedFunction } ? selectedFunction.Binding : null);
        if (_state.TryConsumeExpansionError(out var preparationError))
            return PublishStatus(await FailPreparationAsync(preparationError).ConfigureAwait(false));

        // Expand once in the caller scope before redirections and temporary bindings.
        IReadOnlyList<ShellValue> byteOperands = [];
        var pwdOptions = new PwdOptions();
        var lastArgument = commandValue;
        if (preparation is PreparedCommand.Executable command)
        {
            lastArgument = command.CommandValue;
            var kind = (command.Resolution as CommandResolution.EvaluatorBuiltin)?.Kind;
            if (kind is BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local)
            {
                declaration ??= await AwaitWordExpansionAsync(PrepareDeclarationAsync(
                    simpleCommand, command.Words, scope, captureCommandWords: false)).ConfigureAwait(false);
                if (_state.TryConsumeExpansionError(out var declarationError))
                    return PublishStatus(await FailPreparationAsync(declarationError).ConfigureAwait(false));

                lastArgument = declaration.LastArgument ?? lastArgument;
            }
            else if (kind is null or BuiltinKind.Printf or BuiltinKind.Echo or BuiltinKind.Hash or BuiltinKind.Type or BuiltinKind.Help
                or BuiltinKind.Set or BuiltinKind.Source or BuiltinKind.Getopts or BuiltinKind.Eval or BuiltinKind.Let
                or BuiltinKind.Read or BuiltinKind.Mapfile or BuiltinKind.Unset or BuiltinKind.Return or BuiltinKind.Trap or BuiltinKind.Test
                or BuiltinKind.Exit or BuiltinKind.Break or BuiltinKind.Continue or BuiltinKind.Cd or BuiltinKind.Shift
                or BuiltinKind.Dirs or BuiltinKind.Pushd or BuiltinKind.Popd or BuiltinKind.Caller)
            {
                var operands = new List<ShellValue>();
                if (kind is not null and not BuiltinKind.Printf and not BuiltinKind.Echo)
                    AddOperand(operands, command.CommandValue);
                var words = command.Words;
                for (var index = 1; index < words.Count; index++)
                {
                    await AppendOperandValuesAsync(words[index], scope, operands).ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        break;
                }
                byteOperands = operands;
                if (operands.Count != 0)
                    lastArgument = operands[^1];
            }
            else
            {
                await ExpandArgumentTailAsync(command.Words, 1,
                    scanPwdOptions: command.Resolution is CommandResolution.EvaluatorBuiltin { Kind: BuiltinKind.Pwd })
                    .ConfigureAwait(false);
            }
        }
        else if (preparation is PreparedCommand.Terminal terminalArguments)
        {
            lastArgument = terminalArguments.LastReadArgument;
            await ExpandArgumentTailAsync(terminalArguments.Words, 0, scanPwdOptions: false).ConfigureAwait(false);
        }
        else if (preparation is PreparedCommand.Query queryArguments)
            lastArgument = queryArguments.LastReadArgument;

        if (_state.TryConsumeExpansionError(out var argumentError))
            return PublishStatus(await FailPreparationAsync(argumentError).ConfigureAwait(false));

        // Prepare echo output before prefix assignments and redirections can cause effects.
        ReadOnlyMemory<byte> echoContent = default;
        using var echoMemory = preparation is PreparedCommand.Executable
            { Resolution: CommandResolution.EvaluatorBuiltin { Kind: BuiltinKind.Echo } }
            ? Memory.Budget.CreateScope() : null;
        if (echoMemory != null)
        {
            try
            {
                echoContent = EchoFormatting.Format(byteOperands, echoMemory);
            }
            catch (MemoryBudgetExceededException)
            {
                await ReportMemoryRefusalAsync("echo").ConfigureAwait(false);
                return PublishStatus(await FailPreparationAsync(1).ConfigureAwait(false));
            }

        }

        var commandVars = scope.Variables;
        var commandAssignmentsSuccessful = true;
        var prefixNames = simpleCommand.Assignments.Count == 0 ? null : new HashSet<BindingName>();
        var assignmentError = await AwaitWordExpansionAsync(PrepareAssignmentsAsync()).ConfigureAwait(false);
        if (assignmentError.HasValue)
            return PublishStatus(await FailPreparationAsync(assignmentError.Value).ConfigureAwait(false));

        // In-process redirections write both caller bindings and the prepared command scope.
        // Do not infer writes by comparing values: assigning the caller's old value still replaces a prefix.
        var redirectionExpansionScope = scope;
        // exec keeps redirections in the current shell, including their writes and fatal expansion state.
        var externalRedirections = simpleCommand.Redirections.Count > 0
            && preparation is PreparedCommand.Executable
                { Resolution: CommandResolution.Host, Completion: CommandCompletion.Continue };
        if (externalRedirections)
        {
            redirectionExpansionScope = IsolateRedirectionBindings();
        }
        else if (simpleCommand.Redirections.Count > 0 && prefixNames != null)
        {
            redirectionExpansionScope = scope with
            {
                ExpansionWriteTarget = new ScopedEvaluation.ExpansionBindings(commandVars, prefixNames)
            };
        }
        using var diagnosticName = new EvaluationState.CommandDiagnosticScope(_state,
            preparation is PreparedCommand.Executable invocation ? invocation.CommandValue : commandValue);
        var commandRedirections = await OpenRedirectionScopeAsync(
                simpleCommand.Redirections, redirectionExpansionScope,
                externalRedirections ? RedirectionPersistence.None : RedirectionPersistence.AllocationsAndMoves,
                preparation.RedirectionLifetime, RedirectionOrigin.Command,
                reportMoveRestorationFailure: !externalRedirections && !directPipelineStage)
            .ConfigureAwait(false);
        Exception? scopeFailure = null;
        FunctionBinding[]? exportOwners = null;
        var retainedExportCount = 0;
        var preparedResult = EvaluationResult.FromExitCode(0);
        OperationError? commandCloseError = null;
        try
        {
            preparedResult = await ExecuteWithRedirectionsAsync().ConfigureAwait(false);
        }
        catch (MemoryBudgetExceededException ex)
        {
            scopeFailure = ex;
            await ReportMemoryRefusalAsync(commandName).ConfigureAwait(false);
            preparedResult = await FinalizeResultAsync(EvaluationResult.FromExitCode(1), negated: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scopeFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                commandCloseError = await commandRedirections.CloseAsync(
                    scopeFailure != null || preparedResult.ExitCode != 0).ConfigureAwait(false);
            }
            finally
            {
                if (exportOwners is { } owners)
                    for (var index = 0; index < retainedExportCount; index++)
                        owners[index].Release();
            }
        }
        return await CompleteSimpleCommandAsync(preparedResult, commandCloseError, lastArgument).ConfigureAwait(false);

        static bool RequiresCommandlessRedirectionIsolation(Redirection redirection) =>
            redirection.VariableName.Length != 0
            || (redirection.FileDescriptor ?? redirection.DefaultFileDescriptor) == 0
                && (redirection.Kind is RedirectionKind.Input or RedirectionKind.InputOutput
                        or RedirectionKind.CloseInput or RedirectionKind.CloseOutput
                    || redirection.Kind is RedirectionKind.DuplicateInput or RedirectionKind.DuplicateOutput
                        && !redirection.TryGetLiteralDescriptor(out _));

        ScopedEvaluation IsolateRedirectionBindings()
        {
            foreach (var redirection in simpleCommand.Redirections)
            {
                // Include generated and apparently read-only syntax. Literal targets need no variable owner.
                if (redirection.VariableName.Length != 0 || redirection.Target.Parts.Any(static part =>
                    part is CommandSubstitutionWordPart || part is SourceWordPart source
                    && (source.Kind == WordPartKind.Unquoted && source.Source.AsSpan().Contains((byte)'{')
                        || source.Source.AsSpan().IndexOfAny((byte)'$', (byte)'`') >= 0)))
                {
                    var (variables, context) = scope.Context.ForkForRedirections(scope.Variables, Memory, _cancellationToken);
                    return scope with { Variables = variables, Context = context, ExpansionWriteTarget = null, SelectorBindings = null };
                }
            }
            return scope;
        }

        async Task<EvaluationResult> CompleteSimpleCommandAsync(EvaluationResult result, OperationError? closeError, ShellValue last)
        {
            result = await ApplyCloseErrorAsync(result, closeError).ConfigureAwait(false);
            if (reachedCommand && result.Flow != FlowControlKind.Exit)
            {
                CheckCancellation();
                await BindLastArgumentAsync(last).ConfigureAwait(false);
                if (_state.TryConsumeExpansionError(out var status))
                    result = await FinalizeResultAsync(EvaluationResult.FromExitCode(status), negated: false).ConfigureAwait(false);
            }
            return PublishStatus(result);
        }

        async Task BindLastArgumentAsync(ShellValue value)
        {
            var variables = scope.Variables;
            var name = global::Lokad.Bash.Evaluation.Variables.LastArgumentName;
            if (variables.IsReadonly(name))
            {
                var subject = !variables.IsReadonlyBinding(name)
                    && variables.TryResolveBinding(name, selectOwner: null, out var binding) ? binding.Name : name;
                await ReportExpansionDiagnosticAsync(GetDiagnosticName(subject), ShellValue.FromText("readonly variable"))
                    .ConfigureAwait(false);
                return;
            }

            // The retained value can outlive the command unit and its syntax owner.
            if (!value.IsEmpty && !value.TryGetCompactArrayCapacity(out _))
            {
                Memory.Charge(MemoryCapacity.ArrayBackingBytes<byte>(value.Bytes.Length));
                value = ShellValue.FromBytes(value.Bytes.ToArray());
            }
            Memory.PrepareRetainedStorage(160 + global::Lokad.Bash.Evaluation.Variables.EstimateValueBytes(value));
            if (variables.TrySetLastArgumentScalar(value))
                return;

            var target = await WordExpansion.ResolveBuiltinReferenceAsync(
                VariableReference.ForName(name), scope, variables, variables).ConfigureAwait(false);
            if (target != null && await target.AssignValueAsync(scope, name, value, AssignmentOperator.Replace)
                    .ConfigureAwait(false) is not null)
                variables.SetExported(target.Name, false);
        }

        EvaluationResult PublishStatus(EvaluationResult result)
        {
            if (publishStatus && result.Flow != FlowControlKind.Exit)
                scope.Variables.SetPipelineStatuses([result.ExitCode], scope.Context, _cancellationToken);
            return result;
        }

        async Task<EvaluationResult> ExecuteWithRedirectionsAsync()
        {
            // Exit only the child; the pending error still prevents invoking the command.
            if (externalRedirections)
                _state.FatalExpansion = null;
            if (_state.TryConsumeExpansionError(out var redirectionError, out var errorKind))
            {
                // A failed redirection never enters exec, so it neither replaces the shell
                // nor suppresses timing. Fatal expansion errors still terminate the evaluator.
                return await CompleteRedirectionFailureAsync(redirectionError, errorKind, scope).ConfigureAwait(false);
            }

            if (preparation.RedirectionLifetime == RedirectionLifetime.Evaluation
                && await commandRedirections.PreserveAsync().ConfigureAwait(false) is { } preserveError)
                return await ApplyCloseErrorAsync(EvaluationResult.FromExitCode(0), preserveError).ConfigureAwait(false);

            if (!commandAssignmentsSuccessful)
                return await FailPreparationAsync(1).ConfigureAwait(false);

            var hasStatusPrefix = prefixNames?.Contains(global::Lokad.Bash.Evaluation.Variables.PipelineStatusName) == true;
            if (preparation is PreparedCommand.Query query)
            {
                publishStatus &= !hasStatusPrefix;
                var queryScope = scope with { Variables = commandVars };
                if (query.ExecutionOptions is { } queryOptions)
                {
                    var queryName = PrepareExecArgumentZero(queryOptions, "command");
                    queryScope = ForkExecScope(queryScope, queryName);
                }
                using var clearedExports = query.ExecutionOptions is { ClearEnvironment: true }
                    ? ClearExecExports(queryScope, queryScope.Variables) : null;
                var queryExitCode = await EvaluateCommandQueryAsync(
                        BuiltinKind.Command,
                        query.Arguments,
                        firstName: 0,
                        query.OutputMode,
                        CommandLookupMode.Normal,
                        listAll: false,
                        queryScope.Variables,
                        queryScope.Context)
                    .ConfigureAwait(false);
                var queryResult = await FinalizeResultAsync(EvaluationResult.FromExitCode(queryExitCode), false)
                    .ConfigureAwait(false);
                clearedExports?.Commit();
                return CompleteCommandResult(queryResult, query.Completion, commandInvoked: true);
            }

            if (preparation is PreparedCommand.Terminal terminal)
            {
                publishStatus &= !hasStatusPrefix;
                // Argument/prefix expansion and redirections can supersede a prepared failure.
                if (terminal.Help is { } help)
                {
                    var helpResult = await WriteBuiltinHelpUnlessStdoutClosedAsync(help.Name, help.Kind, help.CommandName,
                        BuiltinHelpOutput.Prepared, negated: false).ConfigureAwait(false);
                    return CompleteCommandResult(helpResult, terminal.Completion, commandInvoked: true);
                }
                if (!terminal.Message.IsEmpty)
                    await ExecuteWriteAsync(GetStdErrDescriptor(), terminal.Message.Bytes).ConfigureAwait(false);
                var terminalResult = await FinalizeResultAsync(terminal.Result, false).ConfigureAwait(false);
                return CompleteCommandResult(terminalResult, terminal.Completion, commandInvoked: true);
            }

            var executable = (PreparedCommand.Executable)preparation;
            commandName = executable.CommandName;
            // A failed redirection must not change exports or $0.
            ShellValue? argumentZero = executable.ExecutionOptions is { } executionOptions
                ? PrepareExecArgumentZero(executionOptions, commandName) : null;
            var invocationScope = commandRedirections is { IsActive: true, HasRestorableMappings: true }
                ? scope with { ErrexitRedirections = commandRedirections }
                : scope;
            if (executable.Resolution is CommandResolution.Function function)
            {
                var functionResult = await EvaluateFunctionAsync(
                        function.Binding,
                        byteOperands,
                        invocationScope,
                        commandVars,
                        prefixNames,
                        false)
                    .ConfigureAwait(false);
                return CompleteCommandResult(functionResult, executable.Completion, commandInvoked: true);
            }

            if (executable.Resolution is CommandResolution.EvaluatorBuiltin evaluatorBuiltin)
            {
                // These builtins publish after their temporary environment has retired.
                // Other builtins see the temporary scalar when selecting the status array.
                publishStatus &= !hasStatusPrefix || evaluatorBuiltin.Kind is BuiltinKind.Eval or BuiltinKind.Source
                    or BuiltinKind.Unset or BuiltinKind.Read or BuiltinKind.Mapfile;
                var builtinScope = invocationScope;
                if (argumentZero is { } builtinName)
                {
                    // Fork before installing temporary bindings so builtin writes cannot reach the caller's frames.
                    builtinScope = ForkExecScope(invocationScope, builtinName);
                    if (ReferenceEquals(commandVars, scope.Variables))
                        commandVars = builtinScope.Variables;
                    else
                        commandVars.ArgumentZero = builtinName;
                }

                // local/declare already publish declarations through the owning function's LocalBindings.
                using var bindings = prefixNames == null
                    || (builtinScope.Context.FunctionContext != null
                        && evaluatorBuiltin.Kind is BuiltinKind.Local or BuiltinKind.Declare)
                    ? null
                    : new Variables.CommandBindings(builtinScope.Variables, commandVars, prefixNames, Memory, _cancellationToken);
                if (bindings != null)
                    commandVars = builtinScope.Variables;

                var enclosingFunction = builtinScope.Context.FunctionContext;
                using var scriptFunction = enclosingFunction != null && bindings != null
                    && evaluatorBuiltin.Kind is BuiltinKind.Eval or BuiltinKind.Source
                    ? new FunctionContext(enclosingFunction.FunctionName,
                        enclosingFunction.Locals.WithTemporaryPrefixes(bindings), enclosingFunction)
                    : null;
                if (scriptFunction != null)
                {
                    builtinScope = builtinScope with
                    {
                        Context = new EvaluationContext(builtinScope.Context.LoopDepth, scriptFunction, builtinScope.Context.ReturnTarget)
                        {
                            GlobalPrefixes = builtinScope.Context.GlobalPrefixes,
                            CallerSource = builtinScope.Context.CallerSource,
                            ErrexitIgnored = builtinScope.Context.ErrexitIgnored,
                            SourceMemory = builtinScope.Context.SourceMemory,
                            Coordinates = builtinScope.Context.Coordinates
                        }
                    };
                }

                if (bindings != null && prefixNames != null
                    && evaluatorBuiltin.Kind is BuiltinKind.Eval or BuiltinKind.Source)
                {
                    builtinScope = builtinScope with
                    {
                        Context = new EvaluationContext(builtinScope.Context.LoopDepth, builtinScope.Context.FunctionContext, builtinScope.Context.ReturnTarget)
                        {
                            ErrexitIgnored = builtinScope.Context.ErrexitIgnored,
                            SourceMemory = builtinScope.Context.SourceMemory,
                            CallerSource = builtinScope.Context.CallerSource,
                            Coordinates = builtinScope.Context.Coordinates,
                            GlobalPrefixes = new GlobalPrefixBindings(bindings,
                                builtinScope.Context.FunctionContext?.Depth ?? 0, builtinScope.Context.GlobalPrefixes)
                        }
                    };
                }

                using var clearedExports = executable.ExecutionOptions is { ClearEnvironment: true }
                    ? ClearExecExports(builtinScope, commandVars) : null;

                var builtinResult = evaluatorBuiltin.Kind switch
                {
                    BuiltinKind.Colon or BuiltinKind.True or BuiltinKind.False => await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(evaluatorBuiltin.Kind == BuiltinKind.False ? 1 : 0), false)
                        .ConfigureAwait(false),
                    BuiltinKind.Break or BuiltinKind.Continue => await EvaluateBuiltinLoopControlAsync(
                        evaluatorBuiltin.Kind, byteOperands, builtinScope, false).ConfigureAwait(false),
                    BuiltinKind.Exit => await EvaluateBuiltinExitAsync(byteOperands, builtinScope, executable.Completion).ConfigureAwait(false),
                    BuiltinKind.Test => await EvaluateBuiltinTestAsync(commandName, byteOperands,
                        builtinScope with { Variables = commandVars, SelectorBindings = bindings, AfterPipelineExitReset = true },
                        false).ConfigureAwait(false),
                    BuiltinKind.Echo => await EvaluateBuiltinEchoAsync(echoContent, false).ConfigureAwait(false),
                    BuiltinKind.Type => await EvaluateBuiltinTypeAsync(byteOperands, commandVars, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Help => await AwaitWordExpansionAsync(EvaluateBuiltinHelpAsync(byteOperands, builtinScope.Context, false)).ConfigureAwait(false),
                    BuiltinKind.Printf => await AwaitWordExpansionAsync(EvaluateBuiltinPrintfAsync(
                        byteOperands, commandVars, bindings, builtinScope.Context, false)).ConfigureAwait(false),
                    BuiltinKind.Read => await AwaitWordExpansionAsync(EvaluateBuiltinReadAsync(
                        byteOperands, new ScopedEvaluation(this, commandVars, builtinScope.Context), bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Mapfile => await AwaitWordExpansionAsync(EvaluateBuiltinMapfileAsync(
                        byteOperands, builtinScope with { Variables = commandVars }, false))
                        .ConfigureAwait(false),
                    BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local =>
                        await AwaitWordExpansionAsync(declaration is { } prepared
                            ? EvaluateBuiltinDeclarationAsync(prepared.Options, prepared.Operands, commandVars, bindings, builtinScope, false)
                            : throw new InvalidOperationException("Declaration was not prepared.")).ConfigureAwait(false),
                    BuiltinKind.Set when byteOperands.Count == 1 => await AwaitWordExpansionAsync(EvaluateBuiltinDeclarationAsync(
                        new DeclarationOptions(BuiltinKind.Set), [], commandVars, bindings, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Set => await EvaluateBuiltinSetAsync(byteOperands, commandVars, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Unset => await AwaitWordExpansionAsync(EvaluateBuiltinUnsetAsync(byteOperands, commandVars, builtinScope, bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Eval or BuiltinKind.Source => await EvaluateBuiltinScriptingAsync(evaluatorBuiltin.Kind,
                        commandName, byteOperands, builtinScope with { Variables = commandVars }, false).ConfigureAwait(false),
                    BuiltinKind.Trap => await AwaitWordExpansionAsync(EvaluateBuiltinTrapAsync(byteOperands, builtinScope.Context))
                        .ConfigureAwait(false),
                    BuiltinKind.Return => await AwaitWordExpansionAsync(
                        EvaluateBuiltinReturnAsync(byteOperands, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Let => await EvaluateBuiltinLetAsync(byteOperands, commandVars, bindings, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Getopts => await AwaitWordExpansionAsync(EvaluateBuiltinGetoptsAsync(
                        byteOperands, builtinScope with { Variables = commandVars }, bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Cd => await EvaluateBuiltinCdAsync(byteOperands, commandVars, bindings, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Pwd => await EvaluateBuiltinPwdAsync(executable.CommandValue, pwdOptions, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Dirs => await EvaluateBuiltinDirsAsync(byteOperands, commandVars, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Pushd => await EvaluateBuiltinPushdAsync(byteOperands, commandVars, bindings, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Popd => await EvaluateBuiltinPopdAsync(byteOperands, commandVars, bindings, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Caller => await EvaluateBuiltinCallerAsync(byteOperands, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Shift => await AwaitWordExpansionAsync(EvaluateBuiltinShiftAsync(
                        byteOperands, builtinScope with { Variables = commandVars }, false)).ConfigureAwait(false),
                    BuiltinKind.Hash => await EvaluateBuiltinHashAsync(byteOperands).ConfigureAwait(false),
                    _ => throw new InvalidOperationException($"Builtin '{commandName}' has no evaluator dispatch.")
                };
                if (scriptFunction != null)
                    CompletePromotions(scriptFunction, enclosingFunction, commandVars, bindings);
                clearedExports?.Commit();
                return CompleteCommandResult(builtinResult, executable.Completion, commandInvoked: true);
            }

            if (!TryBuildFunctionExports(out var functions))
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("exported function limit exceeded"))
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), false).ConfigureAwait(false);
            }
            var inheritedDescriptors = CollectDescriptorsAvailable();
            var commandInvocation = BashCommandInheritance.CreateInheritedCommand(
                commandName,
                argumentZero,
                byteOperands,
                _state.CurrentDirectory,
                executable.ExecutionOptions is { ClearEnvironment: true }
                    ? [] : BuildExportedEnvironment(commandVars, scope.Context, Memory.Workspace),
                GetStdInDescriptor(),
                GetStdOutDescriptor(),
                GetStdErrDescriptor(),
                ref inheritedDescriptors,
                CaptureDescriptorBindings().Bindings,
                functions);
            var execution = await ExecuteRunCommandAsync(commandInvocation, invocationScope,
                executable.Completion == CommandCompletion.ExitEvaluation).ConfigureAwait(false);
            var commandResult = await FinalizeResultAsync(
                    EvaluationResult.FromExitCode(execution.ShellExitCode),
                    false)
                .ConfigureAwait(false);
            // Name lookup fails before exec resets traps; explicit paths reach the exec attempt with traps reset.
            if (executable.Completion == CommandCompletion.ExitEvaluation && !commandName.Contains('/')
                && execution is CommandExecutionResult.NotFound or CommandExecutionResult.NotExecutable)
            {
                using var bindings = prefixNames == null ? null
                    : new Variables.CommandBindings(scope.Variables, commandVars, prefixNames, Memory, _cancellationToken);
                commandResult = await RunExitTrapAsync(scope, EvaluationResult.Exit(commandResult.ExitCode)).ConfigureAwait(false);
            }
            return CompleteCommandResult(commandResult, executable.Completion, commandInvoked: true);
        }

        // Capture the first surviving command field, retaining other fields without replaying their expansion.
        async Task<CommandWords> ExpandCommandNameAsync(IReadOnlyList<Word> source)
        {
            // A prepared field can stay in its original word unless pathname expansion can replace it.
            if (source.Count == 0 || GetLiteralWithoutGlob(source[0]) != null)
                return new CommandWords(source);

            using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
            var fields = new List<ShellValue>();
            for (var index = 0; index < source.Count; index++)
            {
                await AppendOperandValuesAsync(source[index], scope, fields, fieldMemory).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    break;
                if (fields.Count == 0)
                    continue;
                return new CommandWords(source, index + 1, CaptureLiteralWords(fields, start: 0, first: null));
            }
            return new CommandWords([]);
        }

        ShellValue PrepareExecArgumentZero(ExecOptions options, string defaultName)
        {
            var name = options.ArgumentZero ?? ShellValue.FromText(defaultName);
            if (!options.Login)
                return name;
            Memory.Charge(checked(4L * (1L + name.Bytes.Length)));
            return ShellValue.FromBytes(ByteMemoryBuilder.CreateUnaccounted(name.Bytes.Length + 1)
                .Append((byte)'-').Append(name.Bytes).ToMemory());
        }

        ScopedEvaluation ForkExecScope(ScopedEvaluation source, ShellValue name)
        {
            var (variables, context) = source.ForkBindings();
            variables.ArgumentZero = name;
            return source with { Variables = variables, Context = context };
        }

        EvaluationState.FunctionExportClear? ClearExecExports(ScopedEvaluation executionScope, Variables commandVariables)
        {
            // Prepare the fork's visible and saved bindings before updating the function namespace.
            ClearExports(commandVariables);
            if (!ReferenceEquals(commandVariables, executionScope.Variables))
                ClearExports(executionScope.Variables);
            for (var prefix = executionScope.Context.GlobalPrefixes; prefix != null; prefix = prefix.Enclosing)
                ClearExports(prefix.Saved);
            for (var frame = executionScope.Context.FunctionContext; frame != null; frame = frame.Enclosing)
            {
                if (frame.Locals.SavedBindings is { } locals)
                    ClearExports(locals);
                if (frame.PrefixBindings is { } functionBindings)
                    ClearExports(functionBindings.SavedVariables);
            }

            return _state.BeginClearFunctionExports(Memory, _cancellationToken);

            void ClearExports(Variables variables) => variables.ClearExportAttributes(Memory, _cancellationToken);
        }

        bool TryBuildFunctionExports(out IReadOnlyList<ExportedFunction> exports)
        {
            exports = [];
            if (preparation is PreparedCommand.Executable { ExecutionOptions.ClearEnvironment: true })
                return true;
            var selected = _state.GetExportedFunctions(Memory);
            if (selected.IsEmpty)
                return true;
            Memory.Charge(checked(MemoryCapacity.ArrayBackingBytes<ExportedFunction>(selected.Length)
                + MemoryCapacity.ArrayBackingBytes<FunctionBinding>(selected.Length)));
            var result = new ExportedFunction[selected.Length];
            var owners = new FunctionBinding[selected.Length];
            exportOwners = owners;
            var remaining = ExportedFunction.MaximumSourceBytes;
            for (var index = 0; index < selected.Length; index++)
            {
                var function = selected[index];
                function.Retain();
                owners[retainedExportCount++] = function;
                if (!function.TryGetExport(remaining, out result[index])) return false;
                remaining -= result[index].Source.Length;
            }
            exports = result;
            return true;
        }

        async Task<int?> PrepareAssignmentsAsync()
        {
            if (prefixNames == null)
                return null;

            // Copy reached bindings only; actual RHS writes still use the existing caller target.
            commandVars = global::Lokad.Bash.Evaluation.Variables.CreateCommandScope(scope.Variables, Memory, _cancellationToken);
            var assignmentScope = scope with
            {
                Variables = commandVars,
                ExpansionWriteTarget = new ScopedEvaluation.ExpansionBindings(scope.Variables, prefixNames)
                    { PreparingPrefixes = true }
            };
            foreach (var assignment in simpleCommand.Assignments)
            {
                // Element prefixes are invalid identifiers, even before expanding their operands.
                if (assignment.Target.AsSpan().Contains((byte)'['))
                {
                    await ReportInvalidElementPrefixAsync(assignment.Target, assignmentScope).ConfigureAwait(false);
                    continue;
                }

                var target = await DecodeExpandedValueAsync(assignment.Target.ToValue())
                    .ConfigureAwait(false);
                if (_state.TryConsumeExpansionError(out var error))
                    return error;

                // Select before RHS effects: unset and element namerefs are shadowed, not followed.
                var resolvedPrefix = commandVars.TryResolveBinding(target, selectOwner: null, out var prefixBinding);
                var circularPrefix = !resolvedPrefix;
                if (circularPrefix)
                    await ReportCircularNameReferenceAsync(target).ConfigureAwait(false);
                var elementNameref = resolvedPrefix && prefixBinding.Reference.Kind != VariableReferenceKind.Name;
                var unsetNameref = resolvedPrefix && prefixBinding.IsUnsetNameref;
                BindingName selectedName = resolvedPrefix ? prefixBinding.Name : target;
                if (elementNameref && commandVars.TryResolveBinding(target, selectOwner: null,
                        BindingResolution.LastNameref, out var elementAlias))
                    selectedName = elementAlias.Name;
                // Readonly belongs to the selected binding, not a reference followed to reach it.
                if (commandVars.IsReadonlyBinding(selectedName))
                {
                    await ReportCommandDiagnosticAsync([assignment.Target.ToValue()],
                        [ShellValue.FromText("readonly variable")], scope).ConfigureAwait(false);
                    continue;
                }

                ShellValue value;
                if (assignment.ArrayValues is { } compoundValues)
                {
                    // A compound prefix is scalar parenthesized text, unlike an assignment-only array.
                    value = await ExpandCompoundPrefixAsync(compoundValues).ConfigureAwait(false);
                }
                else
                {
                    value = await WordExpansion.ExpandAssignmentValueAsync(assignment.Value, assignmentScope).ConfigureAwait(false);
                }

                if (_state.TryConsumeExpansionError(out error))
                    return error;

                BindingName name = elementNameref || unsetNameref ? target : selectedName;
                if (elementNameref || unsetNameref || circularPrefix)
                {
                    if (assignment.Operator == AssignmentOperator.Append)
                    {
                        // += reads the selected binding after RHS effects, without following a newly initialized nameref.
                        var previous = elementNameref ? prefixBinding.Reference.ToValue(Memory)
                            : commandVars.TryGetNameref(prefixBinding.Name, out var reference) ? reference : ShellValue.Empty;
                        value = WordExpansion.AppendAssignmentValue(previous, value, assignmentScope);
                    }
                    commandVars.SetTemporaryScalar(name, value);
                }
                else
                {
                    if (assignment.Operator == AssignmentOperator.Append)
                    {
                        var hasPrevious = commandVars.TryGetExpansionValue(name, out var previous);
                        if (commandVars.IsIntegerBinding(name))
                        {
                            var integer = await WordExpansion.EvaluateIntegerAssignmentAsync(
                                value, hasPrevious ? previous : null, assignmentScope, ShellValue.Empty).ConfigureAwait(false);
                            if (!integer.HasValue)
                                return _state.TryConsumeExpansionError(out error) ? error : 1;
                            value = integer.Value;
                        }
                        else if (hasPrevious)
                            value = WordExpansion.AppendAssignmentValue(previous, value, assignmentScope);
                    }

                    if (!commandVars.TrySetScalar(name, value, memory: null, commandPrefix: true))
                    {
                        if (commandVars.IsReadonly(name))
                            await ReportCommandDiagnosticAsync([assignment.Target.ToValue()],
                                [ShellValue.FromText("readonly variable")], scope).ConfigureAwait(false);
                        else
                            commandAssignmentsSuccessful = false;
                        continue;
                    }
                }

                prefixNames.Add(name);
                // The temporary binding drops the integer attribute after preparing its value.
                if (commandVars.IsIntegerBinding(name))
                    commandVars.SetIntegerBinding(name, false);
                commandVars.SetExported(name, true);
            }
            return null;

            async Task<ShellValue> ExpandCompoundPrefixAsync(IReadOnlyList<CompoundArrayItem> words)
            {
                using var itemMemory = new MemoryCapacity(Memory.Budget, 0);
                var items = new List<IReadOnlyList<ExpandedWord>>();
                var prefixScope = assignmentScope with { CompoundPrefixItems = words };
                var length = 2L;
                foreach (var word in words)
                {
                    // The enclosing await reports any limit once, before active redirections unwind.
                    var expanded = await WordExpansion.ExpandWithoutBraceAsync(
                        word.Word, prefixScope, allowWordSplitting: false, ExpansionPurpose.CompoundPrefix,
                        TildeExpansion.None)
                        .ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return ShellValue.Empty;

                    // Even a group with no fields occupies its source item's place in the prefix text.
                    length += ExpandedWord.GetJoinedValueLength(expanded) + (items.Count == 0 ? 0L : 1L);
                    Memory.PrepareListCapacity(itemMemory, items, 1);
                    items.Add(expanded);
                }

                // Copy the segments, parentheses and separators straight into the budgeted result.
                if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes)
                    throw new ByteMemoryBuilder.LimitExceededException();
                Memory.Charge(checked(4L * length));
                var joined = new byte[(int)length];
                joined[0] = (byte)'(';
                joined[^1] = (byte)')';
                var destination = joined.AsSpan(1, joined.Length - 2);
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0)
                    {
                        destination[0] = (byte)' ';
                        destination = destination[1..];
                    }
                    destination = ExpandedWord.CopyJoinedValueTo(items[index], destination);
                }
                return ShellValue.FromBytes(joined);
            }
        }

        async Task<EvaluationResult> FailPreparationAsync(int exitCode)
        {
            var result = await FinalizeResultAsync(
                    EvaluationResult.FromExitCode(exitCode),
                    false)
                .ConfigureAwait(false);
            return CompleteCommandResult(result, preparation.Completion, commandInvoked: false);
        }

        async Task ExpandArgumentTailAsync(CommandWords words, int start, bool scanPwdOptions)
        {
            if (start >= words.Count)
                return;

            using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
            var fields = new List<ShellValue>();
            for (var index = start; index < words.Count; index++)
            {
                await AppendOperandValuesAsync(words[index], scope, fields, fieldMemory).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    break;
                if (fields.Count != 0)
                    lastArgument = fields[^1];
                if (scanPwdOptions && !pwdOptions.Finished)
                {
                    foreach (var field in fields)
                    {
                        pwdOptions.Read(field);
                        if (pwdOptions.Finished)
                            break;
                    }
                }
                fields.Clear();
            }
        }

        // Applies or inspects prepared declarations without replaying expansion effects.
        async Task<EvaluationResult> EvaluateBuiltinDeclarationAsync(
            DeclarationOptions options, IReadOnlyList<PreparedDeclarationOperand> operands,
            Variables commandVars, Variables.CommandBindings? bindings, ScopedEvaluation declarationScope, bool declarationNegated)
        {
            var executionScope = declarationScope with { Variables = commandVars };
            var functionContext = declarationScope.Context.FunctionContext;
            var localBuiltinFrame = options.Builtin == BuiltinKind.Local ? functionContext : null;
            var skipReadonlyAttribute = options.Builtin == BuiltinKind.Readonly && !options.Readonly;
            var missingLocalScope = options.Builtin == BuiltinKind.Local && localBuiltinFrame == null;
            if (options.Help == DeclarationHelp.FirstField || options.Help == DeclarationHelp.AfterOptions && !missingLocalScope)
            {
                if (GetStdOutDescriptor().IsClosed)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), declarationNegated).ConfigureAwait(false);
                return await WriteBuiltinHelpAsync(ShellValue.FromText(commandName),
                    BuiltinDocumentation.DeclarationOptionSynopsis(options.Builtin, commandName),
                    BuiltinDocumentation.For(options.Builtin).Description, declarationNegated)
                    .ConfigureAwait(false);
            }
            if (missingLocalScope)
            {
                await ReportDeclarationDiagnosticAsync([ShellValue.FromText("can only be used in a function")])
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), declarationNegated).ConfigureAwait(false);
            }
            if (!options.InvalidOption.IsEmpty)
            {
                await ReportDeclarationOperandAsync(options.InvalidOption, "invalid option").ConfigureAwait(false);
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("usage: "
                    + BuiltinDocumentation.DeclarationOptionSynopsis(options.Builtin, commandName))).ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), declarationNegated).ConfigureAwait(false);
            }
            var listing = operands.Count == 0;
            if (options.Functions && (!listing || options.Builtin != BuiltinKind.Local))
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(await EvaluateFunctionsAsync().ConfigureAwait(false)), declarationNegated).ConfigureAwait(false);
            if (listing || options.PrintNamedVariables)
            {
                var listOptionSnapshot = listing && localBuiltinFrame?.HasOwnOptionSnapshot == true;
                var showAssignments = options.Builtin == BuiltinKind.Set
                    || listing && options.Builtin == BuiltinKind.Declare && !options.Print
                    && options.ListingAttributes == DeclarationListingAttributes.None && options.CaseChange.Set == BindingAttributes.None;
                var style = showAssignments ? VariableDisplayStyle.Assignment
                    : options.FunctionNamesOnly ? VariableDisplayStyle.DeclarationWithoutScalarValues : VariableDisplayStyle.Declaration;
                var preserveStatusOnClosedOutput = listing && (options.Builtin is BuiltinKind.Declare or BuiltinKind.Local)
                    && style != VariableDisplayStyle.Assignment;
                var deferClosedOutput = !listing && GetStdOutDescriptor().IsClosed;
                var deferredFailure = DescriptorWriteResult.Open;
                var printStatus = 0;
                var outputClosed = false;
                await foreach (var name in GetNamesAsync(GetListedNames,
                    value => BindingNameExpansion.FromValue(value, Memory)).ConfigureAwait(false))
                {
                    if (HasPendingExpansionError)
                        break;
                    var optionSnapshot = name == "-"
                        && (listing ? listOptionSnapshot : HasOptionSnapshot());
                    var variables = SelectVariables(name);
                    if (!optionSnapshot && (!variables.HasBinding(name)
                        || localBuiltinFrame != null && !localBuiltinFrame.Locals.Declares(name)))
                    {
                        printStatus = 1;
                        await ReportDeclarationOperandAsync(name.ToValue(), "not found").ConfigureAwait(false);
                        continue;
                    }
                    if (outputClosed)
                        continue;
                    using var output = new ByteMemoryBuilder(0, Memory.Budget);
                    if (optionSnapshot)
                        output.Append(listing && localBuiltinFrame != null ? "local -\n"u8 : "declare -- -\n"u8);
                    else if (!variables.TryAppendVariable(name, output, style))
                    {
                        printStatus = 1;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("output limit exceeded")).ConfigureAwait(false);
                        break;
                    }
                    if (output.Length == 0)
                        continue;
                    var result = await WriteListingRowAsync(output.ToMemory(), deferClosedOutput).ConfigureAwait(false);
                    if (!result.CanAcceptMore)
                    {
                        printStatus = GetListingWriteStatus(result, printStatus, preserveStatusOnClosedOutput);
                        outputClosed = true;
                        if (deferClosedOutput && result.IsError)
                        {
                            // Named -p reports the buffered output failure after visiting the operands.
                            deferredFailure = result;
                            continue;
                        }
                        break;
                    }
                }
                if (deferredFailure.IsError)
                    await ReportNamedListingOutputFailureAsync(deferredFailure).ConfigureAwait(false);
                if (printStatus == 0 && !outputClosed && style == VariableDisplayStyle.Assignment && !HasPendingExpansionError)
                    printStatus = await EvaluateFunctionsAsync().ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(printStatus), declarationNegated).ConfigureAwait(false);

                bool HasOptionSnapshot() => localBuiltinFrame?.HasOwnOptionSnapshot == true
                    || options.Builtin == BuiltinKind.Declare && options.Print
                    && functionContext?.HasVisibleOptionSnapshot() == true;

                // No-argument listings hide only this command's prefixes; named queries inspect them.
                Variables SelectVariables(BindingName name) => !listing ? commandVars
                    : bindings?.Contains(name) == true ? bindings.SavedVariables : declarationScope.Variables;

                IEnumerable<BindingName> GetListedNames(MemoryCapacity listingMemory)
                {
                    var names = commandVars.BindingNames;
                    if (bindings is { } savedBindings)
                    {
                        // Each store is unique. A saved prefix replaces the active entry for listing.
                        names = names.Where(name => !savedBindings.Contains(name))
                            .Concat(savedBindings.SavedVariables.BindingNames);
                    }
                    var selected = names.Where(MatchesFilter);
                    if (listOptionSnapshot)
                        selected = selected.Prepend("-");
                    var sorted = new List<BindingName>();
                    foreach (var name in selected)
                    {
                        Memory.PrepareListCapacity(listingMemory, sorted, 1);
                        if (name.NeedsValueEncoding)
                            Memory.Charge(listingMemory, MemoryCapacity.ArrayBackingBytes<byte>(name.ByteLength));
                        sorted.Add(name.WithEncodedSpelling());
                    }
                    sorted.Sort(static (left, right) => ShellValue.ByteOrder.Compare(left.ToValue(), right.ToValue()));
                    return sorted;
                }

                bool MatchesFilter(BindingName name)
                {
                    var variables = SelectVariables(name);
                    if (!variables.HasBinding(name))
                        return false;
                    // Attribute options do not narrow a local listing.
                    if (localBuiltinFrame != null)
                        return localBuiltinFrame.Locals.Declares(name);
                    var flags = options.ListingAttributes;
                    return (!flags.HasFlag(DeclarationListingAttributes.Indexed) || variables.IndexedArrays.ContainsKey(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Associative) || variables.AssociativeArrays.ContainsKey(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Nameref) || variables.HasNamerefAttribute(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Exported) || variables.IsExported(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Integer) || variables.IsIntegerBinding(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Readonly) || variables.IsReadonlyBinding(name))
                        && (options.CaseChange.Set == BindingAttributes.None
                            || (variables.GetAssignmentAttributes(name) & options.CaseChange.Set) != 0);
                }
            }

            // Downstream closure ends the write without replacing an earlier operand failure.
            // Buffered declaration listings also keep their status on shell-closed stdout.
            int GetListingWriteStatus(DescriptorWriteResult result, int status, bool preserveStatusOnClosedOutput) =>
                result.IsError && (!preserveStatusOnClosedOutput || !GetStdOutDescriptor().IsClosed) ? 1 : status;

            Task<DescriptorWriteResult> WriteListingRowAsync(ReadOnlyMemory<byte> output, bool shellClosed) => shellClosed
                ? ExecuteWriteAsync(GetStdOutDescriptor(), output)
                : WriteBuiltinOutputAsync(ShellValue.FromText(commandName), output);

            Task ReportNamedListingOutputFailureAsync(DescriptorWriteResult result) => ReportBuiltinWriteFailureAsync(
                ShellValue.FromText(commandName), result, GetBuiltinDiagnosticPrefix(declarationScope.Context));

            async Task<int> EvaluateFunctionsAsync()
            {
                const DeclarationListingAttributes ArrayFilters =
                    DeclarationListingAttributes.Indexed | DeclarationListingAttributes.Associative;
                var noOperands = listing;
                var preserveStatusOnClosedOutput = noOperands && options.Builtin == BuiltinKind.Declare && options.Functions;
                if (noOperands && (options.ListingAttributes & ArrayFilters) != DeclarationListingAttributes.None)
                    return 0;
                if (!noOperands && !options.Print && options.Builtin is BuiltinKind.Declare or BuiltinKind.Local
                    && options.InvalidFunctionAttribute.Length != 0)
                {
                    await ReportDeclarationOperandAsync(ShellValue.FromText(options.InvalidFunctionAttribute), "invalid option")
                        .ConfigureAwait(false);
                    return 1;
                }

                var inspect = noOperands || options.Builtin is BuiltinKind.Declare or BuiltinKind.Local
                    && (options.Print || !options.RemoveFunctions
                        && options.ExportChange == DeclarationAttributeChange.Unchanged
                        && options.ReadonlyChange == DeclarationAttributeChange.Unchanged
                        && options.IndexedChange == DeclarationAttributeChange.Unchanged
                        && options.AssociativeChange == DeclarationAttributeChange.Unchanged
                        && options.IntegerChange == DeclarationAttributeChange.Unchanged
                        && options.NamerefChange == DeclarationAttributeChange.Unchanged
                        && options.CaseChange.IsEmpty);
                var caseAttributes = (FunctionAttributes)options.CaseChange.Set;
                var filter = caseAttributes;
                if (options.ListingAttributes.HasFlag(DeclarationListingAttributes.Readonly)) filter |= FunctionAttributes.Readonly;
                if (options.ListingAttributes.HasFlag(DeclarationListingAttributes.Exported)) filter |= FunctionAttributes.Exported;
                var filterNames = noOperands && (options.ListingAttributes != DeclarationListingAttributes.None
                    || caseAttributes != FunctionAttributes.None);
                var namedClosedOutput = !noOperands && GetStdOutDescriptor().IsClosed;
                var deferredFailure = DescriptorWriteResult.Open;
                var functionStatus = 0;
                await foreach (var name in GetNamesAsync(GetListedFunctionNames, static value => value).ConfigureAwait(false))
                {
                    if (HasPendingExpansionError) break;
                    if (!options.Print && TrySaveLocalOptions(name))
                        continue;
                    if (!noOperands && !options.Print && name.Bytes.Span.Contains((byte)'=')
                        && options.Builtin is BuiltinKind.Declare or BuiltinKind.Local)
                    {
                        // Unlike a missing name, an attempted function assignment stops later attribute writes.
                        await ReportDeclarationDiagnosticAsync([ShellValue.FromText("cannot use `-f' to make functions")])
                            .ConfigureAwait(false);
                        return 1;
                    }
                    if (!_state.TryGetFunction(name, out var function))
                    {
                        functionStatus = 1;
                        if (options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                            await ReportDeclarationOperandAsync(name, "not a function").ConfigureAwait(false);
                        else if (options.Print)
                            await ReportDeclarationOperandAsync(name, "not found").ConfigureAwait(false);
                        continue;
                    }
                    if (filterNames && !MatchesFilter(function))
                        continue;
                    if (!inspect)
                    {
                        var attributes = function.Attributes;
                        // Export validates transport names; declare/local only set the attribute.
                        if (options.Builtin == BuiltinKind.Export && options.ExportChange == DeclarationAttributeChange.Set
                            && !function.Definition.Name.CanExport)
                        {
                            functionStatus = 1;
                            await ReportDeclarationOperandAsync(name, "cannot export").ConfigureAwait(false);
                            continue;
                        }
                        if (options.ReadonlyChange == DeclarationAttributeChange.Clear && attributes.HasFlag(FunctionAttributes.Readonly))
                        {
                            functionStatus = 1;
                            await ReportDeclarationOperandAsync(name, "readonly function").ConfigureAwait(false);
                            continue;
                        }
                        if (options.Readonly) attributes |= FunctionAttributes.Readonly;
                        if (options.ExportChange == DeclarationAttributeChange.Set) attributes |= FunctionAttributes.Exported;
                        if (options.ExportChange == DeclarationAttributeChange.Clear) attributes &= ~FunctionAttributes.Exported;
                        attributes = (attributes | caseAttributes) & ~(FunctionAttributes)options.CaseChange.Clear;
                        if (attributes != function.Attributes)
                            _state.SetFunctionBinding(function with { Attributes = attributes });
                        continue;
                    }

                    if (deferredFailure.IsError)
                        continue;
                    using var output = new ByteMemoryBuilder(0, Memory.Budget);
                    var style = !options.Functions ? FunctionDisplayStyle.Definition : !options.FunctionNamesOnly
                        ? noOperands || options.Print ? FunctionDisplayStyle.DefinitionWithAttributes : FunctionDisplayStyle.Definition
                        : noOperands || options.Print ? FunctionDisplayStyle.Attributes : FunctionDisplayStyle.Name;
                    if (!function.TryAppend(output, style))
                    {
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("output limit exceeded")).ConfigureAwait(false);
                        return 1;
                    }
                    var result = await WriteListingRowAsync(output.ToMemory(), namedClosedOutput).ConfigureAwait(false);
                    if (namedClosedOutput && result.IsError)
                    {
                        functionStatus = 1;
                        if (options.Print)
                            deferredFailure = result;
                        else
                            await ReportNamedListingOutputFailureAsync(result).ConfigureAwait(false);
                        continue;
                    }
                    if (!result.CanAcceptMore)
                        return GetListingWriteStatus(result, functionStatus, preserveStatusOnClosedOutput);
                }
                if (deferredFailure.IsError)
                    await ReportNamedListingOutputFailureAsync(deferredFailure).ConfigureAwait(false);
                return functionStatus;

                IEnumerable<ShellValue> GetListedFunctionNames(MemoryCapacity listingMemory)
                {
                    var count = _state.FunctionCount;
                    if (count == 0 || filterNames && !_state.EnumerateFunctions().Any(entry => MatchesFilter(entry.Value)))
                        return [];

                    // A host write can replace attributes on any name already in the listing.
                    Memory.Charge(listingMemory, MemoryCapacity.ArrayBackingBytes<ShellValue>(count));
                    var sorted = new ShellValue[count];
                    var index = 0;
                    foreach (var (name, function) in _state.EnumerateFunctions())
                    {
                        CheckCancellation();
                        // A replacement can retire evaluator-owned source during a write.
                        var spelling = name;
                        if (function.SourceMemory != null)
                        {
                            Memory.Charge(listingMemory, MemoryCapacity.ArrayBackingBytes<byte>(name.Bytes.Length));
                            spelling = ShellValue.FromBytes(name.Bytes.ToArray());
                        }
                        sorted[index++] = spelling;
                    }
                    Array.Sort(sorted, ShellValue.ByteOrder);
                    return sorted;
                }

                bool MatchesFilter(FunctionBinding function)
                {
                    CheckCancellation();
                    return (function.Attributes & filter) != FunctionAttributes.None;
                }
            }

            async IAsyncEnumerable<T> GetNamesAsync<T>(Func<MemoryCapacity, IEnumerable<T>> listNames, Func<ShellValue, T> bindOperand)
            {
                if (listing)
                {
                    // Keep the sorted snapshot through each awaited consumer and release it when enumeration ends.
                    using var listingMemory = new MemoryCapacity(Memory.Budget, 0);
                    foreach (var name in listNames(listingMemory))
                        yield return name;
                }
                else
                {
                    // Named inspection materializes only the reached operand.
                    foreach (var operand in operands)
                    {
                        var value = await GetDeclarationOperandValueAsync(operand, declarationScope).ConfigureAwait(false);
                        if (HasPendingExpansionError)
                            yield break;
                        yield return bindOperand(value);
                    }
                }
            }

            var setsNamedAttribute = options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly;
            var conflictingArrayKinds = options.IndexedChange == DeclarationAttributeChange.Set
                && options.AssociativeChange == DeclarationAttributeChange.Set;
            var globalBindings = options.Global ? bindings : null;
            const string NamerefArrayError = "reference variable cannot be an array";
            var exitCode = 0;
            List<Variables.CommandBindings>? exposedPrefixes = null;
            foreach (var preparedOperand in operands)
            {
                var operand = preparedOperand;
                if (operand.Value == null && TrySaveLocalOptions(operand.Name))
                    continue;
                if (!TryParseDeclarationReference(ShellSource.FromBytes(operand.Name.Bytes), out var reference)
                    || operand.Value == null && reference is { Kind: VariableReferenceKind.Element, Subscript.IsEmpty: true })
                {
                    var invalidOperand = await GetDeclarationOperandValueAsync(operand, declarationScope).ConfigureAwait(false);
                    if (HasPendingExpansionError)
                        break;
                    await DiagnoseQuotedAsync(invalidOperand, "not a valid identifier").ConfigureAwait(false);
                    continue;
                }
                if (reference.Kind != VariableReferenceKind.Name && setsNamedAttribute)
                {
                    await DiagnoseQuotedAsync(operand.Name, "not a valid identifier").ConfigureAwait(false);
                    continue;
                }
                var element = reference.Kind != VariableReferenceKind.Name;
                var setsNameref = options.NamerefChange == DeclarationAttributeChange.Set;
                if (element && !reference.Subscript.IsEmpty && setsNameref)
                {
                    exitCode = 1;
                    await ReportDeclarationOperandAsync(operand.Name, NamerefArrayError).ConfigureAwait(false);
                    continue;
                }
                var local = options.IsLocal(declarationScope, options.Global);
                if (element && reference.Subscript.IsEmpty)
                {
                    var clearsArrayKind = options.IndexedChange == DeclarationAttributeChange.Clear
                        || options.AssociativeChange == DeclarationAttributeChange.Clear;
                    if (setsNameref && !clearsArrayKind && operand.Operator == AssignmentOperator.Replace
                        && operand.Value is { IsEmpty: false } initializer
                        && !TryParseDeclarationReference(ShellSource.FromBytes(initializer.Bytes), out _))
                    {
                        await DiagnoseQuotedAsync(initializer, "invalid variable name for name reference").ConfigureAwait(false);
                        continue;
                    }
                    if (local && functionContext is { } function)
                    {
                        if ((!setsNameref || !clearsArrayKind)
                            && await TryRejectLocalElementInitializerAsync(reference, operand, function, setsNameref).ConfigureAwait(false))
                            continue;
                    }
                    else if (options.IndexedChange != DeclarationAttributeChange.Clear)
                    {
                        if (options.NamerefChange == DeclarationAttributeChange.Clear)
                        {
                            if (await TryRejectEmptyElementAfterNamerefClearAsync(reference.Name, operand).ConfigureAwait(false))
                                continue;
                        }
                        else if (!clearsArrayKind)
                        {
                            await RejectEmptyElementInitializerAsync(reference, operand, setsNameref).ConfigureAwait(false);
                            continue;
                        }
                    }
                }
                else if (local && reference.Kind == VariableReferenceKind.Element && operand.Value != null
                    && functionContext is { } selectorFunction
                    && await TryRejectLocalElementInitializerAsync(reference, operand, selectorFunction, setsNameref: false).ConfigureAwait(false))
                {
                    if (HasPendingExpansionError)
                        break;
                    continue;
                }
                if (skipReadonlyAttribute && !operand.CompoundApplied && operand.Value == null)
                {
                    // The flag only looks up a named operand, even with an array option.
                    if (!commandVars.TryResolveBinding(reference.Name, selectOwner: null, out _))
                        await ReportCircularNameReferenceAsync(reference.Name).ConfigureAwait(false);
                    continue;
                }
                // Array flags affect assignment operands. A parsed compound initializer
                // reaches dispatch as a name, with its array already initialized.
                var arrayKind = !setsNamedAttribute ? options.ArrayKind
                    : operand.Value == null ? ArrayAssignmentKind.Auto
                    : options.IndexedChange == DeclarationAttributeChange.Set ? ArrayAssignmentKind.Indexed : options.ArrayKind;
                var arrayAttributeAssignment = setsNamedAttribute && arrayKind != ArrayAssignmentKind.Auto;
                var assignmentExport = arrayAttributeAssignment && options.Builtin == BuiltinKind.Export
                    ? DeclarationAttributeChange.Set : options.ExportChange;
                // Global declarations can write indexed elements through the active scope.
                var sourceName = reference.Name;
                ShellValue? assignmentPrefix = arrayAttributeAssignment
                    && (options.ExportChange == DeclarationAttributeChange.Set || options.Readonly)
                    && bindings?.Contains(sourceName) == true && commandVars.Scalars.TryGetValue(sourceName, out var prefixValue)
                    ? prefixValue : null;
                var inferElementArray = element && operand.Value == null && !local && arrayKind == ArrayAssignmentKind.Auto
                    && options.IndexedChange != DeclarationAttributeChange.Clear;
                var clearIndexedElement = element && !local && !operand.CompoundApplied
                    && arrayKind == ArrayAssignmentKind.Auto && options.IndexedChange == DeclarationAttributeChange.Clear;
                var assignsGlobalElement = options.Global && element && operand.Value != null && !clearIndexedElement
                    && options.NamerefChange != DeclarationAttributeChange.Set && !conflictingArrayKinds;
                var selectElementBinding = inferElementArray || clearIndexedElement && (operand.Value == null || options.Global);
                var global = (options.Global || arrayAttributeAssignment)
                    && (reference.Kind == VariableReferenceKind.Name || selectElementBinding || assignsGlobalElement);
                if (!global && !local && !operand.CompoundApplied && operand.Value == null)
                {
                    var visibleName = options.Nameref ? sourceName : StripArraySubscript(commandVars.ResolveNamerefName(sourceName));
                    if (!visibleName.IsEmpty && (!commandVars.HasBinding(visibleName)
                        || commandVars.IsUnassignedScalar(visibleName) && (options.Readonly || options.ExportChange == DeclarationAttributeChange.Set)))
                    {
                        // An unset prefix exposes its caller's binding; an unset local still hides it.
                        var owner = declarationScope.Context.GetVisibleBindingOwner(visibleName, commandVars, Memory, ref exposedPrefixes);
                        if (!ReferenceEquals(owner, commandVars))
                            commandVars.CopyBindingFrom(owner, visibleName);
                        if (exposedPrefixes is { } selected)
                        {
                            foreach (var prefix in selected)
                                prefix.ExposeBinding(visibleName, commandVars);
                            selected.Clear();
                        }
                    }
                }
                var resolution = !local && options.NamerefChange == DeclarationAttributeChange.Clear
                    ? BindingResolution.LastNameref : BindingResolution.Referent;
                var targetVars = commandVars;
                var circular = false;
                var recreateArrayReference = false;
                var detachElementNameref = false;
                if (global || selectElementBinding)
                {
                    if (assignsGlobalElement && resolution != BindingResolution.LastNameref
                        || options.NamerefChange == DeclarationAttributeChange.Set)
                        targetVars = SelectGlobalOwner(reference.Name);
                    else
                    {
                        var found = global
                            ? declarationScope.Context.TryResolveGlobalBinding(sourceName, declarationScope.Variables,
                                commandVars, prefixNames, globalBindings, resolution, out var binding)
                            : commandVars.TryResolveBinding(sourceName, selectOwner: null, resolution, out binding);
                        if (!found)
                        {
                            circular = true;
                            binding = new VariableBinding(VariableReference.ForName(sourceName),
                                global ? SelectGlobalOwner(sourceName) : commandVars);
                        }
                        if (selectElementBinding)
                        {
                            if (circular)
                                await ReportCommandDiagnosticAsync([ShellValue.FromText("warning")],
                                    [GetDiagnosticName(sourceName), ShellValue.FromText(": circular name reference")], declarationScope)
                                    .ConfigureAwait(false);
                            if ((global || clearIndexedElement) && binding.Reference.Kind != VariableReferenceKind.Name)
                            {
                                exitCode = 1;
                                await ReportCommandDiagnosticAsync([ShellValue.FromText(commandName)],
                                    [ShellValue.FromText("`"), binding.Reference.ToValue(Memory),
                                        ShellValue.FromBytes(operand.Name.Bytes[sourceName.ByteLength..]),
                                        ShellValue.FromText("': not a valid identifier")], declarationScope).ConfigureAwait(false);
                                continue;
                            }
                            // Ordinary inference keeps an existing whole referent. Global lookup and +a can declare a missing one.
                            if (binding.IsUnsetNameref || binding.Reference.Kind != VariableReferenceKind.Name
                                || !global && !clearIndexedElement && binding.Name != sourceName && !binding.Owner.HasBinding(binding.Name))
                                binding = new VariableBinding(VariableReference.ForName(sourceName),
                                    global ? SelectGlobalOwner(sourceName) : commandVars);
                            else
                                detachElementNameref = !circular && resolution == BindingResolution.LastNameref
                                    && binding.Owner.TryGetNameref(binding.Name, out _);
                        }
                        else if (!assignsGlobalElement && !circular && arrayKind != ArrayAssignmentKind.Auto
                            && (resolution == BindingResolution.Referent
                                || binding.Owner.TryGetNameref(binding.Name, out var lastTarget) && lastTarget.IsEmpty))
                        {
                            var declaredOwner = SelectGlobalOwner(reference.Name);
                            binding = declaredOwner.SelectArrayDeclarationBinding(reference.Name, binding);
                            recreateArrayReference = binding.Owner.TryGetNameref(binding.Name, out _);
                        }
                        reference = selectElementBinding || assignsGlobalElement
                            ? VariableReference.ForElement(binding.Name, reference.Subscript) : binding.Reference;
                        targetVars = binding.Owner;
                        if (global && !assignsGlobalElement && reference.Kind != VariableReferenceKind.Name && bindings?.Contains(reference.Name) == true)
                            targetVars = bindings.SavedVariables;
                    }
                }
                else if (resolution == BindingResolution.LastNameref
                    && targetVars.TryResolveBinding(reference.Name, selectOwner: null, resolution, out var lastReference))
                {
                    if (arrayKind != ArrayAssignmentKind.Auto)
                        lastReference = targetVars.SelectArrayDeclarationBinding(sourceName, lastReference);
                    var lastTarget = lastReference.Reference;
                    reference = reference.Kind == VariableReferenceKind.Name ? lastTarget
                        : VariableReference.ForElement(lastTarget.Name, reference.Subscript);
                }
                var name = reference.Name;
                var existingLocal = declarationScope.Context.FunctionContext?.Locals.Declares(name) == true;
                var hasIntegerInitializer = options.IntegerChange == DeclarationAttributeChange.Set && operand.Value != null;
                var rejectsIntegerNamerefInitializer = hasIntegerInitializer && options.Nameref;
                ShellValue? clearedNamerefTarget = options.NamerefChange == DeclarationAttributeChange.Clear
                    && (!local || existingLocal) && targetVars.TryGetNameref(name, out var removedTarget) ? removedTarget : null;
                var clearsWholeNameref = clearedNamerefTarget.HasValue && arrayKind == ArrayAssignmentKind.Auto && !element;
                var rejectsIntegerNamerefRemoval = hasIntegerInitializer && clearsWholeNameref
                    && (local || name == sourceName && clearedNamerefTarget is { IsEmpty: true });
                if (resolution == BindingResolution.LastNameref && name != sourceName
                    && arrayKind == ArrayAssignmentKind.Auto && !operand.CompoundApplied
                    && targetVars.TryGetNameref(name, out var unsetTarget) && unsetTarget.IsEmpty)
                    continue; // +n cannot detach an indirectly reached unset reference or apply attributes to it.
                if (!assignsGlobalElement && !operand.CompoundApplied && !local && options.NamerefChange != DeclarationAttributeChange.Set
                    && arrayKind != ArrayAssignmentKind.Auto
                    && (recreateArrayReference || targetVars.TryResolveBinding(name, selectOwner: null, out var arrayBinding)
                        && arrayBinding.Owner.TryGetNameref(arrayBinding.Name, out var arrayTarget) && arrayTarget.IsEmpty))
                {
                    // Recreating an unset reference discards its old attributes before checking the initializer.
                    targetVars.RecreateDeclarationArray(name,
                        arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
                }
                var circularLocalReference = false;
                if (!operand.CompoundApplied && reference.Kind == VariableReferenceKind.Name
                    && (local || options.NamerefChange != DeclarationAttributeChange.Set)
                    && (circular || !targetVars.TryResolveBinding(name, selectOwner: null, out _)))
                {
                    await ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                    if (local)
                    {
                        // Local creation performs its own lookup, then reuses a reference already local to this frame.
                        await ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                        circularLocalReference = existingLocal && arrayKind == ArrayAssignmentKind.Auto;
                    }
                    else if (arrayKind == ArrayAssignmentKind.Auto)
                    {
                        if (options.Builtin == BuiltinKind.Export
                            && options.ExportChange == DeclarationAttributeChange.Clear && operand.Value == null)
                            continue;
                        // Ordinary binding cannot create through a cycle; this is a successful declaration no-op.
                        await ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                        if (operand.Value != null && setsNamedAttribute)
                        {
                            if (options.ExportChange != DeclarationAttributeChange.Clear)
                                await ReportCircularNameReferenceAsync(name).ConfigureAwait(false); // Attribute target lookup.
                            exitCode = 1;
                        }
                        continue;
                    }
                    if (arrayKind != ArrayAssignmentKind.Auto && (!local || existingLocal))
                        targetVars.RecreateDeclarationArray(name,
                            arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
                }
                var selfNameref = false;
                ShellValue? namerefTarget = null;
                VariableReference? namerefReference = null;
                if (!operand.CompoundApplied && (options.Nameref || circularLocalReference))
                {
                    if (operand.Value is { } initializer
                        && TryParseDeclarationReference(ShellSource.FromBytes(initializer.Bytes), out var initializerReference))
                        namerefReference = initializerReference;
                    // Declaration initializers reject self targets at script scope, before
                    // replacing values or attributes. Ordinary assignments may create cycles.
                    if (options.Nameref && namerefReference is { } selfReference && selfReference.Name == name)
                    {
                        if (declarationScope.Context.FunctionContext == null)
                        {
                            await DiagnoseAsync(name, "nameref variable self references not allowed").ConfigureAwait(false);
                            continue;
                        }
                        await WriteDiagnosticAsync([ShellValue.FromText(commandName)],
                            [ShellValue.FromText("warning: "), GetDiagnosticName(name), ShellValue.FromText(": circular name reference")],
                            _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
                            rejectsIntegerNamerefInitializer ? GetBuiltinDiagnosticPrefix(declarationScope.Context) : default)
                            .ConfigureAwait(false);
                        selfNameref = true;
                    }
                    // Integer append and empty initializers validate their original spelling after arithmetic.
                    if (!rejectsIntegerNamerefInitializer || operand.Operator == AssignmentOperator.Replace && operand.Value is { IsEmpty: false })
                    {
                        ShellValue? previous = null;
                        if (!local || existingLocal || prefixNames?.Contains(name) == true || targetVars.IsTemporaryBinding(name))
                            previous = targetVars.TryGetNameref(name, out var referenced)
                                ? referenced.IsEmpty ? null : referenced
                                : targetVars.Scalars.TryGetValue(name, out var scalar) ? scalar : null;
                        // Validate before creating a local: invalid targets leave the outer binding visible.
                        namerefTarget = operand.Value ?? previous;
                        if (operand.Operator == AssignmentOperator.Append && operand.Value != null)
                            namerefTarget = WordExpansion.AppendAssignmentValue(previous ?? ShellValue.Empty, operand.Value.Value, declarationScope);
                        if (namerefTarget is { } targetValue)
                        {
                            if (operand.Value == null || operand.Operator == AssignmentOperator.Append)
                                namerefReference = TryParseDeclarationReference(ShellSource.FromBytes(targetValue.Bytes), out var targetReference)
                                    ? targetReference : null;
                            if (namerefReference == null)
                            {
                                await DiagnoseQuotedAsync(targetValue, operand.Value is { IsEmpty: true }
                                        ? "not a valid identifier" : "invalid variable name for name reference")
                                    .ConfigureAwait(false);
                                continue;
                            }
                        }
                    }
                }
                var elementNameref = ShellValue.Empty;
                var removeElementNameref = operand.Value != null && reference.Kind != VariableReferenceKind.Name
                    && !local && options.NamerefChange != DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out elementNameref);
                var initializeGlobalArray = assignsGlobalElement && (removeElementNameref
                    ? !elementNameref.IsEmpty : !targetVars.HasBinding(name));
                if (removeElementNameref)
                {
                    if (targetVars.IsReadonlyBinding(name))
                    {
                        await RejectReadonlyElementNamerefAsync(name, targetVars).ConfigureAwait(false);
                        continue;
                    }
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                        continue;
                    }
                    await ReportElementNamerefRemovalAsync(name).ConfigureAwait(false);
                }
                VariableBinding? selectedLocalBinding = local && existingLocal && options.NamerefChange == DeclarationAttributeChange.Unchanged
                    && targetVars.TryResolveBinding(name, selectOwner: null, out var declaredBinding) ? declaredBinding : null;
                var followsLocalNameref = selectedLocalBinding is { Reference.Kind: VariableReferenceKind.Name } reached
                    && reached.Name != name && !reached.IsUnsetNameref;
                var localDeclarationTarget = followsLocalNameref ? selectedLocalBinding.GetValueOrDefault().Name : name;
                var existingTargetLocal = followsLocalNameref
                    ? functionContext?.Locals.Declares(localDeclarationTarget) == true : existingLocal;
                var shadowEnclosingLocal = followsLocalNameref && !existingTargetLocal
                    && functionContext?.Enclosing?.IsLocal(localDeclarationTarget) == true;
                // When +n cannot find the immediate target's spelling, the local declaration creates it.
                var keepLocalNameref = local && clearedNamerefTarget is { IsEmpty: false } localTarget
                    && !targetVars.HasBinding(BindingNameExpansion.FromValue(localTarget, Memory));
                var localizesTarget = clearsWholeNameref && keepLocalNameref && !rejectsIntegerNamerefRemoval;
                var localizesElementSpelling = selectedLocalBinding is { Reference.Kind: VariableReferenceKind.Element }
                    && (element || arrayKind == ArrayAssignmentKind.Auto);
                var clearsReadonlyUnsetNameref = clearsWholeNameref && clearedNamerefTarget is { IsEmpty: true }
                    && operand.Value == null && options.ReadonlyChange != DeclarationAttributeChange.Clear
                    && targetVars.IsReadonlyBinding(name);
                // Local redeclarations apply readonly and initialization to the reached binding.
                var readonlyBinding = followsLocalNameref ? !shadowEnclosingLocal && targetVars.IsReadonlyBinding(localDeclarationTarget)
                    : options.NamerefChange != DeclarationAttributeChange.Unchanged && reference.Kind == VariableReferenceKind.Name
                        ? !localizesTarget && !clearsReadonlyUnsetNameref && targetVars.IsReadonlyBinding(name)
                        : !localizesElementSpelling && targetVars.IsReadonly(name);
                var declarationName = resolution == BindingResolution.LastNameref || followsLocalNameref && existingTargetLocal
                    ? sourceName : selectedLocalBinding?.Name ?? name;
                var redeclaresLocalArray = followsLocalNameref && existingTargetLocal && element && operand.Value == null;
                if (!operand.CompoundApplied && (operand.Value != null || local && arrayKind == ArrayAssignmentKind.Auto && !redeclaresLocalArray
                        || options.Nameref || options.ReadonlyChange == DeclarationAttributeChange.Clear)
                    && (!local || existingLocal) && readonlyBinding)
                {
                    if (arrayAttributeAssignment)
                    {
                        await DiagnoseReadonlyAsync(sourceName).ConfigureAwait(false);
                        await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                    }
                    else if (operand.Value != null && setsNamedAttribute)
                    {
                        // These builtins apply attributes even when their assignment fails.
                        exitCode = 1;
                        await ReportCommandDiagnosticAsync([GetDiagnosticName(targetVars.ResolveNamerefName(name))],
                            [ShellValue.FromText("readonly variable")], declarationScope).ConfigureAwait(false);
                        await ApplyAttributesAsync(name, targetVars, global, local, assignmentExport).ConfigureAwait(false);
                    }
                    else
                    {
                        if (local && element && operand.Value != null && existingTargetLocal && functionContext is { } function)
                            function.Locals.PrepareReadonlyElementDeclaration(targetVars, localDeclarationTarget,
                                arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
                        await DiagnoseReadonlyAsync(declarationName).ConfigureAwait(false);
                    }
                    continue;
                }
                if (rejectsIntegerNamerefInitializer || rejectsIntegerNamerefRemoval)
                {
                    if (rejectsIntegerNamerefInitializer && (!local || existingLocal)
                        && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                    {
                        await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                        continue;
                    }
                    if (local && !existingLocal && functionContext is { } function
                        && !function.Locals.TryDeclare(declarationScope.Variables, targetVars,
                            new VariableOperand(VariableOperandKind.Name, reference, ShellValue.Empty), prefixNames,
                            ArrayAssignmentKind.Auto, isNameref: false, options.Inherit, options.CaseChange,
                            elementAttributes: default, Memory, function.Enclosing))
                    {
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                        continue;
                    }

                    // Clearing a nonlocal unset reference deletes the slot and its old attributes.
                    // Otherwise the integer flag applies to the declaration before binding fails.
                    if (rejectsIntegerNamerefRemoval && !local)
                        _ = targetVars.TryUnsetSelected(new VariableUnsetTarget(name, RequiresSelector: false, BindingName.Empty), UnsetFlags.None);
                    else if (targetVars.HasBinding(name))
                        await ApplyAttributesAsync(name, targetVars, global, local, assignmentExport).ConfigureAwait(false);
                    exitCode = 1;
                    var initializer = operand.Value.GetValueOrDefault();
                    var integerScope = executionScope with { Variables = targetVars };
                    ShellValue? previous = null;
                    if (operand.Operator == AssignmentOperator.Append)
                    {
                        var selected = await AwaitWordExpansionAsync(WordExpansion.ResolveReadReferenceAsync(reference, integerScope))
                            .ConfigureAwait(false);
                        if (HasPendingExpansionError)
                            break;
                        if (selected != null && selected.TryRead(targetVars, out var stored))
                            previous = stored;
                    }
                    await AwaitWordExpansionAsync(WordExpansion.EvaluateIntegerAssignmentAsync(initializer, previous,
                        integerScope, ShellValue.FromText(commandName)).AsTask()).ConfigureAwait(false);
                    if (HasPendingExpansionError)
                        break;
                    if (rejectsIntegerNamerefRemoval)
                        namerefReference = TryParseDeclarationReference(ShellSource.FromBytes(initializer.Bytes), out var target) ? target : null;
                    if (namerefReference == null)
                        await DiagnoseQuotedAsync(initializer, "not a valid identifier").ConfigureAwait(false);
                    continue;
                }
                if (!element && options.NamerefChange == DeclarationAttributeChange.Clear && targetVars.IsArrayNameref(name))
                {
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                        continue;
                    }
                }
                else if (clearsReadonlyUnsetNameref)
                    targetVars.DetachNamerefToUnassigned(name);
                else if (local && !keepLocalNameref && clearedNamerefTarget is { } oldTarget
                    && (!oldTarget.IsEmpty || clearsWholeNameref))
                {
                    if (clearsWholeNameref && operand.Value is { } initializer)
                    {
                        // Validate the bound spelling while the name is still a reference.
                        var append = operand.Operator == AssignmentOperator.Append;
                        var value = append ? WordExpansion.AppendAssignmentValue(oldTarget, initializer, declarationScope) : initializer;
                        if (!TryParseDeclarationReference(ShellSource.FromBytes(value.Bytes), out _))
                        {
                            if (!append || !TryParseDeclarationReference(ShellSource.FromBytes(initializer.Bytes), out _))
                                await DiagnoseQuotedAsync(initializer, "not a valid identifier").ConfigureAwait(false);
                            else
                                exitCode = 1;
                            continue;
                        }
                        operand = operand with { Value = value, Operator = AssignmentOperator.Replace };
                    }
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                        continue;
                    }
                    if (!oldTarget.IsEmpty)
                        targetVars.TrySetResolvedScalar(name, oldTarget, Memory);
                }
                var convertedReference = false;
                if (options.Nameref && (!local || existingLocal)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                    continue;
                }
                if (options.NamerefChange == DeclarationAttributeChange.Set && arrayKind != ArrayAssignmentKind.Auto
                    && targetVars.TryGetNameref(name, out var arrayReference) && (!local && !arrayReference.IsEmpty || local && existingLocal))
                {
                    // With both -n and an array flag, the builtin converts the reference itself,
                    // independently of any compound write already applied to its referent.
                    var kind = arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed;
                    if (local)
                    {
                        if (!targetVars.TryDeclareResolvedArray(name, kind, ScalarArrayPromotion.DiscardValue))
                        {
                            await DiagnoseAsync(name, "invalid local declaration").ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        if (!targetVars.TryRemoveNamerefAttribute(name))
                        {
                            await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                            continue;
                        }
                        targetVars.TrySetResolvedScalar(name, arrayReference, Memory);
                    }
                    convertedReference = true;
                }
                var localReferenceAssigned = false;
                if ((!operand.CompoundApplied || prefixNames?.Contains(name) == true) && !convertedReference
                    && local && !circularLocalReference && declarationScope.Context.FunctionContext is { } localFunction)
                {
                    var initializingReference = existingLocal && arrayKind == ArrayAssignmentKind.Auto
                        && operand.Value != null && targetVars.TryResolveBinding(name, selectOwner: null, out var terminalBinding)
                        && terminalBinding.IsUnsetNameref;
                    var localValue = operand.Value ?? ShellValue.Empty;
                    if (initializingReference && !TryParseDeclarationReference(ShellSource.FromBytes(localValue.Bytes), out _))
                    {
                        await DiagnoseQuotedAsync(localValue, "invalid variable name for name reference").ConfigureAwait(false);
                        continue;
                    }
                    var hasReferenceValue = (options.Nameref || initializingReference) && operand.Value != null;
                    var localOperand = new VariableOperand(
                        hasReferenceValue ? VariableOperandKind.Assignment : VariableOperandKind.Name,
                        reference, hasReferenceValue ? namerefTarget ?? localValue : ShellValue.Empty);
                    var checkInheritedReadonly = !existingTargetLocal && options.Inherit
                        && (operand.Value != null || options.ReadonlyChange == DeclarationAttributeChange.Clear);
                    var inheritedReadonly = checkInheritedReadonly && targetVars.IsReadonlyBinding(localDeclarationTarget);
                    var localCaseChange = inheritedReadonly ? default : options.CaseChange;
                    var elementAttributes = element && operand.Value != null && !options.Readonly && !inheritedReadonly
                        ? GetAttributeChanges(assignmentExport) : default;
                    if (!localFunction.Locals.TryDeclare(declarationScope.Variables, targetVars,
                            localOperand, prefixNames, arrayKind, options.Nameref, options.Inherit, localCaseChange,
                            elementAttributes, Memory, localFunction.Enclosing))
                    {
                        if (!redeclaresLocalArray && (followsLocalNameref ? readonlyBinding : targetVars.IsReadonly(name)))
                            await DiagnoseReadonlyAsync(declarationName).ConfigureAwait(false);
                        else
                        {
                            var message = arrayKind == ArrayAssignmentKind.Auto ? "invalid local declaration"
                                : ArrayConversionMessage(arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
                            await DiagnoseAsync(declarationName, message).ConfigureAwait(false);
                        }
                        continue;
                    }
                    if (checkInheritedReadonly && targetVars.IsReadonlyBinding(localDeclarationTarget))
                    {
                        await DiagnoseReadonlyAsync(followsLocalNameref ? sourceName : declarationName).ConfigureAwait(false);
                        continue;
                    }
                    localReferenceAssigned = options.Nameref || initializingReference;
                }
                if ((options.Nameref || (operand.CompoundApplied || local) && options.NamerefChange == DeclarationAttributeChange.Set)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                    continue;
                }
                var resolvedName = targetVars.ResolveNamerefName(name);
                var arrayRemovalName = name;
                if (clearIndexedElement && !removeElementNameref)
                {
                    if (resolvedName.IsEmpty)
                        resolvedName = name;
                    // A refusal names an existing operand. A newly created array names its binding.
                    arrayRemovalName = targetVars.PrepareIndexedAttributeRemoval(resolvedName, operand.Value != null)
                        ? resolvedName : sourceName;
                }
                if (options.IndexedChange == DeclarationAttributeChange.Clear && targetVars.IndexedArrays.ContainsKey(resolvedName)
                    || options.AssociativeChange == DeclarationAttributeChange.Clear && targetVars.AssociativeArrays.ContainsKey(resolvedName))
                {
                    await DiagnoseAsync(arrayRemovalName, "cannot destroy array variables in this way").ConfigureAwait(false);
                    continue;
                }
                // Global element references and +n keep attribute ownership separate from the active write owner.
                VariableBinding? referentAttributes = global && reference.Kind != VariableReferenceKind.Name
                    ? new VariableBinding(VariableReference.ForName(name), targetVars) : null;
                VariableReference? referentElement = !operand.CompoundApplied && operand.Value != null
                    && referentAttributes != null
                    ? assignsGlobalElement ? VariableReference.ForElement(sourceName, reference.Subscript) : reference
                    : null;
                if (!local && options.NamerefChange == DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out var finalTarget) && !finalTarget.IsEmpty && (global
                        ? TryResolveGlobalReferent(name, out var referent)
                        : targetVars.TryResolveBinding(name, selectOwner: null, out referent)))
                {
                    if (assignsGlobalElement && referent.Reference.Kind != VariableReferenceKind.Name)
                    {
                        await DiagnoseComposedElementAsync(referent.Reference, sourceName, operand).ConfigureAwait(false);
                        continue;
                    }
                    referentAttributes = referent with { Reference = VariableReference.ForName(referent.Name) };
                    if (!operand.CompoundApplied && operand.Value != null && reference.Kind == VariableReferenceKind.Name
                        && referent.Reference.Kind != VariableReferenceKind.Name)
                        referentElement = referent.Reference;
                }
                if (assignsGlobalElement && referentAttributes is { } globalAttributes)
                {
                    var owner = globalAttributes.Owner;
                    var globalName = globalAttributes.Name;
                    var kind = arrayKind == ArrayAssignmentKind.Associative
                        || arrayKind == ArrayAssignmentKind.Auto && owner.AssociativeArrays.ContainsKey(globalName)
                        ? ArrayKind.Associative : ArrayKind.Indexed;
                    if ((globalName != name || !ReferenceEquals(owner, targetVars)) && owner.IsReadonly(globalName))
                    {
                        await DiagnoseReadonlyAsync(sourceName).ConfigureAwait(false);
                        continue;
                    }
                    var initialize = initializeGlobalArray || options.NamerefChange == DeclarationAttributeChange.Clear
                        && (!owner.HasBinding(globalName) || globalAttributes.IsUnsetNameref);
                    var declared = initialize
                        ? owner.TryAssignResolvedArray(globalName, [], kind, initializeReadonly: false)
                        : owner.TryDeclareResolvedArray(globalName, kind, ScalarArrayPromotion.RetainValue);
                    if (!declared)
                    {
                        await DiagnoseAsync(sourceName, ArrayConversionMessage(kind)).ConfigureAwait(false);
                        continue;
                    }
                }
                else if ((!operand.CompoundApplied || convertedReference) && !local && arrayKind != ArrayAssignmentKind.Auto)
                {
                    var kind = arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed;
                    var arrayDeclarationBinding = referentAttributes ?? new VariableBinding(VariableReference.ForName(name), targetVars);
                    if (!arrayDeclarationBinding.Owner.TryDeclareArray(arrayDeclarationBinding.Name, kind, ScalarArrayPromotion.RetainValue))
                    {
                        await DiagnoseAsync(global && reference.Kind == VariableReferenceKind.Name ? sourceName : name, ArrayConversionMessage(kind)).ConfigureAwait(false);
                        if (arrayAttributeAssignment)
                            await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                        continue;
                    }
                }
                if (!setsNamedAttribute && conflictingArrayKinds)
                {
                    await DiagnoseAsync(name, ArrayConversionMessage(ArrayKind.Indexed)).ConfigureAwait(false);
                    continue;
                }

                var elementAttributesApplied = false;
                var retainUnmarkedPrefix = arrayAttributeAssignment && skipReadonlyAttribute
                    && bindings?.Contains(sourceName) == true;
                var wroteUnmarkedPrefix = false;
                if (!operand.CompoundApplied && !localReferenceAssigned && !options.Nameref
                    && operand.Value is { } expandedInitializer && expandedInitializer.Bytes.Span is [(byte)'(', .., (byte)')']
                    && TrySelectExpandedInitializer(name, reference, targetVars, arrayKind,
                        assignsGlobalElement ? referentAttributes : null, out var initializerTarget, out var initializerKind))
                {
                    // The declared attributes are visible to reads during item expansion.
                    var initializeReadonly = options.Readonly && !initializerTarget.Owner.IsReadonlyBinding(initializerTarget.Name);
                    SetAttributes(initializerTarget.Name, initializerTarget.Owner, assignmentExport);
                    var listSource = ShellSource.FromBytes(expandedInitializer.Bytes[1..^1]);
                    using var syntaxMemory = new MemoryCapacity(Memory.Budget, 0);
                    if (!Parser.TryParseCompoundInitializer(listSource, syntaxMemory, out var items, out var error))
                    {
                        var status = ReaderErrexitEnabled(executionScope) ? 2 : 1;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName),
                            ShellValue.FromText("syntax error in array initializer: " + error.Message)).ConfigureAwait(false);
                        _state.LastExitCode = status;
                        PublishExpansionPipelineStatus(status, executionScope);
                        return status == 2 ? EvaluationResult.Exit(status) : EvaluationResult.DiscardCompoundInitializerUnit();
                    }
                    if (!await AwaitWordExpansionAsync(ExpandAndApplyCompoundArrayItemsAsync(initializerTarget, initializerKind,
                            operand.Operator, items, AssignmentContext.Declaration, executionScope, sourceName, initializeReadonly))
                            .ConfigureAwait(false))
                    {
                        exitCode = 1;
                        if (HasPendingExpansionError)
                            break;
                        if (arrayAttributeAssignment)
                            await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                        continue;
                    }
                    wroteUnmarkedPrefix = retainUnmarkedPrefix && initializerTarget.Name == sourceName
                        && ReferenceEquals(initializerTarget.Owner, commandVars);
                    referentElement = null;
                }
                else if (referentElement is { } elementReference && referentAttributes is { } elementAttributes
                    && operand.Value is { } elementValue)
                {
                    if (assignsGlobalElement && arrayKind == ArrayAssignmentKind.Auto
                        && elementValue.Bytes.Span is [(byte)'(', .., (byte)')'])
                        await ReportBuiltinDiagnosticAsync([ShellValue.FromText("warning")],
                            [operand.Name, operand.AssignmentSeparator, elementValue,
                                ShellValue.FromText(": quoted compound array assignment deprecated")], declarationScope.Context)
                            .ConfigureAwait(false);
                    if (global && !assignsGlobalElement && bindings?.Contains(elementAttributes.Name) == true)
                        elementAttributes = elementAttributes with { Owner = bindings.SavedVariables };
                    if (!await AwaitWordExpansionAsync(AssignReferencedElementAsync(
                            elementReference, elementAttributes, elementValue, operand.Operator, assignsGlobalElement, assignmentExport)).ConfigureAwait(false))
                        break;
                }
                else if (!operand.CompoundApplied)
                {
                    if ((options.Nameref || circularLocalReference) && !localReferenceAssigned)
                    {
                        var target = namerefTarget ?? ShellValue.Empty;
                        Memory.Charge(checked(4L * target.Bytes.Length));
                        targetVars.SetCaseAttributes(name, options.CaseChange);
                        if (!targetVars.TrySetNameref(name, target, Memory))
                        {
                            exitCode = 1;
                            continue;
                        }
                    }
                    else if (operand.Value is { } value && !localReferenceAssigned)
                    {
                        var writeReference = reference;
                        var writeVariables = targetVars;
                        var declarationWrite = WordExpansion.DeclarationWrite.Ordinary;
                        if (global && arrayKind == ArrayAssignmentKind.Indexed)
                        {
                            // Bash writes element zero through the active caller binding, even if it is readonly.
                            var activeName = declarationScope.Variables.ResolveNamerefName(sourceName);
                            if (activeName.IsEmpty)
                            {
                                await WriteInvalidIdentifierDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.Empty,
                                    _builtinDiagnostics, ClosedStderrHandling.AttemptWrite).ConfigureAwait(false);
                                if (arrayAttributeAssignment)
                                    await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                                continue;
                            }
                            writeReference = VariableReference.ForElement(sourceName, ShellSource.FromText("0"));
                            writeVariables = declarationScope.Variables;
                            if (name == sourceName && globalBindings?.Contains(sourceName) == true)
                                writeVariables = globalBindings.SavedVariables;
                            else if (prefixNames != null)
                            {
                                var initializerScope = executionScope with
                                {
                                    ExpansionWriteTarget = new ScopedEvaluation.ExpansionBindings(declarationScope.Variables, prefixNames)
                                };
                                writeVariables = initializerScope.GetExpansionWriteTarget(sourceName) ?? commandVars;
                                activeName = writeVariables.ResolveNamerefName(sourceName);
                            }
                            if (writeVariables.AssociativeArrays.ContainsKey(activeName))
                            {
                                // Keep the active binding's attributes. Append uses its element zero.
                                IReadOnlyList<ShellValue> prior = operand.Operator == AssignmentOperator.Append
                                    && writeVariables.TryGetExpansionValue(activeName, out var previous) ? [previous] : [];
                                _ = writeVariables.TryAssignResolvedArray(activeName, prior, ArrayKind.Indexed, initializeReadonly: true);
                            }
                            declarationWrite = WordExpansion.DeclarationWrite.ActiveGlobalIndexed;
                        }
                        else if (!local && reference.Kind == VariableReferenceKind.Name
                            && options.NamerefChange == DeclarationAttributeChange.Clear
                            && targetVars.TryGetNameref(name, out _))
                        {
                            var resolved = global
                                ? TryResolveGlobalReferent(name, out var binding)
                                : targetVars.TryResolveBinding(name, selectOwner: null, out binding);
                            if (!resolved)
                            {
                                await DiagnoseAsync(name, "invalid name reference").ConfigureAwait(false);
                                continue;
                            }
                            writeReference = binding.Reference;
                            writeVariables = writeReference.Kind == VariableReferenceKind.Name ? binding.Owner : commandVars;
                        }
                        else if (!arrayAttributeAssignment && options.Builtin == BuiltinKind.Export
                            && options.ExportChange == DeclarationAttributeChange.Clear && (prefixNames?.Contains(name) == true) && !(bindings?.SavedVariables ?? declarationScope.Variables).TryGetNameref(name, out _))
                        {
                            // Explicit export -n writes use the persistent value, including append, but keep its attributes.
                            writeVariables = (bindings?.SavedVariables ?? declarationScope.Variables);
                        }
                        if (writeReference.Kind == VariableReferenceKind.Name && options.NamerefChange != DeclarationAttributeChange.Set)
                        {
                            var failure = writeVariables.GetNamerefAssignmentFailure(sourceName, writeReference.Name, value, Memory);
                            if (failure != NamerefAssignmentFailure.None)
                            {
                                // Declarations recover and visit later operands; indirect unset targets remain unchanged.
                                if (failure == NamerefAssignmentFailure.InvalidTarget)
                                    await DiagnoseQuotedAsync(value, "not a valid identifier").ConfigureAwait(false);
                                if (options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                                {
                                    // Attribute-setting also diagnoses the unresolved name; export -n only fails.
                                    exitCode = 1;
                                    if (options.ExportChange != DeclarationAttributeChange.Clear)
                                        await DiagnoseQuotedAsync(ShellValue.Empty, "not a valid identifier").ConfigureAwait(false);
                                }
                                continue;
                            }
                        }
                        if (reference.Kind != VariableReferenceKind.Name && options.Readonly)
                        {
                            // Element declarations create the array and set attributes before selecting its slot.
                            var attributeName = targetVars.ResolveNamerefName(name);
                            var kind = targetVars.AssociativeArrays.ContainsKey(attributeName) ? ArrayKind.Associative : ArrayKind.Indexed;
                            if (!targetVars.TryDeclareArray(attributeName, kind, ScalarArrayPromotion.RetainValue))
                            {
                                await DiagnoseAsync(name, "invalid name reference").ConfigureAwait(false);
                                continue;
                            }
                            SetAttributes(attributeName, targetVars, assignmentExport);
                            elementAttributesApplied = true;
                        }
                        // Direct element writes outlive a named prefix; writes through a nameref stay temporary.
                        if (reference.Kind != VariableReferenceKind.Name && bindings?.Contains(writeReference.Name) == true
                            && !commandVars.TryGetNameref(writeReference.Name, out _))
                            writeVariables = bindings.SavedVariables;
                        var writeScope = global ? declarationScope : executionScope;
                        var writeTarget = await AwaitWordExpansionAsync(WordExpansion.ResolveDeclarationReferenceAsync(
                            writeReference, writeScope, writeVariables,
                            reference.Kind != VariableReferenceKind.Name ? commandVars : writeVariables)).ConfigureAwait(false);
                        if (HasPendingExpansionError || writeTarget == null
                            || !await TryAssignDeclarationValueAsync(writeTarget, writeReference,
                                writeScope, writeVariables, value, operand.Operator,
                                declarationWrite, local && reference.Kind != VariableReferenceKind.Name ? sourceName : declarationName,
                                applyAssignmentAttributes: true).ConfigureAwait(false))
                        {
                            exitCode = 1;
                            if (HasPendingExpansionError) break;
                            if (arrayAttributeAssignment)
                                await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                            continue;
                        }
                        wroteUnmarkedPrefix = retainUnmarkedPrefix && writeTarget.Name == sourceName
                            && ReferenceEquals(writeVariables, commandVars);
                    }
                    else if (inferElementArray)
                    {
                        // The selector declares the binding's kind and leaves the index unevaluated.
                        if (!detachElementNameref)
                        {
                            if (targetVars.TryGetNameref(name, out _))
                                targetVars.RecreateDeclarationArray(name, ArrayKind.Indexed);
                            else if (!targetVars.AssociativeArrays.ContainsKey(name)
                                && !targetVars.TryDeclareResolvedArray(name, ArrayKind.Indexed, ScalarArrayPromotion.RetainValue))
                            {
                                exitCode = 1;
                                continue;
                            }
                        }
                    }
                    else if (!local && !(options.Builtin == BuiltinKind.Export && options.ExportChange == DeclarationAttributeChange.Clear)
                        && !targetVars.TryDeclareName(resolvedName.IsEmpty ? name : resolvedName))
                    {
                        exitCode = 1;
                        continue;
                    }
                }

                // Nonlocal +n writes through the referent before detaching the reference.
                if (!keepLocalNameref && options.NamerefChange == DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out var formerTarget))
                {
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseReadonlyAsync(declarationName).ConfigureAwait(false);
                        continue;
                    }
                    if (formerTarget.IsEmpty || local) targetVars.TryDeclareName(name);
                    else targetVars.TrySetScalar(name, formerTarget);
                }
                if (local && options.Nameref && operand.Value == null && prefixNames?.Contains(name) == true)
                    continue; // A valueless -n on a prefix does not create a function local.
                if (selfNameref && namerefReference is { } assignedReference && assignedReference.Name == name)
                    await ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                var attributeBinding = referentAttributes
                    ?? (local && !options.Nameref && targetVars.TryResolveBinding(name, null, out var localBinding)
                        ? localBinding : new VariableBinding(VariableReference.ForName(name), targetVars));
                if (referentElement == null
                    && (global || reference.Kind == VariableReferenceKind.Name || bindings?.Contains(attributeBinding.Name) != true))
                {
                    if (elementAttributesApplied)
                        PublishAttributes(attributeBinding.Name, attributeBinding.Owner, global, local, assignmentExport);
                    else
                        await ApplyAttributesAsync(attributeBinding.Name, attributeBinding.Owner, global, local, assignmentExport).ConfigureAwait(false);
                }
                if (wroteUnmarkedPrefix && bindings != null)
                {
                    // The assignment persists without carrying export from the command prefix.
                    commandVars.SetExported(sourceName, bindings.SavedVariables.IsExported(sourceName));
                    bindings.RetainBinding(sourceName, Memory, _cancellationToken);
                }
                else if (arrayAttributeAssignment)
                    await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
            }

            return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), declarationNegated).ConfigureAwait(false);

            async Task<bool> TryRejectLocalElementInitializerAsync(VariableReference reference, PreparedDeclarationOperand operand,
                FunctionContext function, bool setsNameref)
            {
                var name = reference.Name;
                var emptySelector = reference.Subscript.IsEmpty;
                var clearsNameref = options.NamerefChange == DeclarationAttributeChange.Clear;
                var existing = function.Locals.Declares(name);
                if (!setsNameref && !existing)
                    return false;
                var hasReference = commandVars.TryGetNameref(name, out var previous);
                var prepareTarget = setsNameref && (!existing || !hasReference && commandVars.GetArrayMetadata(name) == null);
                if (existing && hasReference && commandVars.TryResolveBinding(name, selectOwner: null, out var reached))
                {
                    if (reached.Reference.Kind == VariableReferenceKind.Name)
                    {
                        var missing = !reached.Owner.HasBindingOrAttributes(reached.Name);
                        // A missing target of a reference owned by this frame can become another local.
                        var localizesMissing = missing && commandVars.TryResolveBinding(name, selectOwner: null,
                            BindingResolution.LastNameref, out var last) && function.Locals.Declares(last.Name);
                        if (localizesMissing)
                            prepareTarget = emptySelector && (setsNameref || clearsNameref || conflictingArrayKinds);
                        else if (setsNameref || clearsNameref || missing || reached.IsUnsetNameref)
                        {
                            await PrepareAndRejectNamerefArrayAsync(!previous.IsEmpty).ConfigureAwait(false);
                            return true;
                        }
                    }
                    else if (setsNameref && reached.Reference.Kind == VariableReferenceKind.Element)
                        prepareTarget = true;
                }

                if (!setsNameref && !prepareTarget)
                    return false;
                var declaration = new VariableOperand(VariableOperandKind.Name, reference, ShellValue.Empty);
                var attributes = setsNameref || conflictingArrayKinds ? default : GetAttributeChanges(options.ExportChange);
                if (prepareTarget && !function.Locals.TryDeclare(declarationScope.Variables, commandVars, declaration,
                        prefixNames, options.ArrayKind, isNameref: false, options.Inherit, caseChange: default,
                        attributes, Memory, function.Enclosing))
                    await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                else if (setsNameref)
                    await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                else if (conflictingArrayKinds)
                    await DiagnoseAsync(name, ArrayConversionMessage(ArrayKind.Indexed)).ConfigureAwait(false);
                else
                    await DiagnoseBadArraySubscriptAsync(operand.Name).ConfigureAwait(false);
                return true;

                async Task PrepareAndRejectNamerefArrayAsync(bool initialized)
                {
                    var kind = options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed;
                    var readonlyBinding = commandVars.IsReadonlyBinding(name);
                    var initializer = operand.Value.GetValueOrDefault();
                    var invalidTarget = !setsNameref && !clearsNameref
                        && !TryParseDeclarationReference(ShellSource.FromBytes(initializer.Bytes), out _);
                    var destroysArray = !setsNameref && (kind == ArrayKind.Indexed
                        ? options.IndexedChange == DeclarationAttributeChange.Clear
                        : options.AssociativeChange == DeclarationAttributeChange.Clear);
                    var applyAttributes = !setsNameref && !invalidTarget && !readonlyBinding && !destroysArray && !conflictingArrayKinds;
                    function.Locals.PrepareRejectedNamerefArray(commandVars, name, kind, initialized,
                        applyAttributes ? GetAttributeChanges(options.ExportChange) : default, Memory);
                    if (setsNameref)
                        await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                    else if (invalidTarget)
                        await DiagnoseQuotedAsync(initializer, "invalid variable name for name reference").ConfigureAwait(false);
                    else if (readonlyBinding)
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                    else if (destroysArray)
                        await DiagnoseAsync(name, "cannot destroy array variables in this way").ConfigureAwait(false);
                    else if (conflictingArrayKinds)
                        await DiagnoseAsync(name, ArrayConversionMessage(ArrayKind.Indexed)).ConfigureAwait(false);
                    else if (emptySelector)
                        await DiagnoseBadArraySubscriptAsync(operand.Name).ConfigureAwait(false);
                    else
                    {
                        exitCode = 1;
                        var target = await AwaitWordExpansionAsync(WordExpansion.ResolveRejectedNamerefSelectorAsync(reference, executionScope))
                            .ConfigureAwait(false);
                        if (target != null && !HasPendingExpansionError)
                            await DiagnoseQuotedAsync(operand.Name, "not a valid identifier").ConfigureAwait(false);
                    }
                }
            }

            async Task RejectEmptyElementInitializerAsync(VariableReference reference, PreparedDeclarationOperand operand, bool setsNameref)
            {
                var name = reference.Name;
                var variables = options.Global ? SelectGlobalOwner(name) : commandVars;
                var indexed = variables.IndexedArrays.ContainsKey(name);
                var associative = variables.AssociativeArrays.ContainsKey(name);
                var hasReference = variables.TryGetNameref(name, out var referenceValue);
                if (variables.IsReadonlyBinding(name))
                {
                    if (hasReference && !setsNameref)
                        await RejectReadonlyElementNamerefAsync(name, variables).ConfigureAwait(false);
                    else
                        await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
                    return;
                }
                if (setsNameref && (indexed || associative))
                {
                    await DiagnoseAsync(name, NamerefArrayError).ConfigureAwait(false);
                    return;
                }
                var kind = options.ArrayKind == ArrayAssignmentKind.Associative
                    || options.ArrayKind == ArrayAssignmentKind.Auto && associative
                    ? ArrayKind.Associative : ArrayKind.Indexed;
                // An absent binding starts initialized. A declared-but-unset binding retains its state.
                if (!variables.HasBinding(name) || hasReference && !referenceValue.IsEmpty)
                {
                    IReadOnlyList<ShellValue> initial = !setsNameref || referenceValue.IsEmpty ? []
                        : kind == ArrayKind.Indexed ? [referenceValue] : [ShellValue.FromText("0"), referenceValue];
                    _ = variables.TryAssignResolvedArray(name, initial, kind, initializeReadonly: true);
                }
                else if (!variables.TryDeclareResolvedArray(name, kind, ScalarArrayPromotion.RetainValue))
                {
                    await DiagnoseAsync(name, ArrayConversionMessage(kind)).ConfigureAwait(false);
                    return;
                }
                if (hasReference && !setsNameref)
                    await ReportElementNamerefRemovalAsync(name).ConfigureAwait(false);
                if (conflictingArrayKinds)
                {
                    await DiagnoseAsync(name, ArrayConversionMessage(ArrayKind.Indexed)).ConfigureAwait(false);
                    return;
                }
                if (setsNameref)
                {
                    variables.SetIntegerBinding(name, false);
                    variables.SetCaseAttributes(name, new(BindingAttributes.None, BindingAttributes.Case));
                }
                await ApplyAttributesAsync(name, variables, options.Global, local: false, options.ExportChange).ConfigureAwait(false);
                await DiagnoseBadArraySubscriptAsync(operand.Name).ConfigureAwait(false);
            }

            Task DiagnoseBadArraySubscriptAsync(ShellValue spelling)
            {
                exitCode = 1;
                return ReportBuiltinDiagnosticAsync([spelling], [ShellValue.FromText("bad array subscript")], declarationScope.Context);
            }

            async Task<bool> TryRejectEmptyElementAfterNamerefClearAsync(BindingName source, PreparedDeclarationOperand operand)
            {
                if (!TryResolve(BindingResolution.Referent, out var target))
                    return false;

                var kind = options.ArrayKind == ArrayAssignmentKind.Associative
                    || options.ArrayKind == ArrayAssignmentKind.Auto && target.Owner.AssociativeArrays.ContainsKey(target.Name)
                    ? ArrayKind.Associative : ArrayKind.Indexed;
                var missingReferent = false;
                if (target.IsUnsetNameref)
                {
                    // An unset chain recreates the operand's binding rather than the terminal alias.
                    var owner = options.Global ? SelectGlobalOwner(source) : commandVars;
                    owner.RecreateDeclarationArray(source, kind, initialize: true);
                    target = new VariableBinding(VariableReference.ForName(source), owner);
                }
                else
                {
                    if (!TryResolve(BindingResolution.LastNameref, out var holder))
                        return false;
                    if (holder.Owner.IsReadonlyBinding(holder.Name)
                        || target.Reference.Kind == VariableReferenceKind.Name && target.Owner.IsReadonlyBinding(target.Name))
                    {
                        await DiagnoseReadonlyAsync(source).ConfigureAwait(false);
                        return true;
                    }
                    if (target.Reference.Kind != VariableReferenceKind.Name)
                    {
                        await DiagnoseComposedElementAsync(target.Reference, source, operand).ConfigureAwait(false);
                        return true;
                    }
                    if (options.AssociativeChange == DeclarationAttributeChange.Clear && target.Owner.AssociativeArrays.ContainsKey(target.Name))
                    {
                        await DiagnoseAsync(source, "cannot destroy array variables in this way").ConfigureAwait(false);
                        return true;
                    }
                    if (!target.Owner.HasBinding(target.Name))
                    {
                        if (holder.Owner.TryGetNameref(holder.Name, out var formerTarget))
                        {
                            if (!holder.Owner.TryRemoveNamerefAttribute(holder.Name))
                            {
                                await DiagnoseReadonlyAsync(source).ConfigureAwait(false);
                                return true;
                            }
                            holder.Owner.TrySetScalar(holder.Name, formerTarget);
                            missingReferent = true;
                        }
                        _ = target.Owner.TryAssignResolvedArray(target.Name, [], kind, initializeReadonly: false);
                    }
                    else if (!target.Owner.TryDeclareResolvedArray(target.Name, kind, ScalarArrayPromotion.RetainValue))
                    {
                        await DiagnoseAsync(source, ArrayConversionMessage(kind)).ConfigureAwait(false);
                        return true;
                    }
                }

                await ApplyAttributesAsync(target.Name, target.Owner, options.Global, local: false, options.ExportChange)
                    .ConfigureAwait(false);
                exitCode = 1;
                var message = ShellValue.FromText("bad array subscript");
                if (missingReferent)
                    await ReportBuiltinDiagnosticAsync([GetDiagnosticName(target.Name),
                        ShellValue.FromBytes(operand.Name.Bytes[source.ByteLength..])], [message],
                        declarationScope.Context).ConfigureAwait(false);
                else
                    await ReportBuiltinDiagnosticAsync([operand.Name], [message], declarationScope.Context).ConfigureAwait(false);
                return true;

                bool TryResolve(BindingResolution resolution, out VariableBinding binding) => options.Global
                    ? declarationScope.Context.TryResolveGlobalBinding(source, declarationScope.Variables,
                        commandVars, prefixNames, globalBindings, resolution, out binding)
                    : commandVars.TryResolveBinding(source, selectOwner: null, resolution, out binding);
            }

            async Task RejectReadonlyElementNamerefAsync(BindingName name, Variables variables)
            {
                variables.DetachNamerefToUnassigned(name);
                await ReportElementNamerefRemovalAsync(name).ConfigureAwait(false);
                await DiagnoseReadonlyAsync(name).ConfigureAwait(false);
            }

            Task ReportElementNamerefRemovalAsync(BindingName name) => ReportBuiltinDiagnosticAsync([ShellValue.FromText("warning")],
                [GetDiagnosticName(name), ShellValue.FromText(": removing nameref attribute")], declarationScope.Context);

            Variables SelectGlobalOwner(BindingName name) =>
                declarationScope.Context.GetGlobalBindingOwner(name, declarationScope.Variables, globalBindings);

            bool TryResolveGlobalReferent(BindingName name, out VariableBinding binding) =>
                declarationScope.Context.TryResolveGlobalBinding(name, declarationScope.Variables,
                    declarationScope.Variables, prefixes: null, globalBindings, BindingResolution.Referent, out binding);

            bool TrySelectExpandedInitializer(BindingName name, VariableReference reference, Variables variables, ArrayAssignmentKind arrayKind,
                VariableBinding? declaredArray, out VariableBinding binding, out ArrayKind kind)
            {
                kind = default;
                binding = default;
                if (arrayKind == ArrayAssignmentKind.Auto
                    && (reference.Kind != VariableReferenceKind.Name || options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly))
                    return false;
                if (declaredArray is { } selected)
                    binding = selected;
                else if (!variables.TryResolveBinding(name, selectOwner: null, out binding)
                    || arrayKind == ArrayAssignmentKind.Auto && binding.Reference.Kind != VariableReferenceKind.Name)
                    return false;
                if (binding.Owner.AssociativeArrays.ContainsKey(binding.Name))
                {
                    kind = ArrayKind.Associative;
                    return true;
                }
                kind = ArrayKind.Indexed;
                return binding.Owner.IndexedArrays.ContainsKey(binding.Name);
            }

            bool TrySaveLocalOptions(ShellValue operand)
            {
                if (localBuiltinFrame == null || !operand.Bytes.Span.SequenceEqual("-"u8))
                    return false;
                localBuiltinFrame.SaveErrexit(_state.ErrexitEnabled);
                return true;
            }

            async Task<bool> AssignReferencedElementAsync(
                VariableReference reference, VariableBinding attributes, ShellValue value, AssignmentOperator operation,
                bool assignsGlobalElement, DeclarationAttributeChange exportChange)
            {
                // Attributes select the declaration binding; element writes use the active caller array.
                if (!assignsGlobalElement && attributes.Owner.IsReadonly(attributes.Name))
                {
                    await DiagnoseReadonlyAsync(attributes.Name).ConfigureAwait(false);
                    return true; // Visit later operands, and still detach +n, without evaluating the selector.
                }
                SetAttributes(attributes.Name, attributes.Owner, exportChange);

                var selectionScope = prefixNames == null ? executionScope : executionScope with
                {
                    ExpansionWriteTarget = new ScopedEvaluation.ExpansionBindings(bindings?.SavedVariables ?? declarationScope.Variables, prefixNames)
                };
                var variables = bindings?.Contains(reference.Name) == true ? bindings.SavedVariables : declarationScope.Variables;
                if (prefixNames != null && variables.TryGetNameref(reference.Name, out _)
                    && (prefixNames.Contains(reference.Name) || selectionScope.GetExpansionWriteTarget(reference.Name) == null))
                    variables = commandVars; // Indirect writes stay inside a prefix, including one hiding the nameref itself.
                VariableBinding? activeBinding = assignsGlobalElement
                    && variables.TryResolveBinding(reference.Name, selectOwner: null, out var selected) ? selected : null;
                var activeUnsetNameref = activeBinding is { IsUnsetNameref: true };
                // Prefixes affect selector arithmetic and negative-index bounds, not the stored value used by +=.
                var target = await WordExpansion.ResolveDeclarationReferenceAsync(
                    reference, selectionScope, variables, commandVars).ConfigureAwait(false);
                if (HasPendingExpansionError)
                    return false;
                if (target is { Name.IsEmpty: true }
                    && (activeUnsetNameref || activeBinding is { Reference.Kind: not VariableReferenceKind.Name }))
                {
                    var invalidName = activeUnsetNameref ? ShellValue.Empty : activeBinding.GetValueOrDefault().Reference.ToValue(Memory);
                    await DiagnoseQuotedAsync(invalidName, "not a valid identifier").ConfigureAwait(false);
                    return true;
                }
                if (target == null)
                    exitCode = 1;
                else if (!await TryAssignDeclarationValueAsync(target, reference, declarationScope, variables,
                    value, operation, WordExpansion.DeclarationWrite.Ordinary, reference.Name,
                    applyAssignmentAttributes: !assignsGlobalElement).ConfigureAwait(false))
                    exitCode = 1;
                return true;
            }

            async Task<bool> TryAssignDeclarationValueAsync(
                WordExpansion.ResolvedVariableReference target, VariableReference reference, ScopedEvaluation writeScope, Variables destination,
                ShellValue value, AssignmentOperator operation, WordExpansion.DeclarationWrite declarationWrite, BindingName diagnosticName,
                bool applyAssignmentAttributes)
            {
                if (declarationWrite != WordExpansion.DeclarationWrite.ActiveGlobalIndexed && destination.IsReadonly(target.Name))
                {
                    if (reference.Kind != VariableReferenceKind.Name)
                    {
                        // A newly applied -r rejects the element write but leaves the declaration successful.
                        await ReportCommandDiagnosticAsync([GetDiagnosticName(diagnosticName)], [ShellValue.FromText("readonly variable")], declarationScope)
                            .ConfigureAwait(false);
                        return true;
                    }
                    await DiagnoseReadonlyAsync(diagnosticName).ConfigureAwait(false);
                    return false;
                }
                if (applyAssignmentAttributes)
                {
                    if (!target.Name.IsEmpty && options.IntegerChange != DeclarationAttributeChange.Unchanged)
                        destination.SetIntegerBinding(target.Name, options.IntegerChange == DeclarationAttributeChange.Set);
                    destination.SetCaseAttributes(target.Name, options.CaseChange);
                }
                return await target.AssignValueAsync(writeScope, destination, reference.Name, value, operation,
                    ShellValue.FromText(commandName), declarationWrite).ConfigureAwait(false) is not null;
            }

            async Task ApplyNamedAttributesAsync(BindingName name, ShellValue? assignmentPrefix)
            {
                if (assignmentPrefix is { } value)
                {
                    // Named attributes promote the original command prefix into the reached array.
                    var resolved = commandVars.ResolveNamerefName(name);
                    if (!resolved.IsEmpty)
                    {
                        if (commandVars.IsReadonlyBinding(resolved))
                            await ReportCommandDiagnosticAsync([GetDiagnosticName(resolved)], [ShellValue.FromText("readonly variable")], declarationScope)
                                .ConfigureAwait(false);
                        else
                            commandVars.SetResolvedValuePreservingArray(resolved, value, Memory);
                    }
                }
                await ApplyAttributesAsync(name, commandVars, global: false, local: false, options.ExportChange).ConfigureAwait(false);
            }

            async Task ApplyAttributesAsync(
                BindingName name, Variables variables, bool global, bool local, DeclarationAttributeChange exportChange)
            {
                if (skipReadonlyAttribute)
                    return;
                var attributeName = options.Nameref || local ? name : variables.ResolveNamerefName(name);
                if (attributeName.IsEmpty)
                {
                    // Neither an unset reference nor an element provides a whole binding to mark.
                    if (options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly
                        && exportChange != DeclarationAttributeChange.Clear)
                    {
                        var command = ShellValue.FromText(commandName);
                        if (variables.TryResolveBinding(name, selectOwner: null, out var binding)
                            && binding.Reference.Kind != VariableReferenceKind.Name)
                        {
                            var target = binding.Reference;
                            await ReportExpansionDiagnosticAsync([command],
                                [ShellValue.FromText("`"), GetDiagnosticName(target.Name), ShellValue.FromText("["),
                                    ShellValue.FromBytes(target.Subscript.Bytes), ShellValue.FromText("]': not a valid identifier")])
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await WriteInvalidIdentifierDiagnosticAsync(command, ShellValue.Empty, _builtinDiagnostics,
                                ClosedStderrHandling.AttemptWrite).ConfigureAwait(false);
                        }
                    }
                    return;
                }
                SetAttributes(attributeName, variables, exportChange);
                PublishAttributes(name, variables, global, local, exportChange);
            }

            void PublishAttributes(
                BindingName name, Variables variables, bool global, bool local, DeclarationAttributeChange exportChange)
            {
                if (global && ReferenceEquals(variables, commandVars))
                {
                    // Explicit global declarations already write to their selected caller binding.
                    if (!options.Global)
                        bindings?.Retain(name, Memory, _cancellationToken);
                    // Active fallback attributes can promote this declaration's prefix, not the enclosing call's.
                    if (bindings == null && prefixNames?.Contains(name) == true
                        && (exportChange == DeclarationAttributeChange.Set || options.Readonly))
                        declarationScope.Context.FunctionContext?.Locals.Publish(name, variables);
                }
                if (!global && (local || exportChange == DeclarationAttributeChange.Set || options.Readonly))
                {
                    if (local)
                        declarationScope.Context.FunctionContext?.Locals.Publish(name, variables);
                    if (exportChange == DeclarationAttributeChange.Set || options.Readonly)
                        PromoteAttributeBinding(variables, name, declarationScope.Context, bindings);
                }
            }

            void SetAttributes(BindingName name, Variables variables, DeclarationAttributeChange exportChange)
            {
                var (set, clear) = GetAttributeChanges(exportChange);
                variables.UpdateBindingAttributes(name, set, clear);
            }

            (BindingAttributes Set, BindingAttributes Clear) GetAttributeChanges(DeclarationAttributeChange exportChange)
            {
                var (set, clear) = options.CaseChange;
                Include(BindingAttributes.Integer, options.IntegerChange);
                Include(BindingAttributes.Exported, exportChange);
                if (options.Readonly) set |= BindingAttributes.Readonly;
                return (set, clear);

                void Include(BindingAttributes attribute, DeclarationAttributeChange change)
                {
                    if (change == DeclarationAttributeChange.Set)
                        set |= attribute;
                    else if (change == DeclarationAttributeChange.Clear)
                        clear |= attribute;
                }
            }

            Task ReportDeclarationDiagnosticAsync(ReadOnlySpan<ShellValue> message) =>
                WriteDiagnosticAsync([ShellValue.FromText(commandName)], message, _builtinDiagnostics,
                    ClosedStderrHandling.AttemptWrite, GetBuiltinDiagnosticPrefix(declarationScope.Context));

            Task ReportDeclarationOperandAsync(ShellValue subject, string message) =>
                ReportDeclarationDiagnosticAsync([subject, ShellValue.FromText(": " + message)]);

            Task DiagnoseAsync(BindingName subject, string message)
            {
                exitCode = 1;
                return ReportDeclarationOperandAsync(GetDiagnosticName(subject), message);
            }

            Task DiagnoseReadonlyAsync(BindingName subject)
            {
                exitCode = 1;
                return ReportCommandDiagnosticAsync([ShellValue.FromText(commandName)],
                    [GetDiagnosticName(subject), ShellValue.FromText(": readonly variable")], declarationScope);
            }

            Task DiagnoseQuotedAsync(ShellValue subject, string message)
            {
                exitCode = 1;
                return WriteQuotedOperandDiagnosticAsync(ShellValue.FromText(commandName), subject, message,
                    _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, GetBuiltinDiagnosticPrefix(declarationScope.Context));
            }

            Task DiagnoseComposedElementAsync(VariableReference target, BindingName source, PreparedDeclarationOperand operand)
            {
                exitCode = 1;
                return ReportDeclarationDiagnosticAsync([ShellValue.FromText("`"), target.ToValue(Memory),
                    ShellValue.FromBytes(operand.Name.Bytes[source.ByteLength..]), operand.AssignmentSeparator,
                    operand.Value.GetValueOrDefault(), ShellValue.FromText("': not a valid identifier")]);
            }
        }
        async Task<bool> ApplyAssignmentAsync(AssignmentWord assignment)
        {
            if (assignment.ArrayValues is not { } items)
            {
                var value = await WordExpansion.ExpandAssignmentValueAsync(assignment.Value, scope).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return false;
                return await WordExpansion.TryAssignAsync(assignment.Target, value, assignment.Operator, scope).ConfigureAwait(false);
            }

            if (!VariableReference.TryParse(assignment.Target, Memory, out var compoundTarget))
                return false;
            var name = scope.Variables.ResolveNamerefName(compoundTarget.Name);
            if (compoundTarget.Kind != VariableReferenceKind.Name || !name.IsIdentifier)
            {
                await ReportAssignmentTargetFailureAsync(assignment.Target.ToValue(), "cannot assign list to array member", scope)
                    .ConfigureAwait(false);
                return false;
            }
            if (scope.Variables.IsReadonly(name))
            {
                await ReportAssignmentTargetFailureAsync(assignment.Target.ToValue(), "readonly variable", scope).ConfigureAwait(false);
                return false;
            }
            if (!await ValidateCompoundInitializerCapturesAsync(items, scope).ConfigureAwait(false))
                return false;

            var kind = scope.Variables.AssociativeArrays.ContainsKey(name) ? ArrayKind.Associative : ArrayKind.Indexed;
            return await ExpandAndApplyCompoundArrayItemsAsync(
                new VariableBinding(VariableReference.ForName(name), scope.Variables), kind, assignment.Operator, items,
                AssignmentContext.Direct, scope, compoundTarget.Name, initializeReadonly: false).ConfigureAwait(false);
        }
    }

    private EvaluationResult CompleteCommandResult(
        EvaluationResult result,
        CommandCompletion completion,
        bool commandInvoked)
    {
        if (completion != CommandCompletion.ExitEvaluation)
            return result;
        _state.ExitedThroughExec |= commandInvoked;
        return EvaluationResult.Exit(result.ExitCode);
    }

    private CommandResolution ResolveCommandName(ShellValue commandName, CommandLookupMode lookupMode, out string? registeredName)
    {
        if (FindCommandFunction(commandName, lookupMode) is { } function)
        {
            // Functions can have byte-valued names that need no text representation for dispatch.
            registeredName = string.Empty;
            return new CommandResolution.Function(function);
        }

        if (FindCommandBuiltin(commandName, lookupMode) is { } builtin)
        {
            registeredName = builtin.Name;
            return CommandResolution.ForBuiltin(builtin.Kind);
        }

        registeredName = null;
        return CommandResolution.Host.Instance;
    }

    private FunctionBinding? FindCommandFunction(ShellValue name, CommandLookupMode lookupMode) =>
        lookupMode == CommandLookupMode.Normal && _state.TryGetFunction(name, out var function)
            ? function : null;

    private static (string Name, BuiltinKind Kind)? FindCommandBuiltin(ShellValue name, CommandLookupMode lookupMode) =>
        lookupMode != CommandLookupMode.HostOnly && BuiltinClassification.TryResolve(name.Bytes.Span, out var spelling, out var kind)
            ? (spelling, kind) : null;

    /// <summary>
    /// Opens a lexical redirection scope and records ownership of every descriptor it creates.
    /// The command's execution policy determines which bindings can reach the enclosing scope.
    /// </summary>
    private async Task<RedirectionScope> OpenRedirectionScopeAsync(
        IReadOnlyList<Redirection> redirections,
        ScopedEvaluation scope,
        RedirectionPersistence persistence,
        RedirectionLifetime lifetime,
        RedirectionOrigin origin,
        bool reportMoveRestorationFailure)
    {
        if (redirections.Count == 0)
            return new RedirectionScope(this, active: false, [], []);

        if (_state.CurrentRedirectionTable == null)
            throw new InvalidOperationException("Redirection setup requires an enclosing descriptor table.");
        var evaluation = new RedirectionEvaluation(redirections, CaptureDescriptorBindings().Fork(), origin,
            preserveBindings: lifetime == RedirectionLifetime.Evaluation, reportMoveRestorationFailure);
        // Expose the table as setup advances, so later target expansions and their substitutions
        // inherit earlier redirections. Descriptor ownership is finalized after setup succeeds.
        _state.PushRedirectionTable(evaluation.DescriptorTable, evaluation.OpenedDescriptors, evaluation.TouchedDescriptors, Memory);
        try
        {
            await evaluation.EvaluateAsync(scope with { AfterPipelineExitReset = true }, persistence).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            try
            {
                if (error is MemoryBudgetExceededException)
                {
                    Memory.ReleaseUnusedCapacity();
                    await ReportMemoryRefusalAsync("shell").ConfigureAwait(false);
                    _state.SetExpansionError(1);
                }
            }
            finally
            {
                await new RedirectionScope(this, active: true, [], evaluation.ConsumedSources)
                    .CloseAsync(operationFailed: true).ConfigureAwait(false);
            }
            if (error is MemoryBudgetExceededException)
                return new RedirectionScope(this, active: false, [], []);
            throw;
        }
        return new RedirectionScope(this, active: true, evaluation.ReplacedHandles, evaluation.ConsumedSources)
        {
            InputOpenFailed = evaluation.InputOpenFailed,
            HasRestorableMappings = evaluation.TouchedDescriptors.Count != 0
        };

    }

    internal IReadOnlyList<FileDescriptor> CollectDescriptorsAvailableToNestedEvaluation() =>
        CollectDescriptorsAvailable().Descriptors;

    private DescriptorHandleCollector CollectDescriptorsAvailable()
    {
        var descriptors = new DescriptorHandleCollector(new List<FileDescriptor>(), Memory.Workspace);
        descriptors.Add(_stdIn);
        descriptors.Add(_stdOut);
        descriptors.Add(_stdErr);
        descriptors.Add(GetStdInDescriptor());
        descriptors.Add(GetStdOutDescriptor());
        descriptors.Add(GetStdErrDescriptor());
        descriptors.AddRange(_borrowedDescriptors);
        // Each entry captures the complete ancestor union. Older entries add no new handles.
        if (_subshellBorrowedDescriptorScopes.TryPeek(out var borrowedScope))
        {
            foreach (var descriptor in borrowedScope)
                descriptors.Add(descriptor);
        }

        foreach (var descriptor in _state.EnumerateRedirectionHandles())
            descriptors.Add(descriptor);
        return descriptors;
    }

    private DescriptorTable.Snapshot CaptureDescriptorBindings() =>
        _state.CurrentRedirectionTable?.Capture()
        ?? DescriptorTable.Snapshot.Standard.WithStandardStreams(
            ResolveEffectiveStdIn(null),
            ResolveEffectiveStdOut(null),
            ResolveEffectiveStdErr(null));

    /// <summary>
    /// Creates the inherited descriptor context for a command substitution, replacing its standard output with
    /// the capture pipe while leaving ownership of every outer descriptor with this evaluation.
    /// </summary>
    /// <param name="stdOut">The command-substitution capture descriptor.</param>
    internal EvaluationDescriptorSetup CreateCommandSubstitutionDescriptorSetup(FileDescriptor stdOut)
    {
        var inherited = CaptureDescriptorBindings();
        var bindings = inherited.WithStandardStreams(inherited.ResolveBinding(LogicalDescriptor.StdIn),
            new DescriptorBinding(stdOut, IsWriteOnly: true), inherited.ResolveBinding(LogicalDescriptor.StdErr));

        var borrowedDescriptors = CollectDescriptorsAvailable();
        bindings.AddReferencedHandlesTo(ref borrowedDescriptors);
        return new EvaluationDescriptorSetup(bindings, borrowedDescriptors.Descriptors);
    }

    private FileDescriptor ResolveEffectiveStdIn(FileDescriptor? descriptor) =>
        descriptor ?? _state.GetStdInOverride() ?? _stdIn;

    private FileDescriptor ResolveEffectiveStdOut(FileDescriptor? descriptor) =>
        descriptor ?? _state.GetStdOutOverride() ?? _stdOut;

    private FileDescriptor ResolveEffectiveStdErr(FileDescriptor? descriptor) =>
        descriptor ?? _state.GetStdErrOverride() ?? _stdErr;

    /// <summary>Completes every owned close while preserving an earlier scope failure.</summary>
    internal async Task<OperationError?> CloseDescriptorsAsync(IReadOnlyCollection<FileDescriptor> descriptors, bool operationFailed)
    {
        var failure = new DescriptorCloseResult(true, null, null);
        try
        {
            foreach (var descriptor in descriptors)
            {
                DescriptorCloseResult close;
                try
                {
                    var error = await CloseOwnedDescriptorAsync(descriptor).ConfigureAwait(false);
                    close = new DescriptorCloseResult(error == null, error, null);
                }
                catch (Exception ex)
                {
                    close = new DescriptorCloseResult(false, null, ExceptionDispatchInfo.Capture(ex));
                }
                failure = DescriptorCloseResult.FirstFailure(failure, close);
            }
        }
        finally
        {
            if (ReferenceEquals(descriptors, _descriptorCloseBuffer))
                _descriptorCloseBuffer.Clear();
        }

        if (operationFailed)
            return null;
        failure.Exception?.Throw();
        return failure.Error;
    }

    private IReadOnlyCollection<FileDescriptor> CollectRedirectionDescriptorsToClose(IReadOnlySet<FileDescriptor>? excluded)
    {
        _descriptorCloseBuffer.Clear();
        foreach (var descriptor in _state.EnumerateRedirectionHandles())
            if (_pendingDescriptorCleanup.Contains(descriptor) && excluded?.Contains(descriptor) != true)
                _descriptorCloseBuffer.AddPrepared(descriptor);
        return _descriptorCloseBuffer;
    }

    /// <summary>Reports an expected host failure without unwinding shell control flow.</summary>
    internal Task ReportOperationErrorAsync(OperationError error) =>
        WriteOperationDiagnosticAsync(GetStdErrDescriptor(), "shell: ", path: null, error);

    /// <summary>Writes LF-framed host detail through the supplied descriptor, with an optional path.</summary>
    internal Task WriteOperationDiagnosticAsync(FileDescriptor descriptor, string prefix, string? path, OperationError error) =>
        WriteOperationDiagnosticAsync(descriptor, ShellValue.Empty, prefix, path, error, default);

    internal Task WriteRedirectionOperationDiagnosticAsync(FileDescriptor descriptor, string? path, OperationError error,
        EvaluationContext context)
    {
        var coordinates = context.Coordinates;
        var line = coordinates.RedirectionOpenLine ?? coordinates.Line;
        var prefix = path != null && error.Kind != OperationErrorKind.Unknown && coordinates.Locations != null
            ? new SyntaxDiagnosticPrefix(context.DiagnosticPath, SyntaxDiagnosticOrigin.ScriptLine, line == 0 ? null : line) : default;
        return WriteOperationDiagnosticAsync(descriptor, ShellValue.Empty, string.Empty, path, error, prefix);
    }

    internal Task WriteDescriptorAssignmentErrorAsync(FileDescriptor descriptor, ShellValue variable, EvaluationContext context) =>
        WriteOperationDiagnosticAsync(descriptor, variable, string.Empty, path: null,
            new OperationError("cannot assign fd to variable\n"), GetBuiltinDiagnosticPrefix(context));

    internal async Task WriteEmptyDescriptorDiagnosticAsync(FileDescriptor descriptor, LogicalDescriptor? destination,
        bool reportDuplication, EvaluationContext context)
    {
        var path = context.DiagnosticPath;
        var error = new OperationError("Bad file descriptor\n");
        if (reportDuplication)
        {
            await WriteOperationDiagnosticAsync(descriptor, ShellValue.Empty, "redirection error: cannot duplicate fd: ", path: null,
                error, new SyntaxDiagnosticPrefix(path, SyntaxDiagnosticOrigin.Script, line: null))
                .ConfigureAwait(false);
        }
        var line = context.Coordinates.RedirectionOpenLine ?? context.Coordinates.Line;
        var prefix = new SyntaxDiagnosticPrefix(path, SyntaxDiagnosticOrigin.Script, line == 0 ? null : line);
        await WriteOperationDiagnosticAsync(descriptor, ShellValue.Empty, string.Empty, destination?.ToString() ?? string.Empty,
            error, prefix).ConfigureAwait(false);
    }

    private Task WriteOperationDiagnosticAsync(FileDescriptor descriptor, ShellValue name, string prefix, string? path,
        OperationError error, SyntaxDiagnosticPrefix sourcePrefix) =>
        WriteOperationDiagnosticAsync(descriptor, name, prefix, path, error, sourcePrefix,
            relativeDirectory: null, detailPrefix: string.Empty);

    private async Task<DescriptorWriteResult?> WriteOperationDiagnosticAsync(FileDescriptor descriptor, ShellValue name,
        string prefix, string? path, OperationError error, SyntaxDiagnosticPrefix sourcePrefix, ShellPath? relativeDirectory,
        string detailPrefix)
    {
        var result = await TryWriteOperationDiagnosticAsync(descriptor, name, prefix, path, error, sourcePrefix,
            relativeDirectory, detailPrefix).ConfigureAwait(false);
        if (result == null)
        {
            // The operation already failed. Keep the bounded message on its status and cleanup path.
            await ExecuteWriteAsync(descriptor, ShellMemoryRefusalDiagnostic).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>Formats directly into the admitted output buffer. A null result means admission refused.</summary>
    private async Task<DescriptorWriteResult?> TryWriteOperationDiagnosticAsync(
        FileDescriptor descriptor, ShellValue name, string prefix, string? path, OperationError error,
        SyntaxDiagnosticPrefix sourcePrefix, ShellPath? relativeDirectory, string detailPrefix)
    {
        var encoding = Utf8Text.StrictUtf8;
        var pathText = path.AsSpan();
        if (relativeDirectory != null && pathText.StartsWith("./", StringComparison.Ordinal))
            pathText = pathText[2..];
        var directoryText = (relativeDirectory?.Path).AsSpan();
        var directoryLength = encoding.GetByteCount(directoryText);
        var separatorLength = relativeDirectory is { IsRoot: false } ? 1 : 0;
        var prefixLength = encoding.GetByteCount(prefix);
        var pathLength = encoding.GetByteCount(pathText);
        var detailPrefixLength = encoding.GetByteCount(detailPrefix);
        var detailLength = encoding.GetByteCount(error.Message.AsSpan());
        var nameLength = name.IsEmpty ? 0 : (long)name.Bytes.Length + 2;
        var length = nameLength + prefixLength + directoryLength + separatorLength + pathLength
            + (path is null ? 0 : 2) + detailPrefixLength + detailLength + sourcePrefix.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            return null;

        using var memory = reservation;
        using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
        sourcePrefix.AppendTo(diagnostic);
        if (!name.IsEmpty)
            diagnostic.Append(name.Bytes).Append(": "u8);
        encoding.GetBytes(prefix.AsSpan(), diagnostic.AppendSpan(prefixLength));
        if (path != null)
        {
            encoding.GetBytes(directoryText, diagnostic.AppendSpan(directoryLength));
            if (separatorLength != 0)
                diagnostic.Append("/"u8);
            encoding.GetBytes(pathText, diagnostic.AppendSpan(pathLength));
            diagnostic.Append(": "u8);
        }
        encoding.GetBytes(detailPrefix.AsSpan(), diagnostic.AppendSpan(detailPrefixLength));
        encoding.GetBytes(error.Message.AsSpan(), diagnostic.AppendSpan(detailLength));
        return await ExecuteWriteAsync(descriptor, diagnostic.ToMemory()).ConfigureAwait(false);
    }

    private async Task<EvaluationResult> ApplyCloseErrorAsync(EvaluationResult result, OperationError? error)
    {
        if (error is not { } failure)
            return result;
        await ReportOperationErrorAsync(failure).ConfigureAwait(false);
        return result.WithExitCode(1);
    }

    internal Task<ShellValue> ExpandWordValueAsync(Word word, ScopedEvaluation scope) =>
        AwaitWordExpansionAsync(WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar));

    /// <summary>Expands here-input fields under their assembly owner without joining their bytes.</summary>
    internal Task<(IReadOnlyList<ExpandedWord> Fields, int Length)> ExpandHereInputAsync(
        Word word, ScopedEvaluation scope, MemoryCapacity storage)
    {
        return AwaitWordExpansionAsync(ExpandAsync());

        async Task<(IReadOnlyList<ExpandedWord>, int)> ExpandAsync()
        {
            var fields = await WordExpansion.ExpandHereInputAsync(word, scope, storage).ConfigureAwait(false);
            var length = ExpandedWord.GetJoinedValueLength(fields);
            // A single existing value can use its full length. A joined value has the
            // same representational bound as ordinary scalar expansion.
            if (length >= ByteMemoryBuilder.MaxMemoryBuilderBytes
                && (length != ByteMemoryBuilder.MaxMemoryBuilderBytes || fields is not [{ Segments.Count: 1 }]))
                throw new ByteMemoryBuilder.LimitExceededException();
            return (fields, (int)length);
        }
    }

    private static ExpandedWord? GetLiteralWithoutGlob(Word word) => word.LiteralFields is [var literal]
        && (literal.Segments is [{ IsGlobQuoted: true }] || !GlobPattern.RequiresExpansion(literal)) ? literal : null;

    private Word[] CaptureLiteralWords(List<ShellValue> fields, int start, ShellValue? first)
    {
        var count = checked(fields.Count - start + (first.HasValue ? 1 : 0));
        Memory.Charge(MemoryCapacity.ArrayBackingBytes<Word>(count));
        var words = new Word[count];
        var index = 0;
        if (first is { } value)
            words[index++] = Capture(value);
        for (var field = start; field < fields.Count; field++)
            words[index++] = Capture(fields[field]);
        return words;

        Word Capture(ShellValue field)
        {
            // The argument borrows the expanded bytes and owns its syntax and cached expansion.
            Memory.Charge(256);
            return new Word([WordPart.FromValue(WordPartKind.SingleQuoted, field)]);
        }
    }

    /// <summary>Expands directly into the command's admitted operand list.</summary>
    private Task AppendOperandValuesAsync(Word word, ScopedEvaluation scope, List<ShellValue> operands) =>
        AppendOperandValuesAsync(word, scope, operands, Memory.Workspace);

    private Task AppendOperandValuesAsync(Word word, ScopedEvaluation scope, List<ShellValue> operands, MemoryCapacity backingOwner)
    {
        return AwaitWordExpansionAsync(AppendAsync());

        async Task<bool> AppendAsync()
        {
            if (!scope.CanExpand)
                return true;

            if (word.Parts is [SourceWordPart { Kind: WordPartKind.DoubleQuoted } part]
                && (part.Source.AsSpan().SequenceEqual("$@"u8) || part.Source.AsSpan().SequenceEqual("${@}"u8)))
            {
                CheckCancellation();
                var arguments = scope.Variables.PositionalParameters;
                var count = arguments.Count;
                Memory.PrepareListCapacity(backingOwner, operands, count);
                for (var index = 0; index < count; index++)
                {
                    CheckCancellation();
                    var value = arguments[index];
                    if (value.IsEmpty)
                    {
                        operands.Add(ShellValue.Empty);
                        continue;
                    }

                    // A later operand or loop body can replace the positional frame.
                    var compact = value.TryGetCompactArrayCapacity(out var capacity);
                    Memory.Charge(MemoryCapacity.ArrayBackingBytes<byte>(compact ? capacity : value.Bytes.Length));
                    if (!compact)
                        value = ShellValue.FromBytes(value.Bytes.ToArray());
                    operands.Add(value);
                }
                return true;
            }

            var expanded = await WordExpansion.ExpandToWordsAsync(word, scope).ConfigureAwait(false);
            return await AppendExpandedWordsAsync(expanded, operands, backingOwner, static value => value)
                .ConfigureAwait(false);
        }
    }

    private void AddOperand<T>(List<T> operands, T value)
    {
        Memory.PrepareListCapacity(operands, 1);
        operands.Add(value);
    }

    /// <summary>Validates redirection fields in order and decodes a single candidate.</summary>
    internal async Task<(int Count, string Value)> ExpandRedirectionFieldsAsync(Word word, ScopedEvaluation scope)
    {
        var (count, field) = await CollectRedirectionFieldsAsync(word, scope).ConfigureAwait(false);
        return (count, count == 1 ? await DecodeExpandedValueAsync(field).ConfigureAwait(false) : string.Empty);
    }

    /// <summary>Expands a diagnostic operand as bytes, preserving command substitution spelling.</summary>
    internal async Task<ShellValue?> ExpandRedirectionDiagnosticAsync(Word word, ScopedEvaluation scope)
    {
        var (count, field) = await CollectRedirectionFieldsAsync(
            word, scope with { PreserveCommandSubstitutionSpelling = true }).ConfigureAwait(false);
        return count == 1 ? field : null;
    }

    private async ValueTask<(int Count, ShellValue Value)> CollectRedirectionFieldsAsync(Word word, ScopedEvaluation scope)
    {
        if (scope.CanExpand && GetLiteralWithoutGlob(word) is { } literal)
        {
            CheckCancellation();
            return (1, literal.ToValue(Memory));
        }

        using var fieldMemory = new MemoryCapacity(Memory.Budget, 0);
        var values = new List<ShellValue>();
        await AppendOperandValuesAsync(word, scope, values, fieldMemory).ConfigureAwait(false);
        if (_state.HasPendingExpansionError)
            return (0, ShellValue.Empty);
        if (values.Count == 1)
            return (1, values[0]);
        if (scope.PreserveCommandSubstitutionSpelling)
            return (values.Count, ShellValue.Empty);

        foreach (var value in values)
        {
            CheckCancellation();
            if (!System.Text.Unicode.Utf8.IsValid(value.Bytes.Span))
            {
                await ReportInvalidTextValueAsync().ConfigureAwait(false);
                return (0, ShellValue.Empty);
            }
        }
        return (values.Count, ShellValue.Empty);
    }

    private async Task<string> DecodeExpandedValueAsync(ShellValue value)
    {
        if (_state.HasPendingExpansionError)
            return string.Empty;
        Memory.Charge(checked(4L * value.Bytes.Length));
        if (value.TryGetUtf8Text(out var text))
            return text;

        await ReportInvalidTextValueAsync().ConfigureAwait(false);
        return string.Empty;
    }

    private Task ReportInvalidTextValueAsync() => ReportFatalExpansionAsync(ShellValue.FromText("shell"),
        ShellValue.FromText("text value must be valid UTF-8"), ExpansionFailureKind.Parameter);

    private async Task<T> AwaitWordExpansionAsync<T>(Task<T> expansion)
    {
        try
        {
            return await expansion.ConfigureAwait(false);
        }
        catch (ByteMemoryBuilder.LimitExceededException error) when (_state.FatalExpansion == null)
        {
            _state.FatalExpansion = new ExpansionFailure(ExpansionFailureKind.Parameter, 2);
            await ExecuteWriteAsync(GetStdErrDescriptor(), Utf8Text.Encode($"shell: {error.Message}\n")).ConfigureAwait(false);
            throw;
        }
        catch (SyntaxException exception)
        {
            await ReportSyntaxErrorAsync(exception.Error).ConfigureAwait(false);
            throw;
        }
    }

    private IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment(
        Variables variables, EvaluationContext context, MemoryCapacity resultMemory)
    {
        using var selectionMemory = context.FunctionContext == null && context.GlobalPrefixes == null
            ? null : new MemoryCapacity(Memory.Budget, 0);
        var exports = selectionMemory == null ? variables.ExportedBindings : ExportBindings(selectionMemory);
        return global::Lokad.Bash.Evaluation.Variables.BuildExportedEnvironment(
            exports, (Memory, resultMemory), selectionMemory, _cancellationToken);

        IEnumerable<VariableBinding> ExportBindings(MemoryCapacity selection)
        {
            foreach (var binding in variables.ExportedBindings)
                yield return binding;

            var functionPrefixes = new HashSet<BindingName>();
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
            {
                foreach (var name in function.PrefixNames)
                {
                    CheckCancellation();
                    if (functionPrefixes.Contains(name))
                        continue;
                    selection.Grow(global::Lokad.Bash.Evaluation.Variables.BindingNameSelectionBytes);
                    functionPrefixes.Add(name);
                }
            }
            var visibleOwners = new Dictionary<BindingName, Variables>();
            var prefix = context.GlobalPrefixes;
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
            {
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    foreach (var binding in Unhide(prefix.Saved, prefix.Names, callPrefixes: null))
                        yield return binding;
                    prefix = prefix.Enclosing;
                }
                if (function.Locals.SavedBindings is { } locals)
                    foreach (var binding in Unhide(locals, function.Locals.SavedNames, function.PrefixNames))
                        yield return binding;
                if (function.PrefixBindings is { } bindings)
                    foreach (var binding in Unhide(bindings.SavedVariables, function.PrefixNames, callPrefixes: null))
                        yield return binding;
            }
            while (prefix != null)
            {
                foreach (var binding in Unhide(prefix.Saved, prefix.Names, callPrefixes: null))
                    yield return binding;
                prefix = prefix.Enclosing;
            }

            IEnumerable<VariableBinding> Unhide(Variables saved, IReadOnlySet<BindingName> names, IReadOnlySet<BindingName>? callPrefixes)
            {
                foreach (var binding in saved.ExportedBindings)
                {
                    CheckCancellation();
                    // Removing a local export also hides its saved function-call prefix, but not outer exports.
                    if (callPrefixes?.Contains(binding.Name) != true
                        || visibleOwners.GetValueOrDefault(binding.Name, variables).IsExported(binding.Name, detachBorrowed: false))
                        yield return binding;
                }
                // A scope often saves only a few names, independently of the call depth.
                var scanSavedNames = names.Count < functionPrefixes.Count;
                var candidates = scanSavedNames ? names : functionPrefixes;
                var other = scanSavedNames ? functionPrefixes : names;
                foreach (var name in candidates)
                {
                    CheckCancellation();
                    if (!other.Contains(name))
                        continue;
                    if (!visibleOwners.ContainsKey(name))
                        selection.Grow(192); // Dictionary slots also hold a reference to the owner.
                    visibleOwners[name] = saved;
                }
            }
        }
    }

    private async Task<bool> AppendExpandedWordsAsync<T>(
        IReadOnlyList<ExpandedWord> expanded, List<T> results, MemoryCapacity backingOwner, Func<ShellValue, T> createItem)
    {
        // Each expanded field contributes at least one value, including literal glob fallback.
        var count = expanded.Count;
        if (count > 1)
            Memory.PrepareListCapacity(backingOwner, results, count);
        for (var index = 0; index < count; index++)
        {
            CheckCancellation();
            var expandedWord = expanded[index];
            if (!GlobPattern.RequiresExpansion(expandedWord))
            {
                Memory.PrepareListCapacity(backingOwner, results, 1);
                results.Add(createItem(expandedWord.ToValue(Memory)));
                continue;
            }

            if (!GlobPattern.TryFromExpandedWord(expandedWord, Memory, out var pattern))
            {
                await ReportFatalExpansionAsync(ShellValue.FromText("shell"),
                    ShellValue.FromText("pathname pattern must be valid UTF-8"), ExpansionFailureKind.Parameter)
                    .ConfigureAwait(false);
                return false;
            }
            using var globMemory = new MemoryCapacity(Memory.Budget, 0);
            var glob = new GlobEvaluation(pattern, _state.CurrentDirectory,
                globMemory, _cancellationToken);
            var matches = await glob.EvaluateAsync(this).ConfigureAwait(false);
            Memory.PrepareListCapacity(backingOwner, results, matches.Count);
            foreach (var match in matches)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                // Glob paths are UTF-16; reserve their UTF-8 copies before building argument values.
                Memory.Charge(checked(256L + 4L * (Encoding.UTF8.GetByteCount(match))));
                results.Add(createItem(ShellValue.FromText(match)));
            }
        }

        return true;
    }

    /// <summary>
    /// Sets a variable in the evaluation state, honoring name references.
    /// </summary>
    internal void SetVariable(BindingName name, ShellValue value) =>
        _vars.SetScalarIgnoringReadonly(name, value);

    /// <summary>Imports a validated definition without invoking it or retaining parent-only readonly attributes.</summary>
    public void SetExportedFunction(FunctionDefinition definition)
    {
        // Host initialization is admitted at Engine startup; callbacks during execution
        // share the active namespace's prepublication admission.
        if (Memory.HasCommand) Memory.Charge(256);
        _state.SetFunctionBinding(new FunctionBinding(definition, FunctionAttributes.Exported, sourceMemory: null, Memory));
    }

    internal long RetainedFunctionMemoryBytes => _state.EstimatedFunctionMemoryBytes;

    /// <summary>Sets an exported variable, bypassing readonly checks for host-provided environment.</summary>
    internal void SetExportedVariable(BindingName name, ShellValue value)
    {
        _vars.SetScalarIgnoringReadonly(name, value);
        _vars.SetExported(name, true);
    }

    /// <summary>
    /// Persists an allocated script binding through its enclosing restoration boundary.
    /// </summary>
    internal void BindPersistentDescriptor(LogicalDescriptor descriptor, DescriptorBinding binding) =>
        _state.SetPersistentDescriptor(descriptor, binding, Memory, OwnsPersistentHandle(binding.Handle));

    private bool OwnsPersistentHandle(FileDescriptor descriptor)
    {
        if (!_pendingDescriptorCleanup.Contains(descriptor)) return false;
        foreach (var borrowed in _subshellBorrowedDescriptorScopes)
            if (borrowed.Contains(descriptor)) return false;
        return true;
    }

    /// <summary>
    /// Tracks an evaluator-owned descriptor until close completes, across redirection ownership transfers.
    /// </summary>
    internal void TrackOwnedDescriptor(FileDescriptor descriptor) =>
        ScheduleDescriptorCleanup(descriptor);

    /// <summary>
    /// Records an expansion error exit code for the current evaluation.
    /// </summary>
    internal void SetExpansionError(int exitCode) =>
        _state.SetExpansionError(exitCode);

    internal void SetHereDocumentSyntaxError() => _state.SetHereDocumentSyntaxError();

    internal Task ReportHereDocumentParameterFailureAsync(ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message,
        ScopedEvaluation scope, ExpansionFailureKind? fatalKind)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;
        if (fatalKind is { } kind)
            SetFatalExpansionError(kind);
        _state.SetHereDocumentParameterError();
        return WriteDiagnosticAsync(subject, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
            GetHereDocumentParameterDiagnosticPrefix(scope));
    }

    internal Task ReportInvalidParameterTransformAsync(ShellValue subject, ScopedEvaluation scope, bool hereDocument)
    {
        ReadOnlySpan<ShellValue> message = [ShellValue.FromText("bad substitution")];
        if (hereDocument)
            return ReportHereDocumentParameterFailureAsync([subject], message, scope, ExpansionFailureKind.InvalidParameterTransform);
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;
        SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.InvalidParameterTransform, 1)
        {
            UseInheritedPipelineExitAction = UsesInheritedPipelineExitAction(scope)
        });
        _state.LastExitCode = 1;
        PublishExpansionPipelineStatus(1, scope);
        return WriteDiagnosticAsync([subject], message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
            GetCommandDiagnosticPrefix(scope));
    }

    /// <summary>Reports the first fatal expansion through the active stderr mapping without decoding message bytes.</summary>
    internal Task ReportFatalExpansionAsync(ShellValue name, ShellValue message, ExpansionFailureKind kind) =>
        ReportFatalExpansionAsync([name], [message], kind);

    internal Task ReportFatalExpansionAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message, ExpansionFailureKind kind) =>
        ReportFatalExpansionAsync(name, message, kind, default);

    /// <summary>Reports a rejected assignment with the lexical coordinate of the reached command.</summary>
    internal Task ReportAssignmentTargetFailureAsync(ShellValue name, string message, ScopedEvaluation scope) =>
        ReportAssignmentTargetFailureAsync([name], [ShellValue.FromText(message)], scope);

    internal Task ReportAssignmentTargetFailureAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ScopedEvaluation scope) =>
        ReportFatalExpansionAsync(name, message, ExpansionFailureKind.AssignmentTarget,
            GetCommandDiagnosticPrefix(scope));

    internal Task ReportInvalidNamerefElementTargetAsync(ShellValue target, ScopedEvaluation scope) =>
        ReportFatalExpansionAsync([ShellValue.FromText("`"), target, ShellValue.FromText("'")],
            [ShellValue.FromText("not a valid identifier")], ExpansionFailureKind.AssignmentTarget,
            GetCommandDiagnosticPrefix(scope), canResumeUntestedUnit: true);

    internal Task ReportArrayReadFailureAsync(ReadOnlySpan<ShellValue> subject, ScopedEvaluation scope, bool length)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        ReadOnlySpan<ShellValue> message = [ShellValue.FromText("bad array subscript")];
        if (ReaderErrexitEnabled(scope))
        {
            // Selection precedes command execution and leaves the previous PIPESTATUS visible.
            var status = _state.LastExitCode is { } previous && previous != 0 ? previous : 1;
            SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.ArrayRead, status)
                { UseInheritedPipelineExitAction = !scope.AfterPipelineExitReset && UsesInheritedPipelineExitAction(scope) });
        }
        else if (length)
            return ReportParameterExpansionFailureAsync(subject, message, scope, ExpansionFailureKind.Arithmetic);
        return WriteDiagnosticAsync(subject, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
            GetParameterExpansionDiagnosticPrefix(scope));
    }

    /// <summary>Uses the assignment's lexical spelling while retaining its original source location.</summary>
    internal ShellValue GetAssignmentDiagnosticSpelling(ShellSource spelling, LegacySubstitutionOrigin origin) =>
        ShellSourceCursor.RemoveLineContinuations(spelling, Memory.Workspace, origin).ToValue();

    private async Task ReportInvalidElementPrefixAsync(ShellSource target, ScopedEvaluation scope)
    {
        var spelling = GetAssignmentDiagnosticSpelling(target, scope.ExpansionOrigin);
        if (spelling.Bytes.Span.IndexOf("$("u8) >= 0)
        {
            using var syntax = new MemoryCapacity(Memory.Budget, 0);
            if (Tokenizer.TryReadExpansionWord(ShellSource.FromBytes(spelling.Bytes), ExpansionWordSyntax.Parameter,
                    ShellQuoteKind.None, syntax, out var word, out _))
            {
                using var output = new ByteMemoryBuilder(0, Memory.Budget);
                if (!FunctionDefinitionFormatter.TryAppendWordDisplay(word, output, Memory.Budget, _cancellationToken))
                    throw new ByteMemoryBuilder.LimitExceededException();
                await ReportAsync(ShellValue.FromBytes(output.ToMemory())).ConfigureAwait(false);
                return;
            }
        }
        await ReportAsync(spelling).ConfigureAwait(false);

        Task ReportAsync(ShellValue subject) => ReportCommandDiagnosticAsync(
            [ShellValue.FromText("`"), subject, ShellValue.FromText("'")],
            [ShellValue.FromText("not a valid identifier")], scope);
    }

    internal Task ReportIntegerAssignmentFailureAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ScopedEvaluation scope) => ReportFatalExpansionAsync(name, message, ExpansionFailureKind.IntegerAssignment,
            GetBuiltinDiagnosticPrefix(scope.Context));

    internal Task ReportNamerefAssignmentFailureAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ExpansionFailureKind kind, ScopedEvaluation scope) => ReportFatalExpansionAsync(name, message, kind,
            kind == ExpansionFailureKind.AssignmentTarget ? GetCommandDiagnosticPrefix(scope) : GetBuiltinDiagnosticPrefix(scope.Context));

    private Task ReportFatalExpansionAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ExpansionFailureKind kind, SyntaxDiagnosticPrefix prefix) =>
        ReportFatalExpansionAsync(name, message, kind, prefix, canResumeUntestedUnit: false);

    private Task ReportFatalExpansionAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ExpansionFailureKind kind, SyntaxDiagnosticPrefix prefix, bool canResumeUntestedUnit)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        if (kind == ExpansionFailureKind.IntegerAssignment && _state.ForkKind is null or ShellForkKind.Subshell
            && _state.LastExitCode is { } previousStatus && previousStatus != 0)
            SetFatalExpansionError(new ExpansionFailure(kind, previousStatus));
        else if (canResumeUntestedUnit)
            SetFatalExpansionError(new ExpansionFailure(kind, 1) { CanResumeUntestedUnit = true });
        else
            SetFatalExpansionError(kind);
        return WriteDiagnosticAsync(name, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, prefix);
    }

    internal void SetFatalExpansionError(ExpansionFailureKind kind)
        => SetFatalExpansionError(new ExpansionFailure(kind, kind == ExpansionFailureKind.NamerefDefault ? 2 : 1));

    private void SetFatalExpansionError(ExpansionFailure failure)
    {
        _state.FatalExpansion ??= failure;
        _state.SetExpansionError(_state.FatalExpansion.Value.ExitCode);
    }

    internal Task ReportUnitExpansionFailureAsync(ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message, ScopedEvaluation scope,
        ExpansionFailureKind kind) =>
        ReportUnitExpansionFailureAsync(subject, message, scope, kind,
            kind is ExpansionFailureKind.Arithmetic or ExpansionFailureKind.ArrayIndex ? GetBuiltinDiagnosticPrefix(scope.Context) : default);

    private static SyntaxDiagnosticPrefix GetParameterExpansionDiagnosticPrefix(ScopedEvaluation scope) =>
        scope.ExpansionOrigin == LegacySubstitutionOrigin.HereDocument
            ? GetHereDocumentParameterDiagnosticPrefix(scope)
            : GetCommandDiagnosticPrefix(scope);

    internal Task ReportParameterExpansionFailureAsync(ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message,
        ScopedEvaluation scope, ExpansionFailureKind kind) =>
        ReportUnitExpansionFailureAsync(subject, message, scope, kind, GetParameterExpansionDiagnosticPrefix(scope));

    internal Task ReportParameterFatalExpansionAsync(ShellValue subject, ShellValue message, ScopedEvaluation scope,
        ExpansionFailureKind kind) =>
        ReportFatalExpansionAsync([subject], [message], kind, GetParameterExpansionDiagnosticPrefix(scope));

    internal Task ReportBraceExpansionFailureAsync(ShellValue message, ScopedEvaluation scope) =>
        ReportFatalExpansionAsync([ShellValue.FromText("bad substitution")], [message], ExpansionFailureKind.WordSyntax,
            GetNamedSourceDiagnosticPrefix(scope.Context));

    private Task ReportUnitExpansionFailureAsync(ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message, ScopedEvaluation scope,
        ExpansionFailureKind kind, SyntaxDiagnosticPrefix prefix)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        // Publish status while the reached lexical scope is still active.
        SetFatalExpansionError(kind);
        _state.LastExitCode = 1;
        PublishExpansionPipelineStatus(1, scope);
        return WriteDiagnosticAsync(subject, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite, prefix);
    }

    private void PublishAssignmentTargetFailure(ScopedEvaluation scope)
    {
        if (_state.FatalExpansion is not { Kind: ExpansionFailureKind.AssignmentTarget } failure)
            return;

        var canResume = failure.CanResumeUntestedUnit || !_state.ErrexitEnabled;
        var previousStatus = _state.LastExitCode.GetValueOrDefault();
        var status = !canResume && previousStatus != 0 ? previousStatus : 1;
        _state.FatalExpansion = failure with { ExitCode = status, CanResumeUntestedUnit = canResume };
        _state.SetExpansionError(status);
        // Loaded in-process readers publish after unwind. Other writes use the
        // foreground status storage admitted before the simple command expanded.
        if (canResume && (_state.ForkKind != null || !scope.InNestedCommandUnits && _state.Traps?.ExitTrapStatus == null))
        {
            _state.LastExitCode = status;
            scope.Variables.SetPipelineStatuses([status], scope.Context, _cancellationToken);
        }
    }

    internal Task ReportParameterDefaultFailureAsync(
        ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message, ScopedEvaluation scope)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        var exitActionStatus = _state.Traps?.ExitTrapStatus;
        var loadedUnits = scope.InNestedCommandUnits || PipelineInheritsLoadedUnitStatus || exitActionStatus != null;
        var canResume = !_state.ErrexitEnabled;
        int exitCode;
        if (!canResume)
        {
            // An errexit unwind retains an existing nonzero status.
            exitCode = _state.LastExitCode is { } previous && previous != 0 ? previous : 1;
        }
        else if (_state.ForkKind == ShellForkKind.PipelineSimpleCommand && !scope.InNestedCommandUnits && exitActionStatus is { } saved)
        {
            // A simple pipeline stage inherits the status that entered the EXIT action.
            exitCode = saved;
        }
        else
        {
            exitCode = loadedUnits || _state.ForkKind == ShellForkKind.CommandSubstitution ? 1 : 2;
        }
        SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.ParameterDefaultAssignment, exitCode)
            { CanResumeUntestedUnit = canResume });
        // Publish in the reached bindings before a function can restore its locals.
        // An in-process loaded reader publishes its own status after unwinding.
        if (canResume && (!loadedUnits || _state.ForkKind != null))
        {
            _state.LastExitCode = exitCode;
            PublishExpansionPipelineStatus(exitCode, scope);
        }
        return ReportBuiltinDiagnosticAsync(subject, message, scope.Context);
    }

    private void PublishExpansionPipelineStatus(int exitCode, ScopedEvaluation scope)
    {
        PreparePipelineStatusStorage(1);
        // A failed prefix RHS publishes to the caller before its temporary bindings go away.
        var variables = scope.ExpansionWriteTarget is { PreparingPrefixes: true } prefix ? prefix.Variables : scope.Variables;
        variables.SetPipelineStatuses([exitCode], scope.Context, _cancellationToken);
    }

    private bool PipelineInheritsLoadedUnitStatus => _state.ForkKind == ShellForkKind.PipelineSimpleCommand
        && _pipelineUsesLoadedUnitStatus;

    private bool ReaderErrexitEnabled(ScopedEvaluation scope) =>
        _state.ErrexitEnabled && !scope.LoadedReaderIgnoresErrexit;

    private int ReaderSyntaxExitCode(ScopedEvaluation scope) =>
        !ReaderErrexitEnabled(scope) && scope.LoadedSyntaxFunctionDepth.HasValue ? 1 : 2;

    private bool IsDirectPipelineStageScope(ScopedEvaluation scope) =>
        _state.ForkKind == ShellForkKind.PipelineSimpleCommand && !scope.InNestedCommandUnits
        && ReferenceEquals(scope.Context.FunctionContext, _initialContext.FunctionContext);

    private bool UsesInheritedPipelineExitAction(ScopedEvaluation scope) =>
        IsDirectPipelineStageScope(scope) && _state.Traps?.HasInheritedPipelineExitAction == true;

    internal async Task ReportCommandSubstitutionSyntaxAsync(SubstitutionSyntaxFailure failure,
        CommandSubstitutionSyntax.Parsed? capture, ScopedEvaluation scope, bool quoted, RejectedSelectorCapture rejectedSelector)
    {
        if (_state.FatalExpansion.HasValue)
            return;

        var terminatesReader = failure == SubstitutionSyntaxFailure.NestedHereDocumentSeparator;
        var separatorFailure = terminatesReader || failure == SubstitutionSyntaxFailure.HereDocumentSeparator;
        var prefixFailure = failure is SubstitutionSyntaxFailure.MissingPipelinePrefixCommand or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand;
        var errexit = separatorFailure ? ReaderErrexitEnabled(scope) : _state.ErrexitEnabled;
        var exitCode = terminatesReader ? ReaderSyntaxExitCode(scope)
            : errexit || failure is SubstitutionSyntaxFailure.NestedSyntax or SubstitutionSyntaxFailure.NestedPipelinePrefixCommand ? 2 : 1;
        var kind = terminatesReader ? ExpansionFailureKind.ReaderSyntax : ExpansionFailureKind.CommandSubstitutionSyntax;
        SetFatalExpansionError(new ExpansionFailure(kind, exitCode)
        {
            UseInheritedPipelineExitAction = separatorFailure && UsesInheritedPipelineExitAction(scope)
        });
        _state.LastExitCode = exitCode;
        if ((separatorFailure || prefixFailure) && (capture != null || scope.ExpansionWord != null || !rejectedSelector.Operand.IsEmpty)
            && !GetStdErrDescriptor().IsClosed)
        {
            // The rendered word supplies the delimiters and literal text following a rejected capture.
            using var spelling = new ByteMemoryBuilder(0, Memory.Budget);
            using var workspace = new MemoryCapacity(Memory.Budget, 0);
            UnexpectedSyntax? token = null;
            var word = scope.ExpansionWord;
            if (!rejectedSelector.Operand.IsEmpty && Tokenizer.TryReadExpansionWord(rejectedSelector.Operand, ExpansionWordSyntax.Parameter,
                ShellQuoteKind.None, workspace, out var selectorWord, out _))
                word = selectorWord;
            if (word != null)
            {
                if (scope.CompoundPrefixItems is { } prefixItems && rejectedSelector.Operand.IsEmpty)
                {
                    spelling.Append((byte)'(');
                    AppendCompoundInitializerDisplay(prefixItems, spelling);
                    spelling.Append((byte)')');
                }
                else if (!FunctionDefinitionFormatter.TryAppendWordDisplay(word, spelling, Memory.Budget, _cancellationToken))
                    throw new ByteMemoryBuilder.LimitExceededException();
                token = Tokenizer.ReadUnexpectedWordToken(ShellSource.FromBytes(spelling.ToMemory()),
                    new ShellScanContext { SyntaxMemory = workspace });
            }
            if (token == null && capture != null)
            {
                spelling.Truncate(0);
                if (!FunctionDefinitionFormatter.TryAppendSubstitutionDisplay(capture, spelling, Memory.Budget, _cancellationToken))
                    throw new ByteMemoryBuilder.LimitExceededException();
                if (quoted)
                    spelling.Append((byte)'"');
                var source = ShellSource.FromBytes(spelling.ToMemory());
                Parser.ScanCommandSubstitutionEnd(new ShellScanContext { SyntaxMemory = workspace }, source, 2,
                    out _, out _, out _, out _, out _, out _, out var report);
                token = report?.UnexpectedToken;
            }
            if (token != null)
            {
                var renderedSource = spelling.ToMemory();
                var line = GetCaptureDiagnosticLine(token, renderedSource, scope.Context.Coordinates.Line);
                if (prefixFailure)
                {
                    var start = renderedSource.Span[..token.ReadPosition].LastIndexOf((byte)'\n') + 1;
                    if (start < token.CapturePayloadStart)
                        token = token with { Line = token.Line.Slice(token.CapturePayloadStart - start) };
                }
                var path = scope.Context.DiagnosticPath;
                await ReportUnexpectedSyntaxAsync(token, path, SyntaxDiagnosticOrigin.CommandSubstitution,
                    line, includeLine: !errexit).ConfigureAwait(false);
                if (rejectedSelector.NeedsParameterDiagnostic && !errexit && word != null)
                {
                    var descriptor = GetStdErrDescriptor();
                    var prefix = new SyntaxDiagnosticPrefix(path,
                        scope.CompoundPrefixItems == null ? SyntaxDiagnosticOrigin.Script : SyntaxDiagnosticOrigin.ScriptLine,
                        Math.Max(1, scope.Context.Coordinates.Line));
                    var badSubstitution = ShellValue.FromText("bad substitution");
                    var rendered = ShellValue.FromBytes(spelling.ToMemory());
                    if (!quoted && rejectedSelector.NeedsCaptureEofDiagnostic)
                        await WriteSyntaxDiagnosticLineAsync(descriptor,
                            new SyntaxDiagnosticPrefix(path, SyntaxDiagnosticOrigin.CommandSubstitution, line + 1),
                            MissingCommandSubstitutionClose).ConfigureAwait(false);
                    if (quoted)
                        await WriteDiagnosticAsync(descriptor, [badSubstitution],
                            [ShellValue.FromText("no closing `}' in "), rendered], scope: null, prefix).ConfigureAwait(false);
                    else
                        await WriteDiagnosticAsync(descriptor, [rendered], [badSubstitution], scope: null, prefix).ConfigureAwait(false);
                }
                return;
            }
        }
        var message = separatorFailure
            ? "Unexpected token after a here-document in command substitution."
            : "Unexpected ')' after a pipeline prefix in command substitution.";
        await ReportSyntaxErrorAsync(new SyntaxError(message, 0)).ConfigureAwait(false);
    }

    private static int GetCaptureDiagnosticLine(UnexpectedSyntax token, ReadOnlyMemory<byte> spelling, int baseLine) =>
        Math.Max(1, baseLine) + spelling.Span[token.CapturePayloadStart..token.ReadPosition].Count((byte)'\n') + 1 + token.LineAdvance;

    private const int SmallDiagnosticBytes = ByteMemoryBuilder.DefaultCapacity;
    private const int DiagnosticPunctuationBytes = 3; // Colon, space and LF.

    private ShellValue GetDiagnosticName(BindingName name)
    {
        if (name.NeedsValueEncoding)
            Memory.Charge(checked(4L * name.ByteLength));
        return name.ToValue();
    }

    private Task ReportInvalidIdentifierAsync(ShellValue identifier) =>
        ReportExpansionDiagnosticAsync([ShellValue.FromText("`"), identifier, ShellValue.FromText("'")],
            [ShellValue.FromText("not a valid identifier")]);

    /// <summary>Emits an expansion diagnostic without changing the caller's expansion status.</summary>
    internal Task ReportExpansionDiagnosticAsync(ShellValue name, ShellValue message) =>
        ReportExpansionDiagnosticAsync([name], [message]);

    /// <summary>Writes one admitted record from borrowed fragments of the name and message.</summary>
    internal Task ReportExpansionDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message) =>
        WriteDiagnosticAsync(name, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite);

    internal Task ReportCommandDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        ScopedEvaluation scope) => WriteDiagnosticAsync(name, message, _builtinDiagnostics, ClosedStderrHandling.AttemptWrite,
            GetCommandDiagnosticPrefix(scope));

    private static SyntaxDiagnosticPrefix GetCommandDiagnosticPrefix(ScopedEvaluation scope) =>
        GetCommandDiagnosticPrefix(scope.Context, scope.Context.Coordinates.Line);

    private static SyntaxDiagnosticPrefix GetBuiltinDiagnosticPrefix(EvaluationContext context)
    {
        var coordinates = context.Coordinates;
        var line = coordinates.RedirectionDiagnosticLine is not 0 ? coordinates.RedirectionDiagnosticLine : coordinates.Line;
        return GetCommandDiagnosticPrefix(context, line);
    }

    private static SyntaxDiagnosticPrefix GetCommandDiagnosticPrefix(EvaluationContext context, int line) =>
        context.Coordinates.Locations == null ? default
            : new SyntaxDiagnosticPrefix(context.DiagnosticPath, SyntaxDiagnosticOrigin.ScriptLine, line);

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr) =>
        WriteDiagnosticAsync(name, message, scope, closedStderr, default);

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr, SyntaxDiagnosticPrefix prefix)
    {
        if (scope is { Result.CanAcceptMore: false })
            return Task.FromResult(scope.Result);

        var descriptor = GetStdErrDescriptor();
        if (closedStderr == ClosedStderrHandling.Discard && descriptor.IsClosed)
        {
            CheckCancellation();
            return Task.FromResult(DescriptorWriteResult.Closed);
        }

        return WriteDiagnosticAsync(descriptor, name, message, scope, prefix);
    }

    /// <summary>Writes one diagnostic to the descriptor reached during redirection.</summary>
    internal Task WriteRedirectionDiagnosticAsync(FileDescriptor descriptor, ShellValue name, ShellValue message) =>
        WriteDiagnosticAsync(descriptor, [name], [message], scope: null);

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(FileDescriptor descriptor, ReadOnlySpan<ShellValue> name,
        ReadOnlySpan<ShellValue> message, BuiltinDiagnosticScope? scope) =>
        WriteDiagnosticAsync(descriptor, name, message, scope, default);

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(FileDescriptor descriptor, ReadOnlySpan<ShellValue> name,
        ReadOnlySpan<ShellValue> message, BuiltinDiagnosticScope? scope, SyntaxDiagnosticPrefix prefix)
    {
        var length = (long)DiagnosticPunctuationBytes + prefix.Length;
        foreach (var part in name)
            length += part.Bytes.Length;
        foreach (var part in message)
            length += part.Bytes.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            throw new MemoryBudgetExceededException();

        try
        {
            using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
            prefix.AppendTo(diagnostic);
            foreach (var part in name)
                diagnostic.Append(part.Bytes);
            diagnostic.Append(": "u8);
            foreach (var part in message)
                diagnostic.Append(part.Bytes);
            diagnostic.Append((byte)'\n');
            return WriteAsync(descriptor, diagnostic.ToMemory(), scope, reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }

        async Task<DescriptorWriteResult> WriteAsync(FileDescriptor descriptor, ReadOnlyMemory<byte> bytes, BuiltinDiagnosticScope? scope,
            IMemoryReservation? reservation)
        {
            using var memory = reservation;
            var result = await ExecuteWriteAsync(descriptor, bytes).ConfigureAwait(false);
            if (scope != null)
                scope.Result = result;
            return result;
        }
    }

    private bool TryReserveDiagnostic(long length, out IMemoryReservation? reservation)
    {
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        // Small diagnostics, including refusal messages, fit within caller headroom.
        reservation = null;
        return length <= SmallDiagnosticBytes || Memory.Budget.TryReserve(length, out reservation);
    }

    internal bool HasPendingExpansionError => _state.HasPendingExpansionError;

    /// <summary>
    /// Gets the cancellation token associated with the current evaluation.
    /// </summary>
    internal CancellationToken CancellationToken => _cancellationToken;

    internal void PublishOperation(EvaluationOperation? operation)
    {
        if (operation != null && _forwardNextStep != null)
        {
            _forwardNextStep(operation);
            return;
        }

        _nextStepTracker.SetNextStep(operation);
    }

    private void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();

    private async Task<T> AwaitOperationAsync<T>(Task<T> task)
    {
        if (!_cancellationToken.CanBeCanceled)
            return await task.ConfigureAwait(false);

        return await task.WaitAsync(_cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReportSyntaxErrorAsync(SyntaxError error)
    {
        await ExecuteWriteAsync(GetStdErrDescriptor(), Utf8Text.EncodeLine(error.ToDiagnosticMessage()))
            .ConfigureAwait(false);
    }

    private enum SyntaxDiagnosticOrigin { Script, ScriptLine, Eval, ExitTrap, CommandSubstitution, ArrayAssignment }

    private bool ReportsOnlyPrimarySyntaxDiagnostic(SyntaxDiagnosticOrigin origin) =>
        origin == SyntaxDiagnosticOrigin.ExitTrap && _state.ErrexitEnabled;

    private readonly struct SyntaxDiagnosticPrefix
    {
        private readonly string? _path;
        private readonly int _pathLength;
        private readonly ShellValue _subject;
        private readonly SyntaxDiagnosticOrigin _origin;
        private readonly ReadOnlyMemory<byte> _lineNumber;

        internal SyntaxDiagnosticPrefix(string? path, SyntaxDiagnosticOrigin origin, int? line)
            : this(path, ShellValue.Empty, origin, line) { }

        internal SyntaxDiagnosticPrefix(string? path, ShellValue subject, SyntaxDiagnosticOrigin origin, int? line)
        {
            _path = path;
            _pathLength = path == null ? 0 : Utf8Text.StrictUtf8.GetByteCount(path);
            _subject = subject;
            _origin = origin;
            var hasPrefix = path != null || !subject.IsEmpty || origin != SyntaxDiagnosticOrigin.Script;
            _lineNumber = hasPrefix && line is { } value ? NumberFormatting.FormatNumber(value) : ReadOnlyMemory<byte>.Empty;
            Length = hasPrefix ? (path == null ? 0 : _pathLength + 2)
                + (subject.IsEmpty ? 0 : subject.Bytes.Length + 2) + GetOrigin().Length
                + (_lineNumber.IsEmpty ? 0 : "line "u8.Length + _lineNumber.Length + ": "u8.Length) : 0;
        }

        internal int Length { get; }

        internal void AppendTo(ByteMemoryBuilder diagnostic)
        {
            if (Length == 0)
                return;
            if (_path != null)
            {
                Utf8Text.StrictUtf8.GetBytes(_path, diagnostic.AppendSpan(_pathLength));
                diagnostic.Append(": "u8);
            }
            if (!_subject.IsEmpty)
                diagnostic.Append(_subject.Bytes.Span).Append(": "u8);
            diagnostic.Append(GetOrigin());
            if (!_lineNumber.IsEmpty)
                diagnostic.Append("line "u8).Append(_lineNumber.Span).Append(": "u8);
        }

        private ReadOnlySpan<byte> GetOrigin() => _origin switch
        {
            SyntaxDiagnosticOrigin.Eval => "eval: "u8,
            SyntaxDiagnosticOrigin.ExitTrap => "exit trap: "u8,
            SyntaxDiagnosticOrigin.CommandSubstitution => "command substitution: "u8,
            SyntaxDiagnosticOrigin.ArrayAssignment => "array assign: "u8,
            _ => []
        };
    }

    private Task ReportUnexpectedSyntaxAsync(UnexpectedSyntax syntax, string? path, SyntaxDiagnosticOrigin origin,
        int line, bool includeLine) =>
        ReportUnexpectedSyntaxAsync(syntax, path, ShellValue.Empty, origin, line, includeLine);

    private async Task ReportUnexpectedSyntaxAsync(UnexpectedSyntax syntax, string? path, ShellValue subject,
        SyntaxDiagnosticOrigin origin, int line, bool includeLine)
    {
        var descriptor = GetStdErrDescriptor();
        CheckCancellation();
        if (descriptor.IsClosed)
            return;

        var conditional = syntax.Conditional;
        var error = conditional?.Error ?? ConditionalReaderError.None;
        var hasArgument = !syntax.Argument.IsEmpty;
        var argumentPrefix = !hasArgument ? ReadOnlySpan<byte>.Empty : error switch
        {
            ConditionalReaderError.UnaryOperand or ConditionalReaderError.BinaryOperand => "unexpected argument `"u8,
            ConditionalReaderError.Expression => "syntax error in conditional expression: unexpected token `"u8,
            _ => "unexpected token `"u8
        };
        var reason = error switch
        {
            ConditionalReaderError.UnaryOperand when hasArgument => "' to conditional unary operator\n"u8,
            ConditionalReaderError.UnaryOperand => MissingConditionalUnaryArgument,
            ConditionalReaderError.BinaryOperand when hasArgument => "' to conditional binary operator\n"u8,
            ConditionalReaderError.BinaryOperand => MissingConditionalBinaryArgument,
            ConditionalReaderError.BinaryOperator when hasArgument => "', conditional binary operator expected\n"u8,
            ConditionalReaderError.BinaryOperator => MissingConditionalBinaryOperator,
            ConditionalReaderError.GroupClose when hasArgument => "', expected `)'\n"u8,
            ConditionalReaderError.GroupClose => MissingConditionalGroup,
            ConditionalReaderError.Expression when hasArgument => "'\n"u8,
            ConditionalReaderError.Expression => "syntax error in conditional expression\n"u8,
            ConditionalReaderError.Term => "' in conditional command\n"u8,
            _ => ReadOnlySpan<byte>.Empty
        };
        // Operator lookahead can reach EOF between physical lines, leaving no text to quote.
        var hasNearToken = conditional == null || !syntax.Token.IsEmpty;
        var primaryOnly = ReportsOnlyPrimarySyntaxDiagnostic(origin);
        includeLine &= hasNearToken && !primaryOnly;
        var groups = conditional?.UnclosedGroupLines;
        var groupCount = groups?.Count ?? 0;
        if (primaryOnly)
            groupCount = reason.IsEmpty ? Math.Min(groupCount, 1) : 0;
        var reportNearToken = !primaryOnly || (reason.IsEmpty && groupCount == 0);
        var tokenPrefix = conditional != null || syntax.UseNearPrefix
            ? "syntax error near `"u8 : "syntax error near unexpected token `"u8;
        if (!hasNearToken)
            tokenPrefix = "syntax error\n"u8;
        var prefix = new SyntaxDiagnosticPrefix(path, subject, origin, line);
        var prefixLength = prefix.Length;
        var length = reportNearToken
            ? (long)prefixLength + tokenPrefix.Length + (hasNearToken ? syntax.Token.Length + 2L : 0)
            : 0;
        var lineOffset = line - (conditional?.ReadLine ?? line);
        var reasonPrefix = reason.IsEmpty || conditional is not { } read || read.ReasonLine == read.ReadLine
            ? prefix : new SyntaxDiagnosticPrefix(path, subject, origin, lineOffset + read.ReasonLine);
        if (groupCount != 0)
        {
            // Reserve for any 32-bit line number without formatting each prefix twice.
            const int maximumLineNumberGrowth = 10;
            length += groupCount * (long)(prefixLength + maximumLineNumberGrowth + MissingConditionalGroup.Length);
        }
        if (includeLine)
            length += prefixLength + (long)syntax.Line.Length + 3;
        if (!reason.IsEmpty)
            length += reasonPrefix.Length + argumentPrefix.Length + syntax.Argument.Length + reason.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            throw new MemoryBudgetExceededException();

        using var memory = reservation;
        using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
        if (!reason.IsEmpty)
        {
            reasonPrefix.AppendTo(diagnostic);
            diagnostic.Append(argumentPrefix);
            AppendToken(syntax.Argument.AsSpan());
            diagnostic.Append(reason);
        }
        if (groups != null)
        {
            for (var index = 0; index < groupCount; index++)
            {
                var groupLine = lineOffset + groups[index];
                var groupPrefix = groupLine == line ? prefix : new SyntaxDiagnosticPrefix(path, subject, origin, groupLine);
                groupPrefix.AppendTo(diagnostic);
                diagnostic.Append(MissingConditionalGroup);
            }
        }
        if (reportNearToken)
        {
            prefix.AppendTo(diagnostic);
            diagnostic.Append(tokenPrefix);
            if (hasNearToken)
            {
                AppendToken(syntax.Token.AsSpan());
                diagnostic.Append("'\n"u8);
            }
        }
        if (includeLine)
        {
            prefix.AppendTo(diagnostic);
            diagnostic.Append((byte)'`').Append(syntax.Line.AsSpan()).Append("'\n"u8);
        }
        await ExecuteWriteAsync(descriptor, diagnostic.ToMemory()).ConfigureAwait(false);

        void AppendToken(ReadOnlySpan<byte> token)
        {
            while (token.IndexOf("\\\n"u8) is var continuation && continuation >= 0)
            {
                diagnostic.Append(token[..continuation]);
                token = token[(continuation + 2)..];
            }
            diagnostic.Append(token);
        }
    }

    private async Task ReportArithmeticForHeaderAsync(ArithmeticForHeaderError header, string? path,
        SyntaxDiagnosticOrigin origin, int line)
    {
        var descriptor = GetStdErrDescriptor();
        CheckCancellation();
        if (descriptor.IsClosed)
            return;

        var prefix = new SyntaxDiagnosticPrefix(path,
            origin == SyntaxDiagnosticOrigin.Script ? SyntaxDiagnosticOrigin.ScriptLine : origin, line);
        var reason = header.HasExtraSeparator ? "syntax error: `;' unexpected\n"u8
            : "syntax error: arithmetic expression required\n"u8;
        var expressionPrefix = "syntax error: `(("u8;
        var expressionSuffix = "))'\n"u8;
        var includeExpression = !ReportsOnlyPrimarySyntaxDiagnostic(origin);
        var length = (long)prefix.Length + reason.Length;
        if (includeExpression)
            length += prefix.Length + expressionPrefix.Length + (long)header.Expression.Length + expressionSuffix.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            throw new MemoryBudgetExceededException();

        using var memory = reservation;
        using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
        prefix.AppendTo(diagnostic);
        diagnostic.Append(reason);
        if (includeExpression)
        {
            prefix.AppendTo(diagnostic);
            diagnostic.Append(expressionPrefix).Append(header.Expression.AsSpan()).Append(expressionSuffix);
        }
        await ExecuteWriteAsync(descriptor, diagnostic.ToMemory()).ConfigureAwait(false);
    }

    private static readonly ShellValue HereDocumentWarningName = ShellValue.FromText("warning");
    private static readonly ShellValue HereDocumentWarningPrefix = ShellValue.FromText("here-document at line ");
    private static readonly ShellValue HereDocumentWarningDelimiter = ShellValue.FromText(" delimited by end-of-file (wanted `");
    private static readonly ShellValue HereDocumentWarningClose = ShellValue.FromText("')");
    private static readonly ShellValue PendingCaptureWarningPrefix = ShellValue.FromText("command substitution: ");
    private static readonly ShellValue PendingCaptureWarningSingular = ShellValue.FromText(" unterminated here-document");
    private static readonly ShellValue PendingCaptureWarningPlural = ShellValue.FromText(" unterminated here-documents");
    private static readonly byte[] UnreadConditionalToken =
        [.. "unexpected token `"u8, 0xff, .. "' in conditional command\n"u8];
    private static readonly byte[] UnexpectedSyntaxEndOfInput = "syntax error: unexpected end of file\n"u8.ToArray();
    private static readonly byte[] MissingCommandSubstitutionClose = "unexpected EOF while looking for matching `)'\n"u8.ToArray();
    private static readonly byte[] MissingDoubleQuote = "unexpected EOF while looking for matching `\"'\n"u8.ToArray();
    private static readonly byte[] MissingSingleQuote = "unexpected EOF while looking for matching `''\n"u8.ToArray();
    private static readonly byte[] MissingBacktick = "unexpected EOF while looking for matching ``'\n"u8.ToArray();
    private static readonly byte[] MissingConditionalUnaryArgument = "unexpected argument to conditional unary operator\n"u8.ToArray();
    private static readonly byte[] MissingConditionalBinaryArgument = "unexpected argument to conditional binary operator\n"u8.ToArray();
    private static readonly byte[] MissingConditionalBinaryOperator = "conditional binary operator expected\n"u8.ToArray();
    private static readonly byte[] MissingConditionalUnaryArgumentAtEnd = "unexpected argument `EOF' to conditional unary operator\n"u8.ToArray();
    private static readonly byte[] MissingConditionalBinaryArgumentAtEnd = "unexpected argument `EOF' to conditional binary operator\n"u8.ToArray();
    private static readonly byte[] MissingConditionalBinaryOperatorAtEnd = "unexpected token `EOF', conditional binary operator expected\n"u8.ToArray();
    private static readonly byte[] MissingConditionalGroup = "expected `)'\n"u8.ToArray();
    private static readonly byte[] MissingConditionalGroupAtEnd = "unexpected token `EOF', expected `)'\n"u8.ToArray();
    private static readonly byte[] MissingConditionalClose = "unexpected EOF while looking for `]]'\n"u8.ToArray();
    private static readonly byte[] MissingConditionalTermAtEnd = "unexpected token `EOF' in conditional command\n"u8.ToArray();

    private async Task ReportConditionalEndOfInputAsync(ConditionalEndOfInput failure, string? path,
        SyntaxDiagnosticOrigin origin, int lineOffset)
    {
        var descriptor = GetStdErrDescriptor();
        if (descriptor.IsClosed)
            return;
        lineOffset += failure.CaptureLineOffset.GetValueOrDefault();
        if (origin == SyntaxDiagnosticOrigin.Script)
            origin = SyntaxDiagnosticOrigin.ScriptLine;
        var primaryOnly = ReportsOnlyPrimarySyntaxDiagnostic(origin);
        if (failure.DoubleQuoted is { } doubleQuoted)
        {
            await WriteSyntaxDiagnosticLineAsync(descriptor, Prefix(failure.QuoteLine),
                doubleQuoted ? MissingDoubleQuote : MissingSingleQuote).ConfigureAwait(false);
            if (primaryOnly)
                return;
        }
        await WriteSyntaxDiagnosticLineAsync(descriptor, Prefix(failure.Diagnostic.ReasonLine),
            GetConditionalEndOfInputMessage(failure.Diagnostic.Error, failure.DoubleQuoted.HasValue)).ConfigureAwait(false);
        if (primaryOnly)
            return;
        if (failure.Diagnostic.UnclosedGroupLines is { } groups)
            foreach (var groupLine in groups)
                await WriteSyntaxDiagnosticLineAsync(descriptor, Prefix(groupLine), MissingConditionalGroup).ConfigureAwait(false);
        if (!failure.DoubleQuoted.HasValue)
            await WriteSyntaxDiagnosticLineAsync(descriptor, Prefix(failure.EndLine), UnexpectedSyntaxEndOfInput).ConfigureAwait(false);

        SyntaxDiagnosticPrefix Prefix(int line) => new(path, origin, lineOffset + line);
    }

    private static ReadOnlyMemory<byte> GetConditionalEndOfInputMessage(ConditionalReaderError error, bool followsUnclosedDelimiter) => error switch
    {
        ConditionalReaderError.UnaryOperand => followsUnclosedDelimiter ? MissingConditionalUnaryArgument : MissingConditionalUnaryArgumentAtEnd,
        ConditionalReaderError.BinaryOperand => followsUnclosedDelimiter ? MissingConditionalBinaryArgument : MissingConditionalBinaryArgumentAtEnd,
        ConditionalReaderError.BinaryOperator => followsUnclosedDelimiter ? MissingConditionalBinaryOperator : MissingConditionalBinaryOperatorAtEnd,
        ConditionalReaderError.GroupClose => followsUnclosedDelimiter ? MissingConditionalGroup : MissingConditionalGroupAtEnd,
        ConditionalReaderError.MissingCloser => MissingConditionalClose,
        _ => followsUnclosedDelimiter ? UnreadConditionalToken : MissingConditionalTermAtEnd
    };

    internal async Task ReportHereDocumentCaptureEndOfInputAsync(
        UnclosedDelimiter endOfInput, CaptureHereDocumentWarnings? warnings, ScopedEvaluation scope)
    {
        if (warnings is { } captureWarnings)
            await ReportHereDocumentCaptureWarningsAsync(captureWarnings.Warnings, scope).ConfigureAwait(false);
        var origin = endOfInput.PayloadLine != 0
            || (scope.Context.Coordinates.CaptureFunctionDepth.HasValue && !scope.InNestedCommandUnits)
            ? SyntaxDiagnosticOrigin.CommandSubstitution
            : scope.InEvalReader ? SyntaxDiagnosticOrigin.Eval : SyntaxDiagnosticOrigin.ScriptLine;
        await ReportUnclosedDelimiterAsync(endOfInput, HereDocumentExpansionLine(scope) + endOfInput.PayloadLine,
            scope.Context.DiagnosticPath, origin).ConfigureAwait(false);
    }

    internal Task ReportHereDocumentCaptureWarningsAsync(IReadOnlyList<HereDocumentWarning> warnings, ScopedEvaluation scope) =>
        ReportHereDocumentWarningsAsync(warnings, HereDocumentExpansionLine(scope), scope.Context.DiagnosticPath,
            useDocumentLine: false, fixedPrefix: GetHereDocumentWarningPrefix(scope));

    private static SyntaxDiagnosticPrefix GetHereDocumentParameterDiagnosticPrefix(ScopedEvaluation scope) =>
        GetHereDocumentWarningPrefix(scope) ?? GetCommandDiagnosticPrefix(scope.Context, HereDocumentExpansionLine(scope));

    private static SyntaxDiagnosticPrefix? GetHereDocumentWarningPrefix(ScopedEvaluation scope)
    {
        var line = scope.Context.Coordinates.HereDocumentWarningLine;
        return line != 0
            ? new SyntaxDiagnosticPrefix(scope.Context.DiagnosticPath, SyntaxDiagnosticOrigin.ScriptLine, line)
            : null;
    }

    internal static int HereDocumentExpansionLine(ScopedEvaluation scope)
    {
        var coordinates = scope.Context.Coordinates;
        if (coordinates.CompoundHereDocumentLine == 0)
            return coordinates.Line;
        var readerDepth = coordinates.CaptureFunctionDepth ?? scope.LoadedSyntaxFunctionDepth.GetValueOrDefault();
        if ((scope.Context.FunctionContext?.Depth ?? 0) <= readerDepth)
            return coordinates.CompoundHereDocumentLine;
        return coordinates.FunctionBodyLine != 0 ? coordinates.FunctionBodyLine : coordinates.Line;
    }

    private async Task ReportUnclosedDelimiterAsync(
        UnclosedDelimiter endOfInput, int line, string? path, SyntaxDiagnosticOrigin origin)
    {
        var descriptor = GetStdErrDescriptor();
        if (descriptor.IsClosed)
            return;
        if (origin == SyntaxDiagnosticOrigin.Script && (endOfInput.AtOpeningDelimiter || endOfInput.IncludeScriptLine))
            origin = SyntaxDiagnosticOrigin.ScriptLine;
        var prefix = new SyntaxDiagnosticPrefix(path, origin, line);
        var message = endOfInput.Delimiter switch
        {
            ')' => MissingCommandSubstitutionClose,
            '"' => MissingDoubleQuote,
            '\'' => MissingSingleQuote,
            '`' => MissingBacktick,
            _ => Utf8Text.EncodeLine($"unexpected EOF while looking for matching `{endOfInput.Delimiter}'")
        };
        await WriteSyntaxDiagnosticLineAsync(descriptor, prefix, message).ConfigureAwait(false);
        if (endOfInput.Conditional is { } conditional && !ReportsOnlyPrimarySyntaxDiagnostic(origin))
        {
            var lineOffset = line - conditional.ReadLine;
            await WriteSyntaxDiagnosticLineAsync(descriptor, new SyntaxDiagnosticPrefix(path, origin, lineOffset + conditional.ReasonLine),
                GetConditionalEndOfInputMessage(conditional.Error, followsUnclosedDelimiter: true)).ConfigureAwait(false);
            if (conditional.UnclosedGroupLines is { } groups)
                foreach (var groupLine in groups)
                    await WriteSyntaxDiagnosticLineAsync(descriptor, new SyntaxDiagnosticPrefix(path, origin, lineOffset + groupLine),
                        MissingConditionalGroup).ConfigureAwait(false);
        }
    }

    private async Task ReportReaderGrammarEndOfInputAsync(string? path, SyntaxDiagnosticOrigin origin, int line)
    {
        var descriptor = GetStdErrDescriptor();
        if (!descriptor.IsClosed)
            await WriteSyntaxDiagnosticLineAsync(descriptor, new SyntaxDiagnosticPrefix(path, origin, line),
                UnexpectedSyntaxEndOfInput).ConfigureAwait(false);
    }

    private async Task WriteSyntaxDiagnosticLineAsync(FileDescriptor descriptor, SyntaxDiagnosticPrefix prefix,
        ReadOnlyMemory<byte> message)
    {
        if (prefix.Length == 0)
        {
            await ExecuteWriteAsync(descriptor, message).ConfigureAwait(false);
            return;
        }
        var length = (long)prefix.Length + message.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            throw new MemoryBudgetExceededException();
        using var memory = reservation;
        using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
        prefix.AppendTo(diagnostic);
        diagnostic.Append(message);
        await ExecuteWriteAsync(descriptor, diagnostic.ToMemory()).ConfigureAwait(false);
    }

    private Task ReportCaptureHereDocumentWarningsAsync(CaptureHereDocumentWarnings captured, string? path, int offset, int line) =>
        ReportHereDocumentWarningsAsync(captured.Warnings, offset + captured.LineOffset, path, useDocumentLine: false,
            fixedPrefix: new SyntaxDiagnosticPrefix(path,
                line > 0 ? SyntaxDiagnosticOrigin.ScriptLine : SyntaxDiagnosticOrigin.Script, Math.Max(1, line)));

    private async Task ReportHereDocumentWarningsAsync(
        IReadOnlyList<HereDocumentWarning> warnings, int offset, string? path, bool useDocumentLine, SyntaxDiagnosticPrefix? fixedPrefix)
    {
        var descriptor = GetStdErrDescriptor();
        if (descriptor.IsClosed)
            return;

        foreach (var warning in warnings)
        {
            CheckCancellation();
            var warningLine = warning.Line + offset;
            var readerLine = useDocumentLine && warning.PendingCaptureDocuments == 0 ? warning.Line : warning.ReaderLine;
            var reachedPrefix = fixedPrefix ?? (readerLine == 0 ? default
                : new SyntaxDiagnosticPrefix(path, SyntaxDiagnosticOrigin.ScriptLine, readerLine + offset));
            if (warning.PendingCaptureDocuments != 0)
            {
                await WriteDiagnosticAsync(descriptor, [HereDocumentWarningName],
                    [PendingCaptureWarningPrefix, ShellValue.FromInteger(warning.PendingCaptureDocuments),
                        warning.PendingCaptureDocuments == 1 ? PendingCaptureWarningSingular : PendingCaptureWarningPlural],
                    scope: null, reachedPrefix).ConfigureAwait(false);
                continue;
            }
            await WriteDiagnosticAsync(descriptor, [HereDocumentWarningName],
                [HereDocumentWarningPrefix, ShellValue.FromInteger(warningLine), HereDocumentWarningDelimiter,
                    warning.Delimiter.ToValue(), HereDocumentWarningClose], scope: null, reachedPrefix).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Emits an append operation, retaining downstream closure and any known failure cause.
    /// </summary>
    internal async Task<DescriptorWriteResult> ExecuteWriteAsync(FileDescriptor fileDescriptor, ReadOnlyMemory<byte> content)
    {
        CheckCancellation();
        // Keep the buffer and descriptor borrowed until the host finishes the write.
        var result = await WriteOperation.AsTask(PublishOperation, fileDescriptor, content).ConfigureAwait(false);
        CheckCancellation();
        return result;
    }

    /// <summary>
    /// Executes file tests and evaluates the aggregated result with a provided evaluator.
    /// </summary>
    private async Task<int> ExecuteFileTestsForEvaluationAsync(
        IReadOnlyList<UnresolvedFileTest> tests,
        Func<IReadOnlyList<bool>, bool> evaluator)
    {
        CheckCancellation();
        using var lookupMemory = new MemoryCapacity(Memory.Budget,
            MemoryCapacity.ArrayBackingBytes<int>(tests.Count) + MemoryCapacity.ArrayBackingBytes<bool>(tests.Count));
        var currentDirectory = _state.CurrentDirectory;
        List<FileDescriptor>? terminalDescriptors = null;
        List<int>? terminalResultIndices = null;
        PathLookupBatch<FilePathLookup>? kindPaths = null;
        PathLookupBatch<FilePathLookup>? metadataPaths = null;
        PathLookupBatch<FilePathLookup>? sizePaths = null;
        var pathResultIndices = new int[tests.Count];
        Array.Fill(pathResultIndices, -1);
        List<(int TestIndex, FileTimeComparison Comparison, int Left, int Right)>? comparisons = null;
        List<(int TestIndex, int Left, int Right)>? identityTests = null;
        var results = new bool[tests.Count];
        for (var index = 0; index < tests.Count; index++)
        {
            CheckCancellation();
            var test = tests[index];
            if (test.Kind.IsTerminal)
            {
                lookupMemory.PrepareListCapacity(terminalDescriptors ??= [], 1);
                lookupMemory.PrepareListCapacity(terminalResultIndices ??= [], 1);
                terminalDescriptors.Add(_state.CurrentRedirectionTable?.Resolve(test.TerminalDescriptor) ?? FileDescriptor.Closed);
                terminalResultIndices.Add(index);
            }
            else if (test.Kind.ComparesIdentity)
            {
                if (test.SecondaryOperand is { } secondary
                    && TryReadFileOperand(test.Operand, currentDirectory, out var left) && left is not null
                    && TryReadFileOperand(secondary, currentDirectory, out var right) && right is not null)
                {
                    lookupMemory.PrepareListCapacity(identityTests ??= [], 1);
                    metadataPaths ??= new();
                    identityTests.Add((index, AddPath(metadataPaths, left, FileLinkFollowing.Follow),
                        AddPath(metadataPaths, right, FileLinkFollowing.Follow)));
                }
            }
            else if (test.Kind.TimeComparison is { } comparison)
            {
                if (test.SecondaryOperand is { } secondary
                    && TryReadFileOperand(test.Operand, currentDirectory, out var left)
                    && TryReadFileOperand(secondary, currentDirectory, out var right))
                {
                    lookupMemory.PrepareListCapacity(comparisons ??= [], 1);
                    comparisons.Add((index, comparison,
                        left is null ? -1 : AddPath(metadataPaths ??= new(), left, FileLinkFollowing.Follow),
                        right is null ? -1 : AddPath(metadataPaths ??= new(), right, FileLinkFollowing.Follow)));
                }
            }
            else
            {
                var following = test.Kind.RequiredKind == FilePathKind.SymbolicLink
                    ? FileLinkFollowing.NoFollow : FileLinkFollowing.Follow;
                if (!TryReadFileOperand(test.Operand, currentDirectory, out var path) || path is null)
                    continue;
                var batch = test.Kind.MetadataPredicate is not null ? (metadataPaths ??= new())
                    : test.Kind.IsNonEmpty ? (sizePaths ??= new()) : (kindPaths ??= new());
                pathResultIndices[index] = AddPath(batch, path, following);
            }
        }

        // The evaluator retains one ordered response slot per request through result mapping.
        // Referenced payloads and provider scratch remain under their original owners.
        lookupMemory.Grow(MemoryCapacity.ArrayBackingBytes<FilePathResolutionResult>(kindPaths?.Paths.Count ?? 0)
            + MemoryCapacity.ArrayBackingBytes<FilePathResolutionResult>(metadataPaths?.Paths.Count ?? 0)
            + MemoryCapacity.ArrayBackingBytes<FilePathResolutionResult>(sizePaths?.Paths.Count ?? 0)
            + MemoryCapacity.ArrayBackingBytes<bool>(terminalDescriptors?.Count ?? 0));

        // Kind, metadata and exact-size failures remain independent. Hosts validate traversal
        // before fetching content; lexical normalization here would erase symbolic-link semantics.
        var kinds = await ReadPathsAsync(kindPaths, FilePathInfoRequest.KindOnly).ConfigureAwait(false);
        var infos = await ReadPathsAsync(metadataPaths, FilePathInfoRequest.Metadata).ConfigureAwait(false);
        var sizes = await ReadPathsAsync(sizePaths, FilePathInfoRequest.MetadataWithExactSize).ConfigureAwait(false);
        for (var index = 0; index < tests.Count; index++)
        {
            CheckCancellation();
            var pathIndex = pathResultIndices[index];
            if (pathIndex < 0)
                continue;
            var test = tests[index];
            var result = test.Kind.MetadataPredicate is not null ? infos[pathIndex]
                : test.Kind.IsNonEmpty ? sizes[pathIndex] : kinds[pathIndex];
            if (result.Error is not null)
                continue;
            var info = result.Info;
            if (test.Kind.MetadataPredicate is { } predicate)
            {
                if (!info.HasMetadata)
                    continue;
                results[index] = predicate switch
                {
                    FileMetadataPredicate.ModifiedSinceAccess => info.Metadata.AccessedAt is { } accessedAt
                        && info.Metadata.ModifiedAt > accessedAt,
                    FileMetadataPredicate.Writable => (info.Metadata.EffectiveAccess & FileAccessRights.Write) != 0,
                    FileMetadataPredicate.Readable => (info.Metadata.EffectiveAccess & FileAccessRights.Read) != 0,
                    FileMetadataPredicate.Executable => (info.Metadata.EffectiveAccess & FileAccessRights.Execute) != 0,
                    FileMetadataPredicate.SetUserId => info.Metadata.SpecialModeBits is { } userBits
                        && (userBits & FileSpecialModeBits.SetUserId) != 0,
                    FileMetadataPredicate.SetGroupId => info.Metadata.SpecialModeBits is { } groupBits
                        && (groupBits & FileSpecialModeBits.SetGroupId) != 0,
                    FileMetadataPredicate.Sticky => info.Metadata.SpecialModeBits is { } stickyBits
                        && (stickyBits & FileSpecialModeBits.Sticky) != 0,
                    FileMetadataPredicate.OwnedByEffectiveUser => info.Metadata.OwnershipMatch is { } userOwnership
                        && (userOwnership & FileOwnershipMatch.EffectiveUser) != 0,
                    FileMetadataPredicate.OwnedByEffectiveGroup => info.Metadata.OwnershipMatch is { } groupOwnership
                        && (groupOwnership & FileOwnershipMatch.EffectiveGroup) != 0,
                    _ => throw new InvalidOperationException("Unsupported metadata predicate.")
                };
            }
            else if (test.Kind.IsNonEmpty)
                results[index] = info.Kind != FilePathKind.Missing && info.Metadata.GetRequiredSize() > 0;
            else
                results[index] = test.Kind.RequiredKind is { } requiredKind
                    ? info.Kind == requiredKind : info.Kind != FilePathKind.Missing;
        }
        if (comparisons != null)
        {
            foreach (var comparison in comparisons)
                results[comparison.TestIndex] = CompareTimes(comparison.Comparison,
                    comparison.Left < 0 ? default : infos[comparison.Left],
                    comparison.Right < 0 ? default : infos[comparison.Right]);
        }
        if (identityTests != null)
        {
            foreach (var test in identityTests)
            {
                var left = infos[test.Left];
                var right = infos[test.Right];
                if (left.Error is not null || right.Error is not null)
                    continue;
                results[test.TestIndex] = left.Info.HasMetadata && right.Info.HasMetadata
                    && left.Info.Metadata.Identity is { } leftIdentity
                    && right.Info.Metadata.Identity is { } rightIdentity && leftIdentity == rightIdentity;
            }
        }
        if (terminalDescriptors != null && terminalResultIndices != null)
        {
            CheckCancellation();
            var terminalStatuses = await TerminalStatusesOperation.AsTask(PublishOperation, terminalDescriptors)
                .ConfigureAwait(false);
            CheckCancellation();
            for (var index = 0; index < terminalDescriptors.Count; index++)
                results[terminalResultIndices[index]] = terminalStatuses[index];
        }
        CheckCancellation();
        return evaluator(results) ? 0 : 1;

        int AddPath(PathLookupBatch<FilePathLookup> batch, RawShellPath operand, FileLinkFollowing following)
        {
            var bytes = 40L;
            if (operand.Raw[0] != '/')
            {
                var prefixLength = currentDirectory.IsRoot ? 1 : currentDirectory.Path.Length + 1;
                bytes += 32L + 2L * (prefixLength + operand.Raw.Length);
            }

            lookupMemory.Grow(bytes);
            var count = batch.Paths.Count;
            var index = batch.Add(new FilePathLookup(operand, currentDirectory, following), lookupMemory);
            if (batch.Paths.Count == count)
                lookupMemory.Resize(lookupMemory.Bytes - bytes);
            return index;
        }

        async Task<IReadOnlyList<FilePathResolutionResult>> ReadPathsAsync(
            PathLookupBatch<FilePathLookup>? paths, FilePathInfoRequest request)
        {
            CheckCancellation();
            if (paths == null)
                return Array.Empty<FilePathResolutionResult>();
            // A cancelled wait cannot release the borrowed paths before the host has settled.
            var infos = await PathInfosOperation.AsTask(PublishOperation, paths.Paths, request)
                .ConfigureAwait(false);
            CheckCancellation();
            return infos;
        }

        static bool CompareTimes(FileTimeComparison comparison, FilePathResolutionResult left, FilePathResolutionResult right)
        {
            // Time comparisons treat a non-directory traversal as a missing operand.
            // Other lookup failures do not establish absence, even when the other operand exists.
            if (left.Error is { Kind: not OperationErrorKind.NotDirectory }
                || right.Error is { Kind: not OperationErrorKind.NotDirectory })
                return false;
            var leftInfo = left.Error is null ? left.Info : FilePathInfo.FromKind(FilePathKind.Missing);
            var rightInfo = right.Error is null ? right.Info : FilePathInfo.FromKind(FilePathKind.Missing);
            return comparison switch
            {
                FileTimeComparison.NewerThan => leftInfo.Kind != FilePathKind.Missing
                    && (rightInfo.Kind == FilePathKind.Missing || leftInfo.Metadata.ModifiedAt > rightInfo.Metadata.ModifiedAt),
                FileTimeComparison.OlderThan => rightInfo.Kind != FilePathKind.Missing
                    && (leftInfo.Kind == FilePathKind.Missing || leftInfo.Metadata.ModifiedAt < rightInfo.Metadata.ModifiedAt),
                _ => throw new ArgumentOutOfRangeException(nameof(comparison))
            };
        }
    }

    /// <summary>
    /// Emits a bounded line-read operation on a borrowed descriptor and returns its result.
    /// </summary>
    /// <param name="destinationBudget">Owns the copied batch, or null to retain it with the descriptor.</param>
    /// <param name="fileDescriptor">The host descriptor to read.</param>
    /// <param name="limit">The positive maximum number of records to return.</param>
    public Task<ReadResult> ExecuteReadAsync(FileDescriptor fileDescriptor, int limit, IMemoryBudget? destinationBudget) =>
        ExecuteReadAsync(fileDescriptor, limit, destinationBudget, reusableLine: null, sourceDestination: null);

    private async Task<ReadResult> ExecuteReadAsync(
        FileDescriptor fileDescriptor, int limit, IMemoryBudget? destinationBudget, ByteMemoryBuilder? reusableLine,
        SourceLineDestination? sourceDestination)
    {
        CheckCancellation();
        // The host may still be reading into storage owned by the caller after cancellation.
        var result = await ReadLineOperation.AsTask(PublishOperation, fileDescriptor, limit, destinationBudget, reusableLine,
                sourceDestination)
            .ConfigureAwait(false);
        CheckCancellation();
        return result;
    }

    /// <summary>
    /// Reads bounded source, then executes complete command units in the caller's context.
    /// </summary>
    private async Task<EvaluationResult> ExecuteSourceAsync(string commandName, string path, ScopedEvaluation scope)
    {
        var searchPath = !path.Contains('/') && scope.Variables.TryGetScalar("PATH", out var value)
            ? value.Bytes : ReadOnlyMemory<byte>.Empty;
        using var lookupMemory = searchPath.IsEmpty ? null : new MemoryCapacity(Memory.Budget, 0);
        var (open, openedPath, currentSearchEntry) = lookupMemory != null && RawShellPath.TryParse(path, out var fileName)
            ? await SearchSourcePathAsync(fileName, searchPath, scope, lookupMemory).ConfigureAwait(false)
            : (await ExecuteOpenFileAsync(path, FileOpenMode.Read).ConfigureAwait(false), path, false);
        var descriptor = open.FileDescriptor;
        if (!descriptor.HasValue)
        {
            if (open.Error is { Message.Length: > 0 } error)
                await ReportSourceOpenFailureAsync(commandName, openedPath, error, scope.Context).ConfigureAwait(false);
            return EvaluationResult.FromExitCode(1);
        }

        var sourceMemory = new SourceMemory(Memory.Budget.CreateScope());
        OperationError? closeError = null;
        try
        {
            BoundedReadResult input;
            var readFailed = false;
            Exception? readException = null;
            try
            {
                var expectedLength = open.ReadSize is { } size && size <= Parser.MaximumScriptBytes ? (int)size : 0;
                var output = new ByteMemoryBuilder(sourceMemory.Budget, expectedLength);
                var sourceDestination = new SourceLineDestination(output, Parser.MaximumScriptBytes);
                using var reader = AsyncLineReader.CreateStreaming(
                    (limit, memory, _) => new ValueTask<ReadResult>(
                        ExecuteReadAsync(descriptor.Value, limit, memory, reusableLine: null, sourceDestination)),
                    _cancellationToken, batchSize: 128, sourceMemory.Budget);
                input = await reader.TryReadToEndAsync(Parser.MaximumScriptBytes, output).ConfigureAwait(false);
                readFailed = input is not BoundedReadResult.Complete;
            }
            catch (Exception error)
            {
                readException = error;
                throw;
            }
            finally
            {
                closeError = await CloseDescriptorsAsync([descriptor.Value], operationFailed: readException != null || readFailed).ConfigureAwait(false);
            }

            if (closeError is { } closeFailure)
            {
                await WriteOperationDiagnosticAsync(GetStdErrDescriptor(), "shell: ", openedPath, closeFailure).ConfigureAwait(false);
                return EvaluationResult.FromExitCode(1);
            }
            if (input is BoundedReadResult.Failed)
                return EvaluationResult.FromExitCode(1);
            if (input is BoundedReadResult.TooLarge)
            {
                await ReportSyntaxErrorAsync(new SyntaxError(Parser.ScriptSizeLimitMessage, 0)).ConfigureAwait(false);
                return EvaluationResult.FromExitCode(2);
            }

            // The source can outlive this invocation through its function definitions.
            sourceMemory.OwnDiagnosticPath(openedPath, currentSearchEntry);
            lookupMemory?.Dispose();
            // The active command owns the frame until the sourced invocation and its children settle.
            Memory.Charge(48);
            var sourceScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth, scope.Context.FunctionContext, ReturnTargetKind.Source)
                {
                    GlobalPrefixes = scope.Context.GlobalPrefixes,
                    ErrexitIgnored = scope.Context.ErrexitIgnored,
                    CallerSource = new SourceCallerFrame(scope.Context.FunctionContext?.Depth ?? 0,
                        scope.Context.CallerSource),
                    SourceMemory = sourceMemory
                }
            };
            return await EvaluateCommandUnitsAsync(
                ShellSource.FromBytes(((BoundedReadResult.Complete)input).Content), sourceScope, ScriptExecutionKind.Source)
                .ConfigureAwait(false);
        }
        finally
        {
            sourceMemory.Release();
        }
    }

    /// <summary>
    /// Emits a run-command operation and renders any negotiated launch diagnostic.
    /// </summary>
    private async Task<CommandExecutionResult> ExecuteRunCommandAsync(CommandInvocation command, ScopedEvaluation scope, bool exec)
    {
        CheckCancellation();
        // The current redirection frame retains stderr until the command and any launch diagnostic settle.
        var result = await RunCommand.AsTask(PublishOperation, command, canReportLaunchFailure: true, out var request)
            .ConfigureAwait(false);
        CheckCancellation();
        if (!request.CallerReportsLaunchFailure || command.StdErr.IsClosed || result is CommandExecutionResult.Completed)
            return result;

        const string cannotExecute = "cannot execute: ";
        var name = command.CommandName;
        var directPath = name.Contains('/');
        var error = result is CommandExecutionResult.NotExecutable notExecutable ? notExecutable.Error
            : new OperationError(directPath ? "No such file or directory\n" : exec ? "not found\n" : "command not found\n");
        var directExec = exec && directPath;
        var directory = directExec && !name.StartsWith('/') ? command.CurrentDirectory : null;
        var sourcePrefix = GetCommandDiagnosticPrefix(scope);
        var namedExec = exec && !directPath;
        var prefix = namedExec ? "exec: " : string.Empty;
        var detail = namedExec && error.Kind == OperationErrorKind.IsDirectory ? cannotExecute : string.Empty;
        var written = await WriteOperationDiagnosticAsync(command.StdErr, ShellValue.Empty, prefix, name, error,
            sourcePrefix, directory, detail).ConfigureAwait(false);
        if (written is { CanAcceptMore: true } && directExec
            && error.Kind is OperationErrorKind.IsDirectory or OperationErrorKind.NotDirectory or OperationErrorKind.PermissionDenied)
        {
            await WriteOperationDiagnosticAsync(command.StdErr, ShellValue.Empty, "exec: ", name, error, sourcePrefix,
                directory, cannotExecute).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Emits a create-pipe operation and returns both endpoints or an acquisition failure.
    /// </summary>
    internal async Task<PipeCreationResult> ExecuteCreatePipeAsync()
    {
        CheckCancellation();
        var result = await CreatePipeOperation.AsTask(PublishOperation).ConfigureAwait(false);
        if (_cancellationToken.IsCancellationRequested)
        {
            // A successful acquisition transfers both handles even if cancellation raced the response.
            if (result.Descriptors is { } pipe)
                await CloseDescriptorsAsync([pipe.Write, pipe.Read], operationFailed: true).ConfigureAwait(false);
            CheckCancellation();
        }
        return result;
    }

    /// <summary>Transfers a here-input's writer/payload to an independent owner registered on its reader.</summary>
    internal async Task<OperationError?> StartPipeInputAsync(
        PipeDescriptors pipe, IReadOnlyList<ExpandedWord> fields, int length, bool appendNewline)
    {
        var inputs = _pipeInputs ?? throw new InvalidOperationException("Here-input registration was not prepared.");
        PipeInputWriter writer;
        try
        {
            CheckCancellation();
            writer = new PipeInputWriter(pipe, fields, length, appendNewline, Memory.Budget);
        }
        catch
        {
            await CloseDescriptorsAsync([pipe.Write], operationFailed: true).ConfigureAwait(false);
            throw;
        }
        inputs.Add(pipe.Read, writer);
        var completion = new TaskCompletionSource<OperationError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        PublishOperation(new StartPipeInputOperation(completion, writer));
        // Keep the registration/payload until dispatch settles, even if the caller cancels startup.
        var result = await completion.Task.ConfigureAwait(false);
        CheckCancellation();
        return result;
    }

    /// <summary>
    /// Emits a list-directory operation and applies glob expansion's error policy.
    /// </summary>
    private async Task<IReadOnlyList<DirectoryEntry>> ExecuteListDirectoryAsync(string path)
    {
        CheckCancellation();
        if (!RawShellPath.TryParse(path, out var raw)) return [];
        // Keep the candidate generation admitted until the host settles its borrowed path.
        var result = await ListDirectoryOperation.AsTask(PublishOperation,
            new FilePathLookup(raw, _state.CurrentDirectory, FileLinkFollowing.Follow)).ConfigureAwait(false);
        CheckCancellation();
        // Globs treat expected listing failures as no matches. Other failures still propagate.
        return result.Error is null ? result.Entries : [];
    }

    private static bool TryReadFileOperand(string operand, ShellPath currentDirectory, out RawShellPath? path)
    {
        // An empty operand denotes a missing path, including in time comparisons.
        path = null;
        if (operand.Length == 0)
            return true;
        var prefix = operand.StartsWith('/') ? "" : currentDirectory.Path;
        return PathResolution.FitsPathLimits(prefix, operand) && RawShellPath.TryParse(operand, out path);
    }

    /// <summary>
    /// Emits an open-file operation and returns the allocated descriptor.
    /// </summary>
    internal Task<FileOpenResult> ExecuteOpenFileAsync(string path, FileOpenMode mode)
    {
        if (!RawShellPath.TryParse(path, out var raw))
            return Task.FromResult(FileOpenResult.Failed(new OperationError("Invalid file path\n")));
        return ExecuteOpenFileAsync(raw, mode);
    }

    private Task<FileOpenResult> ExecuteOpenFileAsync(RawShellPath path, FileOpenMode mode)
    {
        CheckCancellation();
        // A successful open transfers ownership even if cancellation races its completion.
        return OpenFileOperation.AsTask(PublishOperation,
            new FilePathLookup(path, _state.CurrentDirectory, FileLinkFollowing.Follow), mode);
    }

    /// <summary>Transfers an owned descriptor to the host and awaits cleanup without cancellation.</summary>
    private Task<OperationError?> CloseOwnedDescriptorAsync(FileDescriptor descriptor) =>
        CloseOwnedDescriptorAsync(descriptor, this);

    private async Task<OperationError?> CloseOwnedDescriptorAsync(FileDescriptor descriptor, Evaluation owner)
    {
        var source = new TaskCompletionSource<OperationError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = owner._pipeInputs != null && owner._pipeInputs.Remove(descriptor, out var writer) ? writer : null;
        try
        {
            PublishOperation(new CloseDescriptorOperation(source, descriptor) { PipeInput = input });
            return await source.Task.ConfigureAwait(false);
        }
        finally
        {
            owner.MarkDescriptorClosed(descriptor);
        }
    }

    /// <summary>Reads a file substitution's redirection in the selected shell context.</summary>
    internal async Task<CommandSubstitutionResult> ReadInputSubstitutionAsync(
        IReadOnlyList<Redirection> redirections, ScopedEvaluation scope, bool writeToCapture)
    {
        var redirectionScope = await OpenRedirectionScopeAsync(redirections, scope,
            RedirectionPersistence.None, RedirectionLifetime.Command, RedirectionOrigin.InputSubstitution, reportMoveRestorationFailure: false)
            .ConfigureAwait(false);
        var result = new CommandSubstitutionResult(ShellValue.Empty, 1, IgnoredNuls: false);
        OperationError? closeError;
        try
        {
            if (_state.FatalExpansion is { } targetFailure)
                result = result with { ExitCode = targetFailure.ExitCode };
            else if (_state.TryConsumeExpansionError(out var redirectionStatus))
            {
                result = result with { ExitCode = redirectionStatus };
                if (!writeToCapture && redirectionScope.InputOpenFailed && _state.ErrexitEnabled)
                    SetFatalExpansionError(ExpansionFailureKind.InputSubstitution);
            }
            else if (writeToCapture)
            {
                CheckCancellation();
                var completion = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                PublishOperation(new CopySubstitutionInputOperation(completion, GetStdInDescriptor(), GetStdOutDescriptor()));
                var copy = await completion.Task.ConfigureAwait(false);
                CheckCancellation();
                if (copy.Error is { } copyError)
                    await ReportOperationErrorAsync(copyError).ConfigureAwait(false);
                if (copy.CanAcceptMore)
                    result = result with { ExitCode = 0 };
            }
            else
            {
                using var outputMemory = Memory.Budget.CreateScope();
                CheckCancellation();
                var completion = new TaskCompletionSource<BoundedReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                PublishOperation(new ReadSubstitutionInputOperation(completion, GetStdInDescriptor(), outputMemory));
                // Keep both the read buffer owner and the descriptor until the host operation settles.
                var read = await completion.Task.ConfigureAwait(false);
                CheckCancellation();
                switch (read)
                {
                    case BoundedReadResult.Complete complete:
                        result = CommandSubstitutionResult.FromCapture(complete.Content, 0, Memory);
                        break;
                    case BoundedReadResult.Failed { Failure.Error: { } error }:
                        await ReportOperationErrorAsync(error).ConfigureAwait(false);
                        break;
                    case BoundedReadResult.TooLarge:
                        throw new ByteMemoryBuilder.LimitExceededException();
                    default:
                        throw new InvalidOperationException("Unexpected file substitution read outcome.");
                }
            }
        }
        finally
        {
            closeError = await redirectionScope.CloseAsync(operationFailed: result.ExitCode != 0).ConfigureAwait(false);
        }
        if (closeError is { } failure)
        {
            await ReportOperationErrorAsync(failure).ConfigureAwait(false);
            return result with { ExitCode = 1 };
        }
        return result;
    }

    private static readonly ReadOnlyMemory<byte> CommandSubstitutionNulWarning =
        "warning: command substitution: ignored null byte in input\n"u8.ToArray();

    /// <summary>
    /// Returns substitution bytes after discarding NULs and trailing LF bytes, preserving CR.
    /// Records nested status for later expansions and assignment-only completion.
    /// </summary>
    internal async Task<ShellValue> ExecuteCommandSubstitutionAsync(CommandSubstitutionSyntax script, ScopedEvaluation scope)
    {
        // Before the child starts, the caller reads the rendered capture from its current line.
        if (script is CommandSubstitutionSyntax.Parsed parsed)
            ApplyReadFunctionBraceLine(scope.Context.Coordinates.Line, parsed.Script.RenderedFunctionBraceLine);
        var evaluation = new CommandSubstitutionEvaluation(script);
        var result = await evaluation.EvaluateAsync(scope).ConfigureAwait(false);
        _state.LastExitCode = result.ExitCode;
        _state.LastCommandSubstitutionExitCode = result.ExitCode;
        if (result.IgnoredNuls)
            await ExecuteWriteAsync(GetStdErrDescriptor(), CommandSubstitutionNulWarning).ConfigureAwait(false);
        return result.Output;
    }

    /// <summary>
    /// Resolves STDOUT descriptor, honoring redirection context and explicit overrides.
    /// </summary>
    internal FileDescriptor GetStdOutDescriptor() =>
        _state.GetStdOutOverride() ?? _stdOut;

    /// <summary>
    /// Resolves STDIN descriptor, honoring redirection context and explicit overrides.
    /// </summary>
    private FileDescriptor GetStdInDescriptor() =>
        _state.GetStdInOverride() ?? _stdIn;

    /// <summary>
    /// Resolves STDERR descriptor, honoring redirection context and explicit overrides.
    /// </summary>
    internal FileDescriptor GetStdErrDescriptor() =>
        _state.GetStdErrOverride() ?? _stdErr;

    /// <summary>
    /// Removes a trailing array subscript from a variable name when present.
    /// </summary>
    private static BindingName StripArraySubscript(BindingName name)
    {
        if (name.IsIdentifier) return name;
        var span = name.ToValue().Bytes.Span;
        var bracketIndex = -1;
        for (var index = 0; index < span.Length; index++)
        {
            if (span[index] == '[')
            {
                bracketIndex = index;
                break;
            }
        }

        if (bracketIndex < 0 || span[^1] != ']')
        {
            return name;
        }

        return BindingName.FromValue(ShellValue.FromBytes(name.ToValue().Bytes[..bracketIndex]));
    }

    private static readonly ShellValue ArithmeticCommandName = ShellValue.FromText("((");

    /// <summary>Expands a reached command or loop operand once before numeric evaluation.</summary>
    private Task<long?> EvaluateArithmeticCommandOperandAsync(ArithmeticSource expression, ScopedEvaluation scope)
    {
        return AwaitWordExpansionAsync(ExpandAndEvaluateAsync());

        async Task<long?> ExpandAndEvaluateAsync()
        {
            var operand = await WordExpansion.PrepareArithmeticAsync(expression, scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
            return operand == null ? null : await WordExpansion.EvaluateArithmeticAsync(
                operand, scope, WordExpansion.ArithmeticFailureMode.Diagnostic, ArithmeticCommandName).ConfigureAwait(false);
        }
    }

    /// <summary>Reads captured fields followed by a borrowed range of immutable source words.</summary>
    private readonly struct CommandWords(IReadOnlyList<Word> source, int start, ReadOnlyMemory<Word> captures)
    {
        public CommandWords(IReadOnlyList<Word> source) : this(source, 0, ReadOnlyMemory<Word>.Empty) { }

        public int CapturedCount => captures.Length;
        public int Count => checked(captures.Length + (source.Count - start));

        public Word this[int index] => index < captures.Length
            ? captures.Span[index] : source[start + (index - captures.Length)];

        public CommandWords Slice(int index) => index < captures.Length
            ? new CommandWords(source, start, captures[index..])
            : new CommandWords(source, start + (index - captures.Length), ReadOnlyMemory<Word>.Empty);

        /// <summary>Attaches a new prefix after every previous capture has been consumed.</summary>
        public CommandWords WithCapturedPrefix(int consumed, Word[] prefix) =>
            new(source, start + (consumed - captures.Length), prefix);
    }

    /// <summary>Captures resolved command dispatch and completion semantics.</summary>
    private abstract record PreparedCommand(
        CommandCompletion Completion,
        RedirectionLifetime RedirectionLifetime)
    {
        public sealed record Terminal(
            EvaluationResult Result,
            ShellValue Message,
            PreparedHelp? Help,
            CommandWords Words,
            ShellValue LastReadArgument,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);

        public sealed record Query(
            IReadOnlyList<ShellValue> Arguments,
            ShellValue LastReadArgument,
            CommandQueryOutputMode OutputMode,
            ExecOptions? ExecutionOptions,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);

        public sealed record Executable(
            CommandWords Words,
            ShellValue CommandValue,
            string CommandName,
            CommandResolution Resolution,
            ExecOptions? ExecutionOptions,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);
    }

    private sealed record ExecOptions(ShellValue? ArgumentZero, bool Login, bool ClearEnvironment);

    private readonly record struct PreparedHelp(ShellValue Name, string CommandName, BuiltinKind Kind);

    private enum CommandCompletion
    {
        Continue,
        ExitEvaluation
    }

    private enum RedirectionLifetime
    {
        Command,
        Evaluation
    }

    internal sealed class RedirectionScope(
        Evaluation owner,
        bool active,
        IReadOnlyCollection<FileDescriptor> replacedHandles,
        IReadOnlyCollection<FileDescriptor> consumedSources)
    {
        private bool _active = active;

        internal bool IsActive => _active;
        internal bool HasRestorableMappings { get; init; }
        public bool InputOpenFailed { get; init; }

        public Task<OperationError?> PreserveAsync()
        {
            if (!_active)
                return Task.FromResult<OperationError?>(null);

            var preservedTable = owner._state.CurrentRedirectionTable;
            var openedDescriptors = owner._state.PreserveRedirectionTable(
                owner.Memory, owner._pendingDescriptorCleanup, owner._subshellBorrowedDescriptorScopes);
            _active = false;
            return FinishAsync(openedDescriptors, preservedTable, operationFailed: false);
        }

        public async ValueTask<OperationError?> CloseAsync(bool operationFailed)
        {
            if (!_active)
                return null;

            _active = false;
            return await FinishAsync(owner._state.PopRedirectionTable(), preservedTable: null, operationFailed).ConfigureAwait(false);
        }

        private Task<OperationError?> FinishAsync(IReadOnlyCollection<FileDescriptor> openedDescriptors,
            DescriptorTable? preservedTable, bool operationFailed)
        {
            var handles = owner._descriptorCloseBuffer;
            owner._state.TransferRedirectionDescriptors(openedDescriptors, owner.Memory, handles);
            if (preservedTable != null)
                foreach (var descriptor in replacedHandles)
                    if (!preservedTable.References(descriptor))
                        RetireUnreferenced(descriptor);
            foreach (var descriptor in consumedSources)
                RetireUnreferenced(descriptor);
            return owner.CloseDescriptorsAsync(handles, operationFailed);

            void RetireUnreferenced(FileDescriptor descriptor)
            {
                if (!owner._borrowedDescriptors.Contains(descriptor)
                    && !owner._subshellBorrowedDescriptorScopes.Any(scope => scope.Contains(descriptor))
                    && !owner._state.ReferencesDescriptor(descriptor)
                    && owner._state.TryTakeRedirectionDescriptor(descriptor, owner.Memory))
                {
                    handles.AddPrepared(descriptor);
                }
            }
        }

    }

    private void MarkDescriptorClosed(FileDescriptor descriptor) =>
        _pendingDescriptorCleanup.Remove(descriptor);

    private void ScheduleDescriptorCleanup(FileDescriptor descriptor)
    {
        if (_pendingDescriptorCleanup.Contains(descriptor))
            return;
        PrepareOwnedDescriptors(1);
        _pendingDescriptorCleanup.AddPrepared(descriptor);
    }

    private abstract record CommandResolution
    {
        private static readonly EvaluatorBuiltin BuiltinWrapper = new(BuiltinKind.Builtin);
        private static readonly EvaluatorBuiltin CommandWrapper = new(BuiltinKind.Command);
        private static readonly EvaluatorBuiltin ExecWrapper = new(BuiltinKind.Exec);

        public sealed record Function(FunctionBinding Binding) : CommandResolution;

        public sealed record EvaluatorBuiltin(BuiltinKind Kind) : CommandResolution;

        public static CommandResolution ForBuiltin(BuiltinKind kind) => kind switch
        {
            BuiltinKind.ApplyPatch => HostBuiltin.Instance,
            BuiltinKind.Builtin => BuiltinWrapper,
            BuiltinKind.Command => CommandWrapper,
            BuiltinKind.Exec => ExecWrapper,
            _ => new EvaluatorBuiltin(kind),
        };

        // Host-backed builtins dispatch through the host with in-process redirection scope.
        public sealed record HostBuiltin : CommandResolution
        {
            public static readonly HostBuiltin Instance = new();
        }

        public sealed record Host : CommandResolution
        {
            public static readonly Host Instance = new();
        }
    }

    /// <summary> Describes how a queried command name resolves without executing it. </summary>
    private abstract record CommandQueryResolution
    {
        /// <summary>The name resolves to a shell function. Descriptive output borrows its definition.</summary>
        public sealed record Function(FunctionDefinition? Definition) : CommandQueryResolution
        {
            public static readonly Function Classification = new(Definition: null);
        }

        /// <summary> The name resolves to an evaluator-owned or host-backed shell builtin. </summary>
        public sealed record Builtin : CommandQueryResolution
        {
            public static readonly Builtin Instance = new();
        }

        /// <summary> The name resolves to a reserved shell keyword. </summary>
        public sealed record Keyword : CommandQueryResolution
        {
            public static readonly Keyword Instance = new();
        }

        /// <summary>The host batch supplies this operand's resolution.</summary>
        public sealed record Host : CommandQueryResolution
        {
            public static readonly Host Instance = new();
        }

        /// <summary>The host batch confirms whether this hosted builtin is available.</summary>
        public sealed record HostBuiltin : CommandQueryResolution
        {
            public static readonly HostBuiltin Instance = new();
        }

        /// <summary> The name does not resolve to any function, builtin, keyword, or host command. </summary>
        public sealed record Missing : CommandQueryResolution
        {
            public static readonly Missing Instance = new();
        }
    }

    private enum CommandQueryOutputMode
    {
        CommandName,
        TypeKind,
        TypePath,
        TypeDescription
    }

    private enum CommandLookupMode
    {
        Normal,
        BypassFunctions,
        BuiltinOnly,
        HostOnly
    }

    private enum RootFlowHandling
    {
        Normalize,
        Preserve
    }

    /// <summary>Carries function identity and its active local-binding scope, including temporary commands.</summary>
    internal sealed class FunctionContext : IDisposable
    {
        public FunctionContext(ShellValue functionName, Variables.LocalBindings locals, FunctionContext? enclosing)
        {
            FunctionName = functionName;
            Enclosing = enclosing;
            Depth = (enclosing?.Depth ?? 0) + 1;
            Locals = locals;
            locals.TrackSavedMemory(enclosing?.Locals.SavedMemoryTracker ?? new EvaluationMemory.SavedLocalMemory());
        }

        public void Dispose() => Locals.RetireSavedMemory();

        public ShellValue FunctionName { get; }
        public FunctionContext? Enclosing { get; }
        public int Depth { get; }

        public Variables.LocalBindings Locals { get; }
        /// <summary>Borrows the inactive argument frame while the invocation and its children run.</summary>
        internal Variables.PositionalParametersScope? CallerPositionals { get; set; }
        public Variables.CommandBindings? PrefixBindings { get; init; }
        internal bool BindingsRestoredForExit { get; set; }
        public IReadOnlySet<BindingName> PrefixNames => PrefixBindings?.PrefixNames ?? NoPrefixNames;
        private static IReadOnlySet<BindingName> NoPrefixNames { get; } = new HashSet<BindingName>();

        /// <summary>Promotion candidates for this scope, filtered before its bindings are restored.</summary>
        public HashSet<BindingName> PromotedNames { get; } = new();

        /// <summary>The supported option state saved by the first local dash operand in this invocation.</summary>
        internal bool? SavedErrexit { get; private set; }

        internal FunctionContext InvocationOwner
        {
            get
            {
                var owner = this;
                // Prefix wrappers around eval and source share the active function invocation.
                while (owner.Locals.IsTemporaryPrefixScope && owner.Enclosing is { } enclosing)
                    owner = enclosing;
                return owner;
            }
        }

        internal void SaveErrexit(bool enabled) => InvocationOwner.SavedErrexit ??= enabled;

        internal bool HasOwnOptionSnapshot => InvocationOwner.SavedErrexit.HasValue;

        internal bool HasVisibleOptionSnapshot()
        {
            for (FunctionContext? frame = this; frame != null; frame = frame.Enclosing)
                if (frame.SavedErrexit.HasValue)
                    return true;
            return false;
        }

        public bool IsLocal(BindingName name)
        {
            // Each temporary local scope's parent is already the next function frame.
            for (FunctionContext? frame = this; frame != null; frame = frame.Enclosing)
                if (frame.Locals.OwnsDeclaration(name))
                    return true;
            return false;
        }

        internal long ForkMemoryBytes => checked(128 + Locals.ForkMemoryBytes + (PrefixBindings?.ForkMemoryBytes ?? 0));

        internal FunctionContext ForkFrame(
            Variables variables, FunctionContext? enclosing, Variables.CommandBindings? scriptPrefixes)
        {
            var callPrefixes = PrefixBindings?.Fork(variables);
            return new FunctionContext(FunctionName, Locals.Fork(variables, enclosing?.Locals, scriptPrefixes ?? callPrefixes), enclosing)
            {
                PrefixBindings = callPrefixes,
                CallerPositionals = CallerPositionals,
                SavedErrexit = SavedErrexit
            };
        }

    }

    /// <summary>Bindings hidden by eval/source prefixes, independently of function-local declaration rules.</summary>
    internal sealed record GlobalPrefixBindings(
        Variables.CommandBindings Bindings, int FunctionDepth, GlobalPrefixBindings? Enclosing)
    {
        public Variables Saved => Bindings.SavedVariables;
        public IReadOnlySet<BindingName> Names => Bindings.PrefixNames;
    }

    internal abstract record LocalNamerefAssignment
    {
        internal sealed record Write(VariableBinding Binding) : LocalNamerefAssignment;
        internal sealed record Circular(BindingName? LookupWarning) : LocalNamerefAssignment;
    }

    /// <summary>The nearest construct that permits and consumes return flow.</summary>
    internal enum ReturnTargetKind { None, Function, Source }

    /// <summary>An active source invocation above the recorded function depth.</summary>
    internal sealed record SourceCallerFrame(int FunctionDepth, SourceCallerFrame? Enclosing);

    /// <summary>Carries control-flow boundaries independently of function-local binding ownership.</summary>
    internal readonly struct EvaluationContext(int loopDepth, FunctionContext? functionContext, ReturnTargetKind returnTarget)
    {
        public int LoopDepth { get; init; } = loopDepth;

        public FunctionContext? FunctionContext { get; } = functionContext;
        public ReturnTargetKind ReturnTarget { get; init; } = returnTarget;
        public GlobalPrefixBindings? GlobalPrefixes { get; init; }
        public SourceCallerFrame? CallerSource { get; init; }
        public bool ErrexitIgnored { get; init; }
        public SourceMemory? SourceMemory { get; init; }
        internal CommandCoordinates Coordinates { get; init; }

        /// <summary>The source that supplies the reached command's syntax and coordinates.</summary>
        internal string? DiagnosticPath => SourceMemory?.DiagnosticPath;

        public EvaluationContext WithErrexitIgnored() => this with { ErrexitIgnored = true };

        public EvaluationContext WithLoopDepth(int loopDepth) => this with { LoopDepth = loopDepth };

        public EvaluationContext WithSourceMemory(SourceMemory? sourceMemory) => this with { SourceMemory = sourceMemory };

        internal EvaluationContext WithCoordinates(CommandCoordinates coordinates) => this with { Coordinates = coordinates };

        /// <summary>Preserves local declaration identity while isolating child bindings and restoration.</summary>
        internal (Variables, EvaluationContext) Fork(Variables source, EvaluationMemory memory,
            CancellationToken cancellationToken) =>
            Fork(source, memory, cancellationToken, borrowVisible: false);

        /// <summary>Isolates redirection writes while borrowing visible bindings until selected.</summary>
        internal (Variables, EvaluationContext) ForkForRedirections(Variables source, EvaluationMemory memory,
            CancellationToken cancellationToken) =>
            Fork(source, memory, cancellationToken, borrowVisible: true);

        private (Variables, EvaluationContext) Fork(Variables source, EvaluationMemory memory,
            CancellationToken cancellationToken, bool borrowVisible)
        {
            var bytes = borrowVisible ? 0 : source.EstimatedMemoryBytes;
            var functionDepth = FunctionContext?.Depth ?? 0;
            var prefixCount = 0;
            for (var prefix = GlobalPrefixes; prefix != null; prefix = prefix.Enclosing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                prefixCount++;
                bytes = checked(bytes + prefix.Bindings.ForkMemoryBytes + 48);
            }
            for (var frame = FunctionContext; frame != null; frame = frame.Enclosing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes = checked(bytes + frame.ForkMemoryBytes);
            }
            // The surrounding command owns these copies until the child and its cleanup finish.
            memory.Charge(checked(bytes + (functionDepth == 0 ? 0 : 32L + 8L * functionDepth)
                + (prefixCount == 0 ? 0 : 32L + 8L * prefixCount)));
            cancellationToken.ThrowIfCancellationRequested();
            var variables = borrowVisible
                ? global::Lokad.Bash.Evaluation.Variables.CreateCommandScope(source, memory, cancellationToken)
                : source.Clone();
            if (functionDepth == 0 && prefixCount == 0)
                return (variables, this);

            var frames = functionDepth == 0 ? [] : new FunctionContext[functionDepth];
            for (var frame = FunctionContext; frame != null; frame = frame.Enclosing)
                frames[frame.Depth - 1] = frame;
            var scriptPrefixes = prefixCount == 0 ? [] : new GlobalPrefixBindings[prefixCount];
            var index = 0;
            for (var prefix = GlobalPrefixes; prefix != null; prefix = prefix.Enclosing)
                scriptPrefixes[index++] = prefix;

            // Each eval/source prefix is pushed at the active function depth. Rebuild both chains
            // outermost-first, selecting the innermost script prefix at each matching depth.
            GlobalPrefixBindings? prefixes = null;
            FunctionContext? function = null;
            for (var depth = 0; depth <= functionDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (index > 0 && scriptPrefixes[index - 1].FunctionDepth == depth)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var prefix = scriptPrefixes[--index];
                    prefixes = new GlobalPrefixBindings(prefix.Bindings.Fork(variables), depth, prefixes);
                }
                if (depth != 0)
                    function = frames[depth - 1].ForkFrame(variables, function,
                        prefixes?.FunctionDepth == depth ? prefixes.Bindings : null);
            }

            // The source chain is immutable and its function-depth anchors survive reconstruction.
            return (variables, new EvaluationContext(LoopDepth, function, ReturnTarget)
            {
                GlobalPrefixes = prefixes,
                CallerSource = CallerSource,
                ErrexitIgnored = ErrexitIgnored,
                SourceMemory = SourceMemory,
                Coordinates = Coordinates
            });
        }

        public Variables GetGlobalBindingOwner(BindingName name, Variables active)
            => GetGlobalBindingOwner(name, active, commandBindings: null, outerBoundary: null);

        public Variables GetGlobalBindingOwner(BindingName name, Variables active, Variables.CommandBindings? commandBindings)
            => GetGlobalBindingOwner(name, active, commandBindings, outerBoundary: null);

        private Variables GetGlobalBindingOwner(BindingName name, Variables active,
            Variables.CommandBindings? commandBindings, FunctionContext? outerBoundary)
        {
            List<Variables.CommandBindings>? exposedPrefixes = null;
            return GetBindingOwner(name, active, DeclarationBindingScope.Global, prefixMemory: null, ref exposedPrefixes, outerBoundary, commandBindings);
        }

        public Variables GetVisibleBindingOwner(BindingName name, Variables active, EvaluationMemory memory,
            ref List<Variables.CommandBindings>? exposedPrefixes) =>
            GetBindingOwner(name, active, DeclarationBindingScope.Visible, memory, ref exposedPrefixes, outerBoundary: null, commandBindings: null);

        private Variables GetBindingOwner(BindingName name, Variables active, DeclarationBindingScope lookup,
            EvaluationMemory? prefixMemory, ref List<Variables.CommandBindings>? exposedPrefixes,
            FunctionContext? outerBoundary, Variables.CommandBindings? commandBindings)
        {
            var bindingName = StripArraySubscript(name);
            // The reached command's prefixes are the innermost restoration scope.
            var owner = commandBindings?.Contains(bindingName) == true ? commandBindings.SavedVariables : active;
            var prefix = GlobalPrefixes;
            var visible = lookup == DeclarationBindingScope.Visible;
            Variables.LocalBindings? localOwner = null;
            Variables? savedLocal = null;
            var declares = false;
            var blocksPromotion = false;
            // Interleave script prefixes with function frames in nesting order:
            // eval may be inside a function or may itself invoke a function.
            for (var function = FunctionContext; function != null && function != outerBoundary; function = function.Enclosing)
            {
                var locals = function.Locals;
                // Reuse the selected namespace until the enclosing walk passes it.
                if (localOwner == null)
                {
                    if (visible)
                    {
                        localOwner = locals.DeclarationOwner(bindingName);
                        declares = localOwner.OwnsDeclaration(bindingName);
                        blocksPromotion = declares && localOwner.BlocksPromotion(bindingName);
                    }
                    else
                    {
                        localOwner = function.InvocationOwner.Locals;
                        savedLocal = localOwner.SavedOwner(bindingName);
                    }
                }
                if (declares)
                {
                    if (function.PrefixNames.Contains(bindingName) && !blocksPromotion)
                    {
                        var inherited = function.InvocationOwner.Locals.SavedOwner(bindingName) ?? owner;
                        // Unset from a helper cancels this saved prefix export, not the caller local's promotion.
                        if (inherited.IsExported(bindingName))
                            return inherited;
                    }
                    if (!locals.IsTemporaryPrefixScope || blocksPromotion)
                        return owner;
                }
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    UnhidePrefix(ref exposedPrefixes);
                    if (visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                        return owner;
                }
                owner = savedLocal ?? owner;
                if (function.PrefixBindings is { } bindings && bindings.Contains(bindingName))
                {
                    owner = bindings.SavedVariables;
                    RecordExposedPrefix(bindings, ref exposedPrefixes);
                }
                if (visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                    return owner;
                if (ReferenceEquals(locals, localOwner))
                    localOwner = null;
            }
            while (prefix != null && (outerBoundary == null || prefix.FunctionDepth > outerBoundary.Depth))
            {
                UnhidePrefix(ref exposedPrefixes);
                if (visible && owner.HasBinding(bindingName))
                    return owner;
            }
            return owner;

            void UnhidePrefix(ref List<Variables.CommandBindings>? exposed)
            {
                if (prefix is not { } current)
                    return;
                if (current.Names.Contains(bindingName))
                {
                    owner = current.Saved;
                    RecordExposedPrefix(current.Bindings, ref exposed);
                }
                prefix = current.Enclosing;
            }

            void RecordExposedPrefix(Variables.CommandBindings bindings, ref List<Variables.CommandBindings>? exposed)
            {
                if (prefixMemory == null)
                    return;
                // Finish the admitted lookup before exposing or restoring any binding.
                var prefixes = exposed ??= [];
                prefixMemory.PrepareListCapacity(prefixes, 1);
                prefixes.Add(bindings);
            }
        }

        public bool TryResolveGlobalBinding(BindingName name, Variables active, out VariableBinding binding)
            => TryResolveGlobalBinding(name, active, active, prefixes: null, commandBindings: null, BindingResolution.Referent, out binding);

        /// <summary>A circular local lookup retries the original name in the global namespace.</summary>
        public bool TryResolveCircularBinding(BindingName name, Variables active, out VariableBinding binding)
        {
            binding = default;
            return !ReferenceEquals(GetGlobalBindingOwner(name, active), active)
                && TryResolveGlobalBinding(name, active, out binding);
        }

        /// <summary>Finds the writable binding beyond an unset chain reached through a local nameref.</summary>
        public bool TryResolveLocalNamerefAssignment(BindingName name, Variables active, EvaluationMemory memory, out LocalNamerefAssignment? target)
        {
            target = null;
            var source = FunctionContext;
            while (source != null && !source.Locals.OwnsDeclaration(name))
                source = source.Enclosing;
            if (source == null || !active.TryResolveBinding(name, selectOwner: null, out var binding))
                return false;

            var context = this;
            var crossedOuterLocal = false;
            for (var function = FunctionContext; function != null; function = function.Enclosing)
            {
                if (!function.Locals.OwnsDeclaration(binding.Name))
                    continue;
                crossedOuterLocal |= function.Depth <= source.Depth;
                var outerBoundary = function.Enclosing;
                var owner = CallerOwner(binding.Name);
                var lookupName = binding.Name;
                if (!owner.TryResolveBinding(lookupName, CallerOwner, out binding))
                {
                    target = new LocalNamerefAssignment.Circular(binding.Name == lookupName ? (BindingName?)lookupName : null);
                    return true;
                }
                if (!binding.Owner.TryGetNameref(binding.Name, out var next) || !next.IsEmpty)
                {
                    target = new LocalNamerefAssignment.Write(binding);
                    return true;
                }

                Variables CallerOwner(BindingName variable) => context.GetGlobalBindingOwner(variable, active, commandBindings: null, outerBoundary);
            }

            // Inner locals can hide the scalar created in the source frame. A target owned by
            // that frame or its caller instead leaves no write when every outer reference is unset.
            if (!crossedOuterLocal)
            {
                source.Locals.ShadowNamerefTarget(binding.Name, binding.Owner, memory);
                target = new LocalNamerefAssignment.Write(binding);
            }
            return true;
        }

        public bool TryResolveGlobalBinding(BindingName name, Variables persistent, Variables active,
            IReadOnlySet<BindingName>? prefixes, Variables.CommandBindings? commandBindings, BindingResolution resolution, out VariableBinding binding)
        {
            var context = this;
            var owner = SelectGlobalOwner(name);
            if (!owner.TryResolveBinding(name, SelectGlobalOwner, resolution, out binding))
                return owner.TryResolveBinding(name, SelectActiveOwner, resolution, out binding);

            // An unset global chain can still name an active referent; +n must not detach that chain.
            return !binding.Owner.TryGetNameref(binding.Name, out var target) || !target.IsEmpty
                || owner.TryResolveBinding(name, SelectActiveOwner, out binding);

            Variables SelectGlobalOwner(BindingName current) => context.GetGlobalBindingOwner(current, persistent, commandBindings);

            Variables SelectActiveOwner(BindingName current) => prefixes?.Contains(StripArraySubscript(current)) == true
                ? active : persistent;
        }

    }

    /// <summary> Enumerates loop kinds supported by evaluation. </summary>
    private enum LoopKind
    {
        While,
        Until
    }
}
