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
        FileDescriptor output)
    {
        var outer = scope.Effects;
        var descriptors = outer.CreateCommandSubstitutionDescriptorSetup(output);
        var (variables, context) = scope.ForkBindings();
        var line = Math.Max(1, scope.Context.Coordinates.Line);
        context = context.WithCoordinates(new CommandCoordinates(script.Locations, line - 1, line, line));
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
            outer._preparedSyntaxRoot);
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
                result = await EvaluateScriptAsync(new ScopedEvaluation(this, _vars, _initialContext), _rootFlowHandling).ConfigureAwait(false);
                if (_state.ForkKind == ShellForkKind.Subshell)
                    result = CompleteDiscardInChild(result);
                result = await RunExitTrapAsync(new ScopedEvaluation(this, _vars, _initialContext), result).ConfigureAwait(false);
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
        var redirectionScope = await OpenRedirectionScopeAsync(redirectedCommand.Redirections, scope,
            RedirectionPersistence.AllocationsAndMoves, RedirectionLifetime.Command, inputSubstitution: false)
            .ConfigureAwait(false);
        Exception? scopeFailure = null;
        var result = EvaluationResult.FromExitCode(0);
        OperationError? closeError = null;
        try
        {
            if (_state.TryConsumeExpansionError(out var redirectionExitCode))
            {
                result = await FinalizeResultAsync(EvaluationResult.FromExitCode(redirectionExitCode), negated: false).ConfigureAwait(false);
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
                InNestedCommandUnits = false,
                LoadedReaderIgnoresErrexit = false
            };
            if (redirections != null)
            {
                // The child owns this frame through EXIT and descriptor cleanup below.
                _ = await OpenRedirectionScopeAsync(redirections, childScope,
                    RedirectionPersistence.Allocations, RedirectionLifetime.Command, inputSubstitution: false)
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
                var stdIn = index > 0 ? pipes[index - 1].Read : GetStdInDescriptor();
                var stdOut = index < links.Length ? pipes[index].Write : GetStdOutDescriptor();
                var command = index == 0 ? first : links[index - 1].Right;
                if (index < links.Length && links[index].Operator == PipelineOperator.PipeAndError)
                    command = AppendPipeStandardError(command);
                var script = new ParsedScript([command]);
                var descriptors = new EvaluationDescriptorSetup(
                    bindings.WithStandardStreams(stdIn, stdOut, GetStdErrDescriptor()), borrowedDescriptors);
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
                    0 => [stdOut],
                    _ when index == links.Length => [stdIn],
                    _ => [stdIn, stdOut]
                };

                stage._retainCompletedMemory = true;
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

            var redirectionScope = await OpenRedirectionScopeAsync(simpleCommand.Redirections, scope,
                RedirectionPersistence.AllocationsAndMoves, RedirectionLifetime.Command, inputSubstitution: false)
                .ConfigureAwait(false);
            Exception? assignmentFailure = null;
            var assignmentResult = EvaluationResult.FromExitCode(exitCode);
            OperationError? assignmentCloseError = null;
            try
            {
                if (_state.TryConsumeExpansionError(out var assignmentRedirectionExitCode))
                    exitCode = assignmentRedirectionExitCode;
                else if (exitCode == 0)
                    exitCode = _state.LastCommandSubstitutionExitCode ?? 0;
                assignmentResult = await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), negated: false).ConfigureAwait(false);
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
            // Literal targets without allocation cannot write bindings. Isolate potential writes from the caller
            // without copying its entire variable store for ordinary file/descriptor redirections.
            if (MayWriteBindings(simpleCommand.Redirections))
            {
                redirectionExpansionScope = scope with
                {
                    Variables = global::Lokad.Bash.Evaluation.Variables.CreateCommandScope(scope.Variables, Memory, _cancellationToken),
                    ExpansionWriteTarget = null
                };
            }

            static bool MayWriteBindings(IReadOnlyList<Redirection> redirections)
            {
                foreach (var redirection in redirections)
                {
                    if (redirection.VariableName.Length != 0) return true;
                    foreach (var part in redirection.Target.Parts)
                    {
                        // Brace ranges can generate expansion syntax. Conservatively include them and
                        // read-only expansions; literal byte parts never expand.
                        if (part is CommandSubstitutionWordPart
                            || part is SourceWordPart source
                            && (source.Kind == WordPartKind.Unquoted && source.Source.AsSpan().Contains((byte)'{')
                                || source.Source.AsSpan().IndexOfAny((byte)'$', (byte)'`') >= 0))
                            return true;
                    }
                }
                return false;
            }
        }
        else if (simpleCommand.Redirections.Count > 0 && prefixNames != null)
        {
            redirectionExpansionScope = scope with
            {
                ExpansionWriteTarget = new ScopedEvaluation.ExpansionBindings(commandVars, prefixNames)
            };
        }
        var commandRedirections = await OpenRedirectionScopeAsync(
                simpleCommand.Redirections, redirectionExpansionScope,
                externalRedirections ? RedirectionPersistence.None : RedirectionPersistence.AllocationsAndMoves,
                preparation.RedirectionLifetime, inputSubstitution: false)
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
            if (_state.TryConsumeExpansionError(out var redirectionError))
            {
                // A failed redirection never enters exec, so it neither replaces the shell
                // nor suppresses timing. Fatal expansion errors still terminate via FinalizeResultAsync.
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(redirectionError), false)
                    .ConfigureAwait(false);
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
                        builtinScope with { Variables = commandVars, SelectorBindings = bindings }, false).ConfigureAwait(false),
                    BuiltinKind.Echo => await EvaluateBuiltinEchoAsync(echoContent, false).ConfigureAwait(false),
                    BuiltinKind.Type => await EvaluateBuiltinTypeAsync(byteOperands, commandVars, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Help => await AwaitWordExpansionAsync(EvaluateBuiltinHelpAsync(byteOperands, false)).ConfigureAwait(false),
                    BuiltinKind.Printf => await AwaitWordExpansionAsync(EvaluateBuiltinPrintfAsync(
                        byteOperands, commandVars, bindings, builtinScope.Context, false)).ConfigureAwait(false),
                    BuiltinKind.Read => await AwaitWordExpansionAsync(EvaluateBuiltinReadAsync(
                        byteOperands, new ScopedEvaluation(this, commandVars, builtinScope.Context), bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Mapfile => await AwaitWordExpansionAsync(EvaluateBuiltinMapfileAsync(
                        byteOperands, builtinScope with { Variables = commandVars }, executable.UsesCommandWrapper, false))
                        .ConfigureAwait(false),
                    BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local =>
                        await AwaitWordExpansionAsync(EvaluateBuiltinDeclarationAsync(
                            declaration ?? throw new InvalidOperationException("Declaration was not prepared."),
                            commandVars, bindings, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Set => await EvaluateBuiltinSetAsync(byteOperands, commandVars, false).ConfigureAwait(false),
                    BuiltinKind.Unset => await AwaitWordExpansionAsync(EvaluateBuiltinUnsetAsync(byteOperands, commandVars, builtinScope, bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Eval or BuiltinKind.Source => await EvaluateBuiltinScriptingAsync(evaluatorBuiltin.Kind,
                        commandName, byteOperands, builtinScope with { Variables = commandVars }, false).ConfigureAwait(false),
                    BuiltinKind.Trap => await AwaitWordExpansionAsync(EvaluateBuiltinTrapAsync(byteOperands)).ConfigureAwait(false),
                    BuiltinKind.Return => await AwaitWordExpansionAsync(
                        EvaluateBuiltinReturnAsync(byteOperands, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Let => await EvaluateBuiltinLetAsync(byteOperands, commandVars, bindings, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Getopts => await AwaitWordExpansionAsync(EvaluateBuiltinGetoptsAsync(
                        byteOperands, builtinScope with { Variables = commandVars }, bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Cd => await EvaluateBuiltinCdAsync(byteOperands, commandVars, bindings, builtinScope.Context, false)
                        .ConfigureAwait(false),
                    BuiltinKind.Pwd => await EvaluateBuiltinPwdAsync(executable.CommandValue, pwdOptions, false).ConfigureAwait(false),
                    BuiltinKind.Dirs => await EvaluateBuiltinDirsAsync(byteOperands, commandVars, false).ConfigureAwait(false),
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

            if (executable.Resolution is CommandResolution.Missing)
                return await FailPreparationAsync(127).ConfigureAwait(false);

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
                    ? [] : BuildExportedEnvironment(commandVars, scope.Context),
                GetStdInDescriptor(),
                GetStdOutDescriptor(),
                GetStdErrDescriptor(),
                ref inheritedDescriptors,
                CaptureDescriptorBindings().Bindings,
                functions);
            var execution = await ExecuteRunCommandAsync(commandInvocation).ConfigureAwait(false);
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
            };
            foreach (var assignment in simpleCommand.Assignments)
            {
                // Element prefixes are invalid identifiers, even before expanding their operands.
                if (assignment.Target.AsSpan().Contains((byte)'['))
                {
                    await ReportExpansionDiagnosticAsync(
                        assignment.Target.ToValue(), ShellValue.FromText("not a valid identifier")).ConfigureAwait(false);
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
                    await ReportExpansionDiagnosticAsync(assignment.Target.ToValue(),
                        ShellValue.FromText("readonly variable")).ConfigureAwait(false);
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
                            await ReportExpansionDiagnosticAsync(assignment.Target.ToValue(),
                                ShellValue.FromText("readonly variable")).ConfigureAwait(false);
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
                var length = 2L;
                foreach (var word in words)
                {
                    // The enclosing await reports any limit once, before active redirections unwind.
                    var expanded = await WordExpansion.ExpandWithoutBraceAsync(
                        word.Word, assignmentScope, allowWordSplitting: false, ExpansionPurpose.CompoundPrefix,
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
            PreparedDeclaration declaration, Variables commandVars, Variables.CommandBindings? bindings, ScopedEvaluation declarationScope, bool declarationNegated)
        {
            var options = declaration.Options;
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
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("can only be used in a function"))
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
            var listing = declaration.Operands.Count == 0;
            if (options.Functions && (!listing || options.Builtin != BuiltinKind.Local))
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(await EvaluateFunctionsAsync().ConfigureAwait(false)), declarationNegated).ConfigureAwait(false);
            if (listing || options.Print && options.Builtin is BuiltinKind.Declare or BuiltinKind.Local)
            {
                var listOptionSnapshot = listing && localBuiltinFrame?.HasOwnOptionSnapshot == true;
                var style = listing && options.Builtin == BuiltinKind.Declare && !options.Print
                    && options.ListingAttributes == DeclarationListingAttributes.None && options.CaseChange.Set == BindingAttributes.None
                    ? VariableDisplayStyle.Assignment : options.FunctionNamesOnly ? VariableDisplayStyle.DeclarationName : VariableDisplayStyle.Declaration;
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
                    var result = await WriteBuiltinOutputAsync(ShellValue.FromText(commandName), output.ToMemory()).ConfigureAwait(false);
                    if (!result.CanAcceptMore)
                    {
                        printStatus = result.IsError ? 1 : 0;
                        outputClosed = true;
                        break;
                    }
                }
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
                        && (!flags.HasFlag(DeclarationListingAttributes.Nameref) || variables.TryGetNameref(name, out _))
                        && (!flags.HasFlag(DeclarationListingAttributes.Exported) || variables.IsExported(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Integer) || variables.IsIntegerBinding(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Readonly) || variables.IsReadonlyBinding(name))
                        && (options.CaseChange.Set == BindingAttributes.None
                            || (variables.GetAssignmentAttributes(name) & options.CaseChange.Set) != 0);
                }
            }

            async Task<int> EvaluateFunctionsAsync()
            {
                const DeclarationListingAttributes ArrayFilters =
                    DeclarationListingAttributes.Indexed | DeclarationListingAttributes.Associative;
                var noOperands = declaration.Operands.Count == 0;
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
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("cannot use `-f' to make functions"))
                            .ConfigureAwait(false);
                        return 1;
                    }
                    if (!_state.Functions.TryGetValue(name, out var function))
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

                    using var output = new ByteMemoryBuilder(0, Memory.Budget);
                    var style = !options.FunctionNamesOnly
                        ? noOperands || options.Print ? FunctionDisplayStyle.DefinitionWithAttributes : FunctionDisplayStyle.Definition
                        : noOperands || options.Print ? FunctionDisplayStyle.Attributes : FunctionDisplayStyle.Name;
                    if (!function.TryAppend(output, style))
                    {
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("output limit exceeded")).ConfigureAwait(false);
                        return 1;
                    }
                    var result = await WriteBuiltinOutputAsync(ShellValue.FromText(commandName), output.ToMemory()).ConfigureAwait(false);
                    if (!result.CanAcceptMore)
                        return result.IsError ? 1 : functionStatus;
                }
                return functionStatus;

                IEnumerable<ShellValue> GetListedFunctionNames(MemoryCapacity listingMemory)
                {
                    var functions = _state.Functions;
                    var count = functions.Count;
                    if (count == 0 || filterNames && !functions.Values.Any(MatchesFilter))
                        return [];

                    // A host write can replace attributes on any name already in the listing.
                    Memory.Charge(listingMemory, MemoryCapacity.ArrayBackingBytes<ShellValue>(count));
                    var sorted = new ShellValue[count];
                    var index = 0;
                    foreach (var (name, function) in functions)
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
                if (declaration.Operands.Count == 0)
                {
                    // Keep the sorted snapshot through each awaited consumer and release it when enumeration ends.
                    using var listingMemory = new MemoryCapacity(Memory.Budget, 0);
                    foreach (var name in listNames(listingMemory))
                        yield return name;
                }
                else
                {
                    // Explicit operands remain literal and join through the shared budget/diagnostic path.
                    foreach (var operand in declaration.Operands)
                    {
                        var value = await ExpandWordValueAsync(operand.ToWord(), declarationScope).ConfigureAwait(false);
                        if (HasPendingExpansionError)
                            yield break;
                        yield return bindOperand(value);
                    }
                }
            }

            var setsNamedAttribute = options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly;
            var exitCode = 0;
            List<Variables.CommandBindings>? exposedPrefixes = null;
            foreach (var operand in declaration.Operands)
            {
                if (operand.Value == null && TrySaveLocalOptions(operand.Name))
                    continue;
                if (!TryParseDeclarationReference(ShellSource.FromBytes(operand.Name.Bytes), out var reference))
                {
                    var invalidOperand = await ExpandWordValueAsync(operand.ToWord(), declarationScope).ConfigureAwait(false);
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
                var global = (options.Global || arrayAttributeAssignment) && reference.Kind == VariableReferenceKind.Name;
                var local = options.IsLocal(declarationScope, options.Global);
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
                if (global)
                {
                    if (options.NamerefChange == DeclarationAttributeChange.Set)
                        targetVars = declarationScope.Context.GetGlobalBindingOwner(reference.Name, declarationScope.Variables);
                    else
                    {
                        if (!declarationScope.Context.TryResolveGlobalBinding(reference.Name, declarationScope.Variables, commandVars, prefixNames, resolution, out var binding))
                        {
                            circular = true;
                            binding = new VariableBinding(VariableReference.ForName(reference.Name), declarationScope.Context.GetGlobalBindingOwner(reference.Name, declarationScope.Variables));
                        }
                        if (!circular && arrayKind != ArrayAssignmentKind.Auto
                            && (resolution == BindingResolution.Referent
                                || binding.Owner.TryGetNameref(binding.Name, out var lastTarget) && lastTarget.IsEmpty))
                        {
                            var declaredOwner = declarationScope.Context.GetGlobalBindingOwner(reference.Name, declarationScope.Variables);
                            binding = declaredOwner.SelectArrayDeclarationBinding(reference.Name, binding);
                            recreateArrayReference = binding.Owner.TryGetNameref(binding.Name, out _);
                        }
                        reference = binding.Reference;
                        targetVars = binding.Owner;
                        if (reference.Kind != VariableReferenceKind.Name && bindings?.Contains(reference.Name) == true)
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
                if (options.Nameref && options.IntegerChange == DeclarationAttributeChange.Set)
                {
                    // The incompatible nameref request fails without binding its initializer.
                    if ((!local || declarationScope.Context.FunctionContext?.Locals.Declares(name) == true)
                        && targetVars.HasBinding(name))
                        targetVars.SetIntegerBinding(name, true);
                    exitCode = 1;
                    continue;
                }
                var existingLocal = declarationScope.Context.FunctionContext?.Locals.Declares(name) == true;
                if (resolution == BindingResolution.LastNameref && name != sourceName
                    && arrayKind == ArrayAssignmentKind.Auto && !operand.CompoundApplied
                    && targetVars.TryGetNameref(name, out var unsetTarget) && unsetTarget.IsEmpty)
                    continue; // +n cannot detach an indirectly reached unset reference or apply attributes to it.
                if (!operand.CompoundApplied && !local && options.NamerefChange != DeclarationAttributeChange.Set
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
                        await ReportExpansionDiagnosticAsync([ShellValue.FromText(commandName)],
                            [ShellValue.FromText("warning: "), GetDiagnosticName(name), ShellValue.FromText(": circular name reference")])
                            .ConfigureAwait(false);
                        selfNameref = true;
                    }
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
                if (operand.Value != null && reference.Kind != VariableReferenceKind.Name
                    && !local && options.NamerefChange != DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out _))
                {
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseAsync(name, "readonly variable").ConfigureAwait(false);
                        continue;
                    }
                    await ReportRemovingNamerefAsync(name).ConfigureAwait(false);
                }
                var readonlyBinding = options.NamerefChange != DeclarationAttributeChange.Unchanged
                    && reference.Kind == VariableReferenceKind.Name
                    ? targetVars.IsReadonlyBinding(name) : targetVars.IsReadonly(name);
                var declarationName = resolution == BindingResolution.LastNameref ? sourceName
                    : local && existingLocal && options.NamerefChange == DeclarationAttributeChange.Unchanged
                    && targetVars.TryResolveBinding(name, null, out var declaredBinding) ? declaredBinding.Name : name;
                if (!operand.CompoundApplied && (operand.Value != null || local && arrayKind == ArrayAssignmentKind.Auto || options.Nameref
                        || options.ReadonlyChange == DeclarationAttributeChange.Clear)
                    && (!local || existingLocal) && readonlyBinding)
                {
                    if (arrayAttributeAssignment)
                    {
                        await DiagnoseAsync(sourceName, "readonly variable").ConfigureAwait(false);
                        await ApplyNamedAttributesAsync(sourceName, assignmentPrefix).ConfigureAwait(false);
                    }
                    else if (operand.Value != null && setsNamedAttribute)
                    {
                        // These builtins apply attributes even when their assignment fails.
                        exitCode = 1;
                        await ReportExpansionDiagnosticAsync(GetDiagnosticName(targetVars.ResolveNamerefName(name)), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
                        await ApplyAttributesAsync(name, targetVars, global, local, assignmentExport).ConfigureAwait(false);
                    }
                    else
                        await DiagnoseAsync(declarationName, "readonly variable").ConfigureAwait(false);
                    continue;
                }
                var keepLocalNameref = false;
                if (local && existingLocal && options.NamerefChange == DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out var localTarget) && !localTarget.IsEmpty)
                {
                    // Test the immediate target: an existing intermediate nameref also detaches +n.
                    // A missing target instead becomes local, leaving this reference intact.
                    keepLocalNameref = !targetVars.HasBinding(BindingNameExpansion.FromValue(localTarget, Memory));
                    if (!keepLocalNameref)
                    {
                        var retainedTarget = localTarget;
                        if (!targetVars.TryRemoveNamerefAttribute(name))
                        {
                            await DiagnoseAsync(name, "readonly variable").ConfigureAwait(false);
                            continue;
                        }
                        targetVars.TrySetResolvedScalar(name, retainedTarget, Memory);
                    }
                }
                var convertedReference = false;
                if (options.Nameref && (!local || existingLocal)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name, "reference variable cannot be an array").ConfigureAwait(false);
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
                            await DiagnoseAsync(name, "readonly variable").ConfigureAwait(false);
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
                    if (!localFunction.Locals.TryDeclare(declarationScope.Variables, targetVars,
                            localOperand, prefixNames, arrayKind, options.Nameref, options.Inherit, options.CaseChange, Memory))
                    {
                        var message = targetVars.IsReadonly(name) ? "readonly variable"
                            : arrayKind != ArrayAssignmentKind.Auto
                                ? ArrayConversionMessage(arrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed)
                                : "invalid local declaration";
                        await DiagnoseAsync(declarationName, message).ConfigureAwait(false);
                        continue;
                    }
                    localReferenceAssigned = options.Nameref || initializingReference;
                }
                if ((options.Nameref || (operand.CompoundApplied || local) && options.NamerefChange == DeclarationAttributeChange.Set)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name, "reference variable cannot be an array").ConfigureAwait(false);
                    continue;
                }
                var resolvedName = targetVars.ResolveNamerefName(name);
                if (options.IndexedChange == DeclarationAttributeChange.Clear && targetVars.IndexedArrays.ContainsKey(resolvedName)
                    || options.AssociativeChange == DeclarationAttributeChange.Clear && targetVars.AssociativeArrays.ContainsKey(resolvedName))
                {
                    await DiagnoseAsync(name, "cannot destroy array variables in this way").ConfigureAwait(false);
                    continue;
                }
                // Global element references and +n keep attribute ownership separate from the active write owner.
                VariableBinding? referentAttributes = global && reference.Kind != VariableReferenceKind.Name
                    ? new VariableBinding(VariableReference.ForName(name), targetVars) : null;
                VariableReference? referentElement = !operand.CompoundApplied && operand.Value != null
                    && referentAttributes != null ? reference : null;
                if (!local && options.NamerefChange == DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out var finalTarget) && !finalTarget.IsEmpty && (global
                        ? declarationScope.Context.TryResolveGlobalBinding(name, declarationScope.Variables, out var referent)
                        : targetVars.TryResolveBinding(name, selectOwner: null, out referent)))
                {
                    referentAttributes = referent with { Reference = VariableReference.ForName(referent.Name) };
                    if (!operand.CompoundApplied && operand.Value != null && reference.Kind == VariableReferenceKind.Name
                        && referent.Reference.Kind != VariableReferenceKind.Name)
                        referentElement = referent.Reference;
                }
                if ((!operand.CompoundApplied || convertedReference) && !local && arrayKind != ArrayAssignmentKind.Auto)
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
                if (!setsNamedAttribute && options.IndexedChange == DeclarationAttributeChange.Set
                    && options.AssociativeChange == DeclarationAttributeChange.Set)
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
                    && TrySelectExpandedInitializer(name, reference, targetVars, arrayKind, out var initializerTarget, out var initializerKind))
                {
                    // The declared attributes are visible to reads during item expansion.
                    var initializeReadonly = options.Readonly && !initializerTarget.Owner.IsReadonlyBinding(initializerTarget.Name);
                    SetAttributes(initializerTarget.Name, initializerTarget.Owner, assignmentExport);
                    var listSource = ShellSource.FromBytes(expandedInitializer.Bytes[1..^1]);
                    using var syntaxMemory = new MemoryCapacity(Memory.Budget, 0);
                    if (!Parser.TryParseCompoundInitializer(listSource, syntaxMemory, out var items, out var error))
                    {
                        var status = _state.ErrexitEnabled && !executionScope.LoadedReaderIgnoresErrexit ? 2 : 1;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName),
                            ShellValue.FromText("syntax error in array initializer: " + error.Message)).ConfigureAwait(false);
                        _state.LastExitCode = status;
                        PublishExpansionPipelineStatus(status, executionScope);
                        return status == 2 ? EvaluationResult.Exit(status) : EvaluationResult.DiscardCompoundInitializerUnit();
                    }
                    if (!await AwaitWordExpansionAsync(ExpandAndApplyCompoundArrayItemsAsync(initializerTarget, initializerKind,
                            operand.Operator, items, AssignmentContext.Declaration, executionScope, sourceName,
                            ShellValue.FromText(commandName), initializeReadonly))
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
                    if (!await AwaitWordExpansionAsync(AssignReferencedElementAsync(
                            elementReference, elementAttributes, elementValue, operand.Operator, global, assignmentExport)).ConfigureAwait(false))
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
                            // Bash writes element zero through the active binding here, even if that binding is readonly.
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
                            if (prefixNames != null)
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
                                ? declarationScope.Context.TryResolveGlobalBinding(name, declarationScope.Variables, out var binding)
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
                                declarationWrite, local && reference.Kind != VariableReferenceKind.Name ? sourceName : declarationName).ConfigureAwait(false))
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
                        await DiagnoseAsync(declarationName, "readonly variable").ConfigureAwait(false);
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

            bool TrySelectExpandedInitializer(BindingName name, VariableReference reference, Variables variables, ArrayAssignmentKind arrayKind,
                out VariableBinding binding, out ArrayKind kind)
            {
                kind = default;
                binding = default;
                if (arrayKind == ArrayAssignmentKind.Auto
                    && (reference.Kind != VariableReferenceKind.Name || options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly))
                    return false;
                if (!variables.TryResolveBinding(name, selectOwner: null, out binding)
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
                bool global, DeclarationAttributeChange exportChange)
            {
                // Attributes select the declaration binding; element writes use the active caller array.
                if (global && bindings?.Contains(attributes.Name) == true)
                    attributes = attributes with { Owner = bindings.SavedVariables };
                if (attributes.Owner.IsReadonly(attributes.Name))
                {
                    await DiagnoseAsync(attributes.Name, "readonly variable").ConfigureAwait(false);
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
                // Prefixes affect selector arithmetic and negative-index bounds, not the stored value used by +=.
                var target = await WordExpansion.ResolveDeclarationReferenceAsync(
                    reference, selectionScope, variables, commandVars).ConfigureAwait(false);
                if (HasPendingExpansionError)
                    return false;
                if (target == null)
                    exitCode = 1;
                else if (!await TryAssignDeclarationValueAsync(target, reference, declarationScope, variables,
                    value, operation, WordExpansion.DeclarationWrite.Ordinary, reference.Name).ConfigureAwait(false))
                    exitCode = 1;
                return true;
            }

            async Task<bool> TryAssignDeclarationValueAsync(
                WordExpansion.ResolvedVariableReference target, VariableReference reference, ScopedEvaluation writeScope, Variables destination,
                ShellValue value, AssignmentOperator operation, WordExpansion.DeclarationWrite declarationWrite, BindingName diagnosticName)
            {
                if (declarationWrite != WordExpansion.DeclarationWrite.ActiveGlobalIndexed && destination.IsReadonly(target.Name))
                {
                    if (reference.Kind != VariableReferenceKind.Name)
                    {
                        // A newly applied -r rejects the element write but leaves the declaration successful.
                        await ReportExpansionDiagnosticAsync(GetDiagnosticName(diagnosticName), ShellValue.FromText("readonly variable"))
                            .ConfigureAwait(false);
                        return true;
                    }
                    await DiagnoseAsync(diagnosticName, "readonly variable").ConfigureAwait(false);
                    return false;
                }
                if (!target.Name.IsEmpty && options.IntegerChange != DeclarationAttributeChange.Unchanged)
                    destination.SetIntegerBinding(target.Name, options.IntegerChange == DeclarationAttributeChange.Set);
                destination.SetCaseAttributes(target.Name, options.CaseChange);
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
                            await ReportExpansionDiagnosticAsync(GetDiagnosticName(resolved), ShellValue.FromText("readonly variable"))
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
                variables.SetCaseAttributes(name, options.CaseChange);
                if (options.IntegerChange != DeclarationAttributeChange.Unchanged)
                    variables.SetIntegerBinding(name, options.IntegerChange == DeclarationAttributeChange.Set);
                if (exportChange != DeclarationAttributeChange.Unchanged)
                    variables.SetExported(name, exportChange == DeclarationAttributeChange.Set);
                if (options.Readonly) variables.SetReadonlyBinding(name);
            }

            Task ReportDeclarationOperandAsync(ShellValue subject, string message) =>
                ReportExpansionDiagnosticAsync([ShellValue.FromText(commandName)], [subject, ShellValue.FromText(": " + message)]);

            Task DiagnoseAsync(BindingName subject, string message)
            {
                exitCode = 1;
                return ReportDeclarationOperandAsync(GetDiagnosticName(subject), message);
            }

            Task DiagnoseQuotedAsync(ShellValue subject, string message)
            {
                exitCode = 1;
                return WriteQuotedOperandDiagnosticAsync(ShellValue.FromText(commandName), subject, message,
                    _builtinDiagnostics, ClosedStderrHandling.AttemptWrite);
            }
        }
        async Task<bool> ApplyAssignmentAsync(AssignmentWord assignment)
        {
            if (!assignment.IsCompoundArrayAssignment)
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
                await ReportFatalExpansionAsync(assignment.Target.ToValue(), ShellValue.FromText("cannot assign list to array member"), ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                return false;
            }
            if (!name.IsIdentifier || assignment.ArrayValues is not { } items)
                return false;
            if (scope.Variables.IsReadonly(name))
            {
                await ReportFatalExpansionAsync(assignment.Target.ToValue(), ShellValue.FromText("readonly variable"), ExpansionFailureKind.AssignmentTarget).ConfigureAwait(false);
                return false;
            }

            var kind = scope.Variables.AssociativeArrays.ContainsKey(name) ? ArrayKind.Associative : ArrayKind.Indexed;
            return await ExpandAndApplyCompoundArrayItemsAsync(
                new VariableBinding(VariableReference.ForName(name), scope.Variables), kind, assignment.Operator, items,
                AssignmentContext.Direct, scope, compoundTarget.Name, ShellValue.Empty, initializeReadonly: false).ConfigureAwait(false);
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

    private CommandResolution ResolveCommandName(ShellValue commandName, CommandLookupMode lookupMode)
    {
        if (FindCommandFunction(commandName, lookupMode) is { } function)
            return new CommandResolution.Function(function);

        if (FindCommandBuiltin(commandName, lookupMode) is { } builtin)
        {
            return builtin == BuiltinKind.ApplyPatch
                ? new CommandResolution.HostBuiltin(builtin)
                : new CommandResolution.EvaluatorBuiltin(builtin);
        }

        return lookupMode == CommandLookupMode.BuiltinOnly
            ? CommandResolution.Missing.Instance
            : CommandResolution.Host.Instance;
    }

    private FunctionBinding? FindCommandFunction(ShellValue name, CommandLookupMode lookupMode) =>
        lookupMode == CommandLookupMode.Normal && _state.Functions.TryGetValue(name, out var function)
            ? function : null;

    private static BuiltinKind? FindCommandBuiltin(ShellValue name, CommandLookupMode lookupMode) =>
        lookupMode != CommandLookupMode.HostOnly && BuiltinClassification.TryResolve(name.Bytes.Span, out var builtin)
            ? builtin : null;

    /// <summary>
    /// Opens a lexical redirection scope and records ownership of every descriptor it creates.
    /// The command's execution policy determines which bindings can reach the enclosing scope.
    /// </summary>
    private async Task<RedirectionScope> OpenRedirectionScopeAsync(
        IReadOnlyList<Redirection> redirections,
        ScopedEvaluation scope,
        RedirectionPersistence persistence,
        RedirectionLifetime lifetime,
        bool inputSubstitution)
    {
        if (redirections.Count == 0)
            return new RedirectionScope(this, active: false, [], []);

        if (_state.CurrentRedirectionTable == null)
            throw new InvalidOperationException("Redirection setup requires an enclosing descriptor table.");
        var evaluation = new RedirectionEvaluation(redirections, CaptureDescriptorBindings().Fork(), inputSubstitution,
            preserveBindings: lifetime == RedirectionLifetime.Evaluation);
        // Expose the table as setup advances, so later target expansions and their substitutions
        // inherit earlier redirections. Descriptor ownership is finalized after setup succeeds.
        _state.PushRedirectionTable(evaluation.DescriptorTable, evaluation.OpenedDescriptors, evaluation.TouchedDescriptors, Memory);
        try
        {
            await evaluation.EvaluateAsync(scope, persistence).ConfigureAwait(false);
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
        var bindings = CaptureDescriptorBindings().WithStandardStreams(GetStdInDescriptor(), stdOut, GetStdErrDescriptor());

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
    internal async Task WriteOperationDiagnosticAsync(FileDescriptor descriptor, string prefix, string? path, OperationError error)
    {
        if (await TryWriteOperationDiagnosticAsync(descriptor, ShellValue.Empty, prefix, path, error).ConfigureAwait(false) == null)
        {
            // The operation already failed. Keep the bounded message on its status and cleanup path.
            await ExecuteWriteAsync(descriptor, ShellMemoryRefusalDiagnostic).ConfigureAwait(false);
        }
    }

    /// <summary>Formats directly into the admitted output buffer. A null result means admission refused.</summary>
    private async Task<DescriptorWriteResult?> TryWriteOperationDiagnosticAsync(
        FileDescriptor descriptor, ShellValue name, string prefix, string? path, OperationError error)
    {
        var encoding = Utf8Text.StrictUtf8;
        var prefixLength = encoding.GetByteCount(prefix);
        var pathLength = path is null ? 0 : encoding.GetByteCount(path);
        var detailLength = encoding.GetByteCount(error.Message.AsSpan());
        var nameLength = name.IsEmpty ? 0 : (long)name.Bytes.Length + 2;
        var length = nameLength + prefixLength + pathLength + (path is null ? 0 : 2) + detailLength;
        if (!TryReserveDiagnostic(length, out var reservation))
            return null;

        using var memory = reservation;
        using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
        if (!name.IsEmpty)
            diagnostic.Append(name.Bytes).Append(": "u8);
        encoding.GetBytes(prefix.AsSpan(), diagnostic.AppendSpan(prefixLength));
        if (path != null)
        {
            encoding.GetBytes(path.AsSpan(), diagnostic.AppendSpan(pathLength));
            diagnostic.Append(": "u8);
        }
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
        Variables variables, EvaluationContext context)
    {
        using var selectionMemory = new MemoryCapacity(Memory.Budget, 0);
        return global::Lokad.Bash.Evaluation.Variables.BuildExportedEnvironment(
            ExportBindings(), Memory, selectionMemory, _cancellationToken);

        IEnumerable<VariableBinding> ExportBindings()
        {
            foreach (var binding in variables.ExportedBindings)
                yield return binding;
            if (context.FunctionContext == null && context.GlobalPrefixes == null)
                yield break;

            var functionPrefixes = new HashSet<BindingName>();
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
            {
                foreach (var name in function.PrefixNames)
                {
                    CheckCancellation();
                    if (functionPrefixes.Contains(name))
                        continue;
                    selectionMemory.Grow(global::Lokad.Bash.Evaluation.Variables.BindingNameSelectionBytes);
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
                        || visibleOwners.GetValueOrDefault(binding.Name, variables).IsExported(binding.Name))
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
                        selectionMemory.Grow(192); // Dictionary slots also hold a reference to the owner.
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
    internal void BindPersistentDescriptor(LogicalDescriptor descriptor, FileDescriptor handle) =>
        _state.SetPersistentDescriptor(descriptor, handle, Memory, OwnsPersistentHandle(handle));

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

    /// <summary>Reports the first fatal expansion through the active stderr mapping without decoding message bytes.</summary>
    internal Task ReportFatalExpansionAsync(ShellValue name, ShellValue message, ExpansionFailureKind kind) =>
        ReportFatalExpansionAsync([name], [message], kind);

    internal Task ReportFatalExpansionAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message, ExpansionFailureKind kind)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        if (kind == ExpansionFailureKind.IntegerAssignment && _state.ForkKind is null or ShellForkKind.Subshell
            && _state.LastExitCode is { } previousStatus && previousStatus != 0)
            SetFatalExpansionError(new ExpansionFailure(kind, previousStatus));
        else
            SetFatalExpansionError(kind);
        return ReportExpansionDiagnosticAsync(name, message);
    }

    internal void SetFatalExpansionError(ExpansionFailureKind kind)
        => SetFatalExpansionError(new ExpansionFailure(kind, kind == ExpansionFailureKind.NamerefDefault ? 2 : 1));

    private void SetFatalExpansionError(ExpansionFailure failure)
    {
        _state.FatalExpansion ??= failure;
        _state.SetExpansionError(_state.FatalExpansion.Value.ExitCode);
    }

    internal Task ReportUnitExpansionFailureAsync(ReadOnlySpan<ShellValue> subject, ReadOnlySpan<ShellValue> message, ScopedEvaluation scope,
        ExpansionFailureKind kind)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        // Publish status while the reached lexical scope is still active.
        SetFatalExpansionError(kind);
        _state.LastExitCode = 1;
        PublishExpansionPipelineStatus(1, scope);
        return ReportExpansionDiagnosticAsync(subject, message);
    }

    private void PublishAssignmentTargetFailure(ScopedEvaluation scope)
    {
        if (_state.FatalExpansion is not { Kind: ExpansionFailureKind.AssignmentTarget } failure)
            return;

        var canResume = !_state.ErrexitEnabled;
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
            // A prefix RHS writes through to the caller even though its temporary bindings go away.
            var statusScope = ReferenceEquals(scope.ExpansionWriteTarget?.Variables, _vars)
                ? scope with { Variables = _vars } : scope;
            PublishExpansionPipelineStatus(exitCode, statusScope);
        }
        return ReportExpansionDiagnosticAsync(subject, message);
    }

    private void PublishExpansionPipelineStatus(int exitCode, ScopedEvaluation scope)
    {
        PreparePipelineStatusStorage(1);
        scope.Variables.SetPipelineStatuses([exitCode], scope.Context, _cancellationToken);
    }

    private bool PipelineInheritsLoadedUnitStatus => _state.ForkKind == ShellForkKind.PipelineSimpleCommand
        && _pipelineUsesLoadedUnitStatus;

    internal Task ReportCommandSubstitutionSyntaxAsync(SubstitutionSyntaxFailure failure)
    {
        if (_state.FatalExpansion.HasValue)
            return Task.CompletedTask;

        var exitCode = _state.ErrexitEnabled || failure is SubstitutionSyntaxFailure.NestedSyntax
            or SubstitutionSyntaxFailure.NestedHereDocumentSeparator ? 2 : 1;
        SetFatalExpansionError(new ExpansionFailure(ExpansionFailureKind.CommandSubstitutionSyntax, exitCode));
        _state.LastExitCode = exitCode;
        var message = failure is SubstitutionSyntaxFailure.HereDocumentSeparator or SubstitutionSyntaxFailure.NestedHereDocumentSeparator
            ? "Unexpected token after a here-document in command substitution."
            : "Unexpected ')' after a pipeline prefix in command substitution.";
        return ReportSyntaxErrorAsync(new SyntaxError(message, 0));
    }

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

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(ReadOnlySpan<ShellValue> name, ReadOnlySpan<ShellValue> message,
        BuiltinDiagnosticScope? scope, ClosedStderrHandling closedStderr)
    {
        if (scope is { Result.CanAcceptMore: false })
            return Task.FromResult(scope.Result);

        var descriptor = GetStdErrDescriptor();
        if (closedStderr == ClosedStderrHandling.Discard && descriptor.IsClosed)
        {
            CheckCancellation();
            return Task.FromResult(DescriptorWriteResult.Closed);
        }

        return WriteDiagnosticAsync(descriptor, name, message, scope);
    }

    /// <summary>Writes one diagnostic to the descriptor reached during redirection.</summary>
    internal Task WriteRedirectionDiagnosticAsync(FileDescriptor descriptor, ShellValue name, ShellValue message) =>
        WriteDiagnosticAsync(descriptor, [name], [message], scope: null);

    private Task<DescriptorWriteResult> WriteDiagnosticAsync(FileDescriptor descriptor, ReadOnlySpan<ShellValue> name,
        ReadOnlySpan<ShellValue> message, BuiltinDiagnosticScope? scope)
    {
        var length = (long)DiagnosticPunctuationBytes;
        foreach (var part in name)
            length += part.Bytes.Length;
        foreach (var part in message)
            length += part.Bytes.Length;
        if (!TryReserveDiagnostic(length, out var reservation))
            throw new MemoryBudgetExceededException();

        try
        {
            using var diagnostic = ByteMemoryBuilder.CreateUnaccounted((int)length);
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
        var (open, openedPath) = lookupMemory != null && RawShellPath.TryParse(path, out var fileName)
            ? await SearchSourcePathAsync(fileName, searchPath, scope, lookupMemory).ConfigureAwait(false)
            : (await ExecuteOpenFileAsync(path, FileOpenMode.Read).ConfigureAwait(false), path);
        var descriptor = open.FileDescriptor;
        if (!descriptor.HasValue)
        {
            if (open.Error is { Message.Length: > 0 } error)
                await ReportSourceOpenFailureAsync(commandName, openedPath, error).ConfigureAwait(false);
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

            openedPath = path;
            lookupMemory?.Dispose();
            // The active command owns the frame until the sourced invocation and its children settle.
            Memory.Charge(48);
            var sourceScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth, scope.Context.FunctionContext, ReturnTargetKind.Source)
                {
                    GlobalPrefixes = scope.Context.GlobalPrefixes,
                    ErrexitIgnored = scope.Context.ErrexitIgnored,
                    CallerSource = new SourceCallerFrame(scope.Context.FunctionContext?.Depth ?? 0, scope.Context.CallerSource),
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
    /// Emits a run-command operation and returns its execution result.
    /// </summary>
    private async Task<CommandExecutionResult> ExecuteRunCommandAsync(CommandInvocation command)
    {
        CheckCancellation();
        // The invocation borrows arguments, exports and descriptors until the host settles.
        var result = await RunCommand.AsTask(PublishOperation, command).ConfigureAwait(false);
        CheckCancellation();
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

    /// <summary>Reads the single input redirection of a file substitution in the caller's context.</summary>
    internal async Task<CommandSubstitutionResult> ReadInputSubstitutionAsync(
        IReadOnlyList<Redirection> redirections, ScopedEvaluation scope)
    {
        using var outputMemory = Memory.Budget.CreateScope();
        var redirectionScope = await OpenRedirectionScopeAsync(redirections, scope,
            RedirectionPersistence.None, RedirectionLifetime.Command, inputSubstitution: true)
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
                if (redirectionScope.InputOpenFailed && _state.ErrexitEnabled)
                    SetFatalExpansionError(ExpansionFailureKind.InputSubstitution);
            }
            else
            {
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
            bool UsesCommandWrapper,
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
        public sealed record Function(FunctionBinding Binding) : CommandResolution;

        public sealed record EvaluatorBuiltin(BuiltinKind Kind) : CommandResolution;

        public sealed record HostBuiltin(BuiltinKind Kind) : CommandResolution;

        public sealed record Host : CommandResolution
        {
            public static readonly Host Instance = new();
        }

        public sealed record Missing : CommandResolution
        {
            public static readonly Missing Instance = new();
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
        public Variables.CommandBindings? PrefixBindings { get; init; }
        internal bool BindingsRestoredForExit { get; set; }
        public IReadOnlySet<BindingName> PrefixNames => PrefixBindings?.PrefixNames ?? NoPrefixNames;
        private static IReadOnlySet<BindingName> NoPrefixNames { get; } = new HashSet<BindingName>();

        /// <summary>Promotion candidates for this scope, filtered before its bindings are restored.</summary>
        public HashSet<BindingName> PromotedNames { get; } = new();

        /// <summary>The supported option state saved by the first local dash operand in this invocation.</summary>
        internal bool? SavedErrexit { get; private set; }

        private FunctionContext OptionOwner
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

        internal void SaveErrexit(bool enabled) => OptionOwner.SavedErrexit ??= enabled;

        internal bool HasOwnOptionSnapshot => OptionOwner.SavedErrexit.HasValue;

        internal bool HasVisibleOptionSnapshot()
        {
            for (FunctionContext? frame = this; frame != null; frame = frame.Enclosing)
                if (frame.SavedErrexit.HasValue)
                    return true;
            return false;
        }

        public bool IsLocal(BindingName name) => Locals.Declares(name) || Enclosing?.IsLocal(name) == true;

        internal long ForkMemoryBytes => checked(128 + Locals.ForkMemoryBytes + (PrefixBindings?.ForkMemoryBytes ?? 0));

        internal FunctionContext ForkFrame(
            Variables variables, FunctionContext? enclosing, Variables.CommandBindings? scriptPrefixes)
        {
            var callPrefixes = PrefixBindings?.Fork(variables);
            return new FunctionContext(FunctionName, Locals.Fork(variables, enclosing?.Locals, scriptPrefixes ?? callPrefixes), enclosing)
            {
                PrefixBindings = callPrefixes,
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
        public ReturnTargetKind ReturnTarget { get; } = returnTarget;
        public GlobalPrefixBindings? GlobalPrefixes { get; init; }
        public SourceCallerFrame? CallerSource { get; init; }
        public bool ErrexitIgnored { get; init; }
        public SourceMemory? SourceMemory { get; init; }
        internal CommandCoordinates Coordinates { get; init; }

        public EvaluationContext WithErrexitIgnored() => this with { ErrexitIgnored = true };

        public EvaluationContext WithLoopDepth(int loopDepth) => this with { LoopDepth = loopDepth };

        public EvaluationContext WithSourceMemory(SourceMemory? sourceMemory) => this with { SourceMemory = sourceMemory };

        internal EvaluationContext WithCoordinates(CommandCoordinates coordinates) => this with { Coordinates = coordinates };

        /// <summary>Preserves local declaration identity while isolating child bindings and restoration.</summary>
        internal (Variables, EvaluationContext) Fork(Variables source, EvaluationMemory memory, CancellationToken cancellationToken)
        {
            var bytes = source.EstimatedMemoryBytes;
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
            var variables = source.Clone();
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
            => GetGlobalBindingOwner(name, active, outerBoundary: null);

        private Variables GetGlobalBindingOwner(BindingName name, Variables active, FunctionContext? outerBoundary)
        {
            List<Variables.CommandBindings>? exposedPrefixes = null;
            return GetBindingOwner(name, active, DeclarationBindingScope.Global, prefixMemory: null, ref exposedPrefixes, outerBoundary);
        }

        public Variables GetVisibleBindingOwner(BindingName name, Variables active, EvaluationMemory memory,
            ref List<Variables.CommandBindings>? exposedPrefixes) =>
            GetBindingOwner(name, active, DeclarationBindingScope.Visible, memory, ref exposedPrefixes, outerBoundary: null);

        private Variables GetBindingOwner(BindingName name, Variables active, DeclarationBindingScope lookup,
            EvaluationMemory? prefixMemory, ref List<Variables.CommandBindings>? exposedPrefixes, FunctionContext? outerBoundary)
        {
            var bindingName = StripArraySubscript(name);
            var owner = active;
            var prefix = GlobalPrefixes;
            // Interleave script prefixes with function frames in nesting order:
            // eval may be inside a function or may itself invoke a function.
            for (var function = FunctionContext; function != null && function != outerBoundary; function = function.Enclosing)
            {
                if (lookup == DeclarationBindingScope.Visible && function.Locals.Declares(bindingName))
                {
                    if (function.PrefixNames.Contains(bindingName) && !function.Locals.BlocksPromotion(bindingName))
                    {
                        var inherited = function.Locals.HiddenOwner(bindingName, owner);
                        // Unset from a helper cancels this saved prefix export, not the caller local's promotion.
                        if (inherited.IsExported(bindingName))
                            return inherited;
                    }
                    if (function.Locals.SavedBindings != null || function.Locals.BlocksPromotion(bindingName))
                        return owner;
                }
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    UnhidePrefix(ref exposedPrefixes);
                    if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                        return owner;
                }
                if (lookup == DeclarationBindingScope.Global)
                    owner = function.Locals.HiddenOwner(bindingName, owner);
                if (function.PrefixBindings is { } bindings && bindings.Contains(bindingName))
                {
                    owner = bindings.SavedVariables;
                    RecordExposedPrefix(bindings, ref exposedPrefixes);
                }
                if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                    return owner;
            }
            while (prefix != null && (outerBoundary == null || prefix.FunctionDepth > outerBoundary.Depth))
            {
                UnhidePrefix(ref exposedPrefixes);
                if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName))
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
            => TryResolveGlobalBinding(name, active, active, prefixes: null, BindingResolution.Referent, out binding);

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

                Variables CallerOwner(BindingName variable) => context.GetGlobalBindingOwner(variable, active, outerBoundary);
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
            IReadOnlySet<BindingName>? prefixes, BindingResolution resolution, out VariableBinding binding)
        {
            var context = this;
            var owner = GetGlobalBindingOwner(name, persistent);
            if (!owner.TryResolveBinding(name, current => context.GetGlobalBindingOwner(current, persistent), resolution, out binding))
                return owner.TryResolveBinding(name, SelectActiveOwner, resolution, out binding);

            // An unset global chain can still name an active referent; +n must not detach that chain.
            return !binding.Owner.TryGetNameref(binding.Name, out var target) || !target.IsEmpty
                || owner.TryResolveBinding(name, SelectActiveOwner, out binding);

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
