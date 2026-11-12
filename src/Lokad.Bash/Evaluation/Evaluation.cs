using System;
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
    /// <summary>
    /// Indicates whether flow escaping the root script should be treated as invalid.
    /// </summary>
    private readonly RootFlowHandling _rootFlowHandling;
    // Pipelines retain completed evaluators until all stages and descriptor cleanup finish.
    private bool _retainCompletedMemory;
    /// <summary>
    /// Tracks descriptors inherited by a nested evaluator and therefore not owned by it.
    /// </summary>
    private readonly IReadOnlyList<FileDescriptor> _borrowedDescriptors;
    /// <summary>
    /// Tracks descriptors borrowed temporarily by an in-process subshell.
    /// </summary>
    private readonly Stack<HashSet<FileDescriptor>> _subshellBorrowedDescriptorScopes = new();
    /// <summary> Retains owned descriptors until their terminal close completes. </summary>
    private readonly List<FileDescriptor> _pendingDescriptorCleanup = new();

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
            [],
            new MemoryGovernor(long.MaxValue))
    {
    }

    internal Evaluation(
        ParsedScript script,
        EvaluationDescriptorSetup descriptorSetup,
        Action<EvaluationOperation?>? publishNextStep,
        Action? onComplete,
        EvaluationOptions options,
        IReadOnlyList<FileDescriptor> borrowedDescriptors,
        IMemoryBudget memoryBudget)
        : this(
            new EvaluationState(script, options.CurrentDirectory, descriptorSetup.Bindings.Fork()),
            global::Lokad.Bash.Evaluation.Variables.CreateEmpty(),
            descriptorSetup,
            options,
            new EvaluationContext(0, null, ReturnTargetKind.None),
            borrowedDescriptors,
            RootFlowHandling.Normalize,
            publishNextStep,
            onComplete,
            memoryBudget)
    {
        _vars.TrySetScalar("PWD", ShellValue.FromText(_state.CurrentDirectory.Path));
        _vars.SetExported("PWD", true);
    }

    /// <summary>Forks bindings and shell state; descriptors stay borrowed and live memory shares the caller's allowance.</summary>
    internal static Evaluation CreateCommandSubstitution(
        ScopedEvaluation scope,
        ParsedScript script,
        FileDescriptor output)
    {
        var outer = scope.Effects;
        var descriptors = outer.CreateCommandSubstitutionDescriptorSetup(output);
        var state = outer._state.CloneForNestedEvaluation(script, descriptors, ShellForkKind.CommandSubstitution);
        var variables = outer.Memory.CloneVariables(scope.Variables);
        return new Evaluation(
            state,
            variables,
            descriptors,
            outer.Options with { CurrentDirectory = state.CurrentDirectory },
            scope.Context.Fork(variables),
            outer.CollectDescriptorsAvailableToNestedEvaluation(),
            RootFlowHandling.Preserve,
            publishNextStep: null,
            onComplete: null,
            outer.Memory.Budget);
    }

    private Evaluation(
        EvaluationState state,
        Variables variables,
        EvaluationDescriptorSetup descriptorSetup,
        EvaluationOptions options,
        EvaluationContext initialContext,
        IReadOnlyList<FileDescriptor> borrowedDescriptors,
        RootFlowHandling rootFlowHandling,
        Action<EvaluationOperation?>? publishNextStep,
        Action? onComplete,
        IMemoryBudget memoryBudget)
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
        _rootFlowHandling = rootFlowHandling;
        _borrowedDescriptors = MergeBorrowedDescriptors(
            borrowedDescriptors,
            descriptorSetup.BorrowedDescriptors);
        Memory = new EvaluationMemory(memoryBudget);

        static IReadOnlyList<FileDescriptor> MergeBorrowedDescriptors(
            IReadOnlyList<FileDescriptor> first,
            IReadOnlyList<FileDescriptor> second)
        {
            if (second.Count == 0)
                return first;
            if (first.Count == 0)
                return second;

            var descriptors = new DescriptorHandleCollector(new List<FileDescriptor>(first));
            descriptors.AddRange(second);
            return descriptors.Descriptors;
        }
    }

    /// <summary> Gets evaluation options. </summary>
    public EvaluationOptions Options { get; }

    internal ShellValue ShellOptionFlags => _state.ErrexitEnabled ? ShellValue.FromText("e") : ShellValue.Empty;

    /// <summary> Gets the last recorded exit code, if any. </summary>
    public int? LastExitCode => _state.LastExitCode;

    /// <summary> Gets the current working directory. </summary>
    public ShellPath CurrentDirectory => _state.CurrentDirectory;

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

    async Task<bool> IFileTester.IsDirectoryAsync(string path) =>
        await ExecuteFileTestsForEvaluationAsync([new UnresolvedFileTest(FilePredicateKind.Directory, path, null)],
            static results => results[0]).ConfigureAwait(false) == 0;

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

    /// <summary> Records that final host-side cleanup completed for a descriptor. </summary>
    internal void MarkDescriptorCleanupCompleted(FileDescriptor descriptor) =>
        MarkDescriptorClosed(descriptor);

    /// <summary>Transfers unfinished child cleanup to its parent after the child has settled.</summary>
    internal void TransferDescriptorCleanupTo(Evaluation parent)
    {
        foreach (var descriptor in GetDescriptorsForCleanup())
        {
            parent.TrackOwnedDescriptor(descriptor);
            MarkDescriptorCleanupCompleted(descriptor);
        }
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
            var descriptors = new DescriptorHandleCollector(new List<FileDescriptor>());
            _state.AddRedirectionHandlesTo(ref descriptors, _borrowedDescriptors);
            return await CloseDescriptorsAsync(descriptors.Descriptors, operationFailed).ConfigureAwait(false);
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

            if (result.Flow == FlowControlKind.Exit)
                return result;

            if (result.Flow != FlowControlKind.None)
            {
                if (rootFlowHandling == RootFlowHandling.Preserve)
                    return result;

                result = NormalizeUnhandledFlow(result);
                _state.LastExitCode = result.ExitCode;
            }

            exitCode = result.ExitCode;
        }

        return EvaluationResult.FromExitCode(_state.TryGetStickyExitCode(out var sticky) ? sticky : exitCode);
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
        var redirectionScope = await OpenRedirectionScopeAsync(redirectedCommand.Redirections, scope, scope)
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
        bool negated)
    {
        var parentState = _state;
        var parentVars = _vars;
        var inheritedRedirectionDescriptors = parentState.CurrentRedirectionOpenedDescriptors;
        var borrowedDescriptors = new HashSet<FileDescriptor>(CollectDescriptorsAvailableToNestedEvaluation());
        // Child resets must not release the parent's saved handlers before restoration.
        Memory.Charge(RetainedTrapMemoryBytes);
        var childState = parentState.CloneForSubshell();
        var childVars = Memory.CloneVariables(scope.Variables);
        var childContext = scope.Context.Fork(childVars);
        var result = EvaluationResult.FromExitCode(0);
        OperationError? closeError = null;

        _state = childState;
        _vars = childVars;
        _subshellBorrowedDescriptorScopes.Push(borrowedDescriptors);
        Exception? failure = null;
        try
        {
            result = await EvaluateCommandAsync(subshellCommand.Body, scope with { Variables = childVars, Context = childContext }, negated: false)
                .ConfigureAwait(false);
            result = await RunExitTrapAsync(scope with { Variables = childVars, Context = childContext }, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var descriptorsToClose = new DescriptorHandleCollector(new List<FileDescriptor>());
            _state.AddRedirectionHandlesTo(ref descriptorsToClose, inheritedRedirectionDescriptors);

            try
            {
                closeError = await CloseDescriptorsAsync(descriptorsToClose.Descriptors,
                    operationFailed: failure != null || result.ExitCode != 0).ConfigureAwait(false);
            }
            finally
            {
                _state = parentState;
                _vars = parentVars;
                _ = _subshellBorrowedDescriptorScopes.Pop();
            }
        }

        result = await ApplyCloseErrorAsync(result, closeError).ConfigureAwait(false);
        result = EvaluationResult.FromExitCode(result.ExitCode);

        scope.Variables.SetPipelineStatuses([result.ExitCode], Memory, _cancellationToken);
        return ApplyNegation(result, negated);
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
        var commands = new List<ICommand>();
        var operators = new List<PipelineOperator>();
        FlattenPipeline(pipelineCommand, commands, operators);
        var pipes = new List<PipeDescriptors>(Math.Max(0, commands.Count - 1));
        var ownershipTransferred = false;
        try
        {
            for (var index = 0; index < commands.Count - 1; index++)
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
            }

            using var pipelineMemory = Memory.Budget.CreateScope();
            var stages = new List<PipelineEvaluationStage>(commands.Count);
            for (var index = 0; index < commands.Count; index++)
            {
                var stdIn = index > 0 ? pipes[index - 1].Read : GetStdInDescriptor();
                var stdOut = index < commands.Count - 1 ? pipes[index].Write : GetStdOutDescriptor();
                var stdErr = index < commands.Count - 1 && operators[index] == PipelineOperator.PipeAndError
                    ? pipes[index].Write
                    : GetStdErrDescriptor();
                var script = new ParsedScript([commands[index]]);
                var descriptors = new EvaluationDescriptorSetup(
                    CaptureDescriptorBindings().WithStandardStreams(stdIn, stdOut, stdErr), []);
                var forkKind = commands[index] is SimpleCommand ? ShellForkKind.PipelineSimpleCommand : ShellForkKind.Subshell;
                var stageState = _state.CloneForNestedEvaluation(script, descriptors, forkKind);
                var stageVars = Memory.CloneVariables(scope.Variables);
                var stageContext = scope.Context.Fork(stageVars);
                var stage = new Evaluation(
                    stageState,
                    stageVars,
                    descriptors,
                    Options with { CurrentDirectory = stageState.CurrentDirectory },
                    stageContext,
                    CollectDescriptorsAvailableToNestedEvaluation(),
                    RootFlowHandling.Preserve,
                    publishNextStep: null,
                    onComplete: null,
                    Memory.Budget);
                var descriptorsToClose = new List<FileDescriptor>(2);
                if (index > 0)
                    descriptorsToClose.Add(stdIn);
                if (index < commands.Count - 1)
                    descriptorsToClose.Add(stdOut);

                stage._retainCompletedMemory = true;
                stage.Memory.Budget = pipelineMemory;
                stages.Add(new PipelineEvaluationStage(stage, descriptorsToClose));
            }

            var pipelineTask = RunPipelineEvaluations.AsTask(PublishOperation, stages);
            ownershipTransferred = true;
            try
            {
                // The host propagates cancellation to the stages. Await their cleanup before taking ownership.
                var results = await pipelineTask.ConfigureAwait(false);
                scope.Variables.SetPipelineStatuses(results, Memory, _cancellationToken);
                var exitCode = ComputePipelineExitCode(results);
                var pipelineNegated = pipelineCommand.Negated ^ negated;
                var result = EvaluationResult.FromExitCode(EvaluationRules.ApplyNegation(exitCode, pipelineNegated));
                return await FinalizeResultAsync(result, negated: false).ConfigureAwait(false);
            }
            finally
            {
                foreach (var stage in stages)
                    stage.Evaluation.TransferDescriptorCleanupTo(this);
            }
        }
        finally
        {
            if (!ownershipTransferred)
                await CloseCreatedPipesPreservingFailureAsync().ConfigureAwait(false);
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
    /// Flattens a nested pipeline command into a left-to-right command list and operator list.
    /// </summary>
    private static void FlattenPipeline(
        ICommand command,
        List<ICommand> commands,
        List<PipelineOperator> operators)
    {
        if (command is PipelineCommand pipeline)
        {
            FlattenPipeline(pipeline.Left, commands, operators);
            commands.Add(pipeline.Right);
            operators.Add(pipeline.Operator);
            return;
        }

        commands.Add(command);
    }

    /// <summary>
    /// Evaluates a simple command, including assignments, builtins, functions, and external invocations.
    /// </summary>
    private async Task<EvaluationResult> EvaluateSimpleCommandAsync(
        SimpleCommand simpleCommand,
        ScopedEvaluation scope)
    {
        _state.LastCommandSubstitutionExitCode = null;
        PreparedDeclaration? declaration = null;
        if (simpleCommand.DeclarationAssignments.Count > 0)
        {
            declaration = await AwaitWordExpansionAsync(PrepareDeclarationAsync(simpleCommand, scope)).ConfigureAwait(false);
            simpleCommand = declaration.Command;
        }
        simpleCommand = await ExpandCommandNameAsync(simpleCommand).ConfigureAwait(false);
        if (_state.HasPendingExpansionError)
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), false).ConfigureAwait(false);

        if (simpleCommand.Words.Count == 0)
        {
            // Assignments run in the enclosing descriptor scope, before any command-local redirections.
            var exitCode = 0;
            foreach (var assignment in simpleCommand.Assignments)
            {
                var applied = await AwaitWordExpansionAsync(ApplyAssignmentAsync(assignment)).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), false).ConfigureAwait(false);
                if (!applied)
                    exitCode = 1;
            }

            var redirectionScope = await OpenRedirectionScopeAsync(simpleCommand.Redirections, scope, scope)
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
            return await ApplyCloseErrorAsync(assignmentResult, assignmentCloseError).ConfigureAwait(false);
        }

        var commandValue = await ExpandWordValueAsync(simpleCommand.Words[0], scope).ConfigureAwait(false);
        var commandName = string.Empty;
        if (_state.TryConsumeExpansionError(out var nameError))
            return await FinalizeResultAsync(EvaluationResult.FromExitCode(nameError), false).ConfigureAwait(false);

        var preparation = await AwaitWordExpansionAsync(PrepareCommandAsync(simpleCommand, commandValue, scope)).ConfigureAwait(false);
        if (_state.TryConsumeExpansionError(out var preparationError))
            return await FailPreparationAsync(preparationError).ConfigureAwait(false);

        if (declaration == null && preparation is PreparedCommand.Executable
            { Resolution: CommandResolution.EvaluatorBuiltin { Kind: BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local } } declarationCommand)
        {
            declaration = await AwaitWordExpansionAsync(PrepareDeclarationAsync(declarationCommand.SimpleCommand, scope)).ConfigureAwait(false);
            preparation = declarationCommand with { SimpleCommand = declaration.Command };
        }
        if (_state.TryConsumeExpansionError(out var declarationError))
            return await FailPreparationAsync(declarationError).ConfigureAwait(false);

        // Expand once in the caller scope before redirections and temporary bindings.
        IReadOnlyList<string> arguments = [];
        IReadOnlyList<ShellValue> byteOperands = [];
        string? sourcePath = null;
        if (preparation is PreparedCommand.Executable command)
        {
            if (command.Resolution is not CommandResolution.EvaluatorBuiltin
                || command.Resolution is CommandResolution.EvaluatorBuiltin
                {
                    Kind: BuiltinKind.Printf or BuiltinKind.Echo or BuiltinKind.Hash or BuiltinKind.Type or BuiltinKind.Help or BuiltinKind.Export
                        or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local
                        or BuiltinKind.Set or BuiltinKind.Source or BuiltinKind.Getopts or BuiltinKind.Eval or BuiltinKind.Let
                        or BuiltinKind.Read or BuiltinKind.Mapfile or BuiltinKind.Unset or BuiltinKind.Return or BuiltinKind.Trap or BuiltinKind.Test
                        or BuiltinKind.Exit or BuiltinKind.Break or BuiltinKind.Continue
                })
            {
                var kind = (command.Resolution as CommandResolution.EvaluatorBuiltin)?.Kind;
                var operands = new List<ShellValue>();
                if (kind is not null and not BuiltinKind.Printf and not BuiltinKind.Echo and not BuiltinKind.Hash and not BuiltinKind.Source)
                    operands.Add(ShellValue.FromText(command.CommandName));
                for (var index = 1; (declaration == null || kind is not (BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local)) && index < command.SimpleCommand.Words.Count; index++)
                {
                    if (kind == BuiltinKind.Source && index == 1)
                        sourcePath = await ExpandWordAsync(command.SimpleCommand.Words[index], scope).ConfigureAwait(false);
                    else if (kind is BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local
                        || kind == BuiltinKind.Getopts && index <= 2)
                        operands.Add(await ExpandWordValueAsync(command.SimpleCommand.Words[index], scope).ConfigureAwait(false));
                    else
                    {
                        var expanded = await ExpandToWordsAsync(command.SimpleCommand.Words[index], scope).ConfigureAwait(false);
                        operands.AddRange(expanded);
                    }
                    if (_state.HasPendingExpansionError)
                        break;
                }
                byteOperands = operands;
            }
            else
            {
                arguments = await ExpandArgumentsAsync(command).ConfigureAwait(false);
            }
        }
        else if (preparation is PreparedCommand.Terminal terminalArguments)
        {
            foreach (var word in terminalArguments.SimpleCommand.Words)
            {
                _ = await ExpandToWordsAsync(word, scope).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    break;
            }
        }

        if (_state.TryConsumeExpansionError(out var argumentError))
            return await FailPreparationAsync(argumentError).ConfigureAwait(false);

        // Prepare echo output before prefix assignments and redirections can cause effects.
        ReadOnlyMemory<byte> echoContent = default;
        using var echoMemory = preparation is PreparedCommand.Executable
            { Resolution: CommandResolution.EvaluatorBuiltin { Kind: BuiltinKind.Echo } }
            ? Memory.Budget.CreateScope() : null;
        if (echoMemory != null)
        {
            try
            {
                echoContent = EchoFormatting.Format(byteOperands, EchoFormatting.GetOutputFormat(byteOperands), echoMemory);
            }
            catch (MemoryBudgetExceededException)
            {
                await ReportMemoryRefusalAsync("echo").ConfigureAwait(false);
                return await FailPreparationAsync(1).ConfigureAwait(false);
            }

        }

        var commandVars = scope.Variables;
        var commandAssignmentsSuccessful = true;
        var prefixNames = simpleCommand.Assignments.Count == 0 ? null : new HashSet<BindingName>();
        var assignmentError = await AwaitWordExpansionAsync(PrepareAssignmentsAsync()).ConfigureAwait(false);
        if (assignmentError.HasValue)
            return await FailPreparationAsync(assignmentError.Value).ConfigureAwait(false);

        // In-process redirections write both caller bindings and the prepared command scope.
        // Do not infer writes by comparing values: assigning the caller's old value still replaces a prefix.
        var redirectionExpansionScope = scope;
        // exec keeps redirections in the current shell, including their writes and fatal expansion state.
        var externalRedirections = simpleCommand.Redirections.Count > 0
            && preparation is PreparedCommand.Executable
                { Resolution: CommandResolution.Host, Completion: CommandCompletion.Continue };
        if (externalRedirections)
        {
            // Literal targets cannot write bindings. Isolate potential expansion writes from the caller
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
                simpleCommand.Redirections, redirectionExpansionScope, externalRedirections ? scope : redirectionExpansionScope)
            .ConfigureAwait(false);
        Exception? scopeFailure = null;
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
            commandCloseError = await commandRedirections.CloseAsync(
                scopeFailure != null || preparedResult.ExitCode != 0).ConfigureAwait(false);
        }
        return await ApplyCloseErrorAsync(preparedResult, commandCloseError).ConfigureAwait(false);

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

            if (preparation is PreparedCommand.Query query)
            {
                var queryScope = scope with { Variables = commandVars };
                if (query.ExecutionOptions is { } queryOptions)
                {
                    var queryName = PrepareExecArgumentZero(queryOptions, "command");
                    queryScope = ForkExecScope(queryScope, queryName);
                    if (queryOptions.ClearEnvironment)
                        ClearExecExports(queryScope, queryScope.Variables);
                }
                var queryExitCode = await EvaluateCommandQueryAsync(
                        BuiltinKind.Command,
                        query.Arguments,
                        query.OutputMode,
                        queryScope.Variables,
                        queryScope.Context)
                    .ConfigureAwait(false);
                var queryResult = await FinalizeResultAsync(EvaluationResult.FromExitCode(queryExitCode), false)
                    .ConfigureAwait(false);
                return CompleteCommandResult(queryResult, query.Completion, commandInvoked: true);
            }

            if (preparation is PreparedCommand.Terminal terminal)
            {
                // Argument/prefix expansion and redirections can supersede a prepared failure.
                if (!terminal.Message.IsEmpty)
                    await ExecuteWriteAsync(terminal.Channel == CommandMessageChannel.StandardOutput
                        ? GetStdOutDescriptor() : GetStdErrDescriptor(), terminal.Message.Bytes).ConfigureAwait(false);
                var terminalResult = await FinalizeResultAsync(terminal.Result, false).ConfigureAwait(false);
                return CompleteCommandResult(terminalResult, terminal.Completion, commandInvoked: true);
            }

            var executable = (PreparedCommand.Executable)preparation;
            commandName = executable.CommandName;
            // A failed redirection must not change exports or $0.
            ShellValue? argumentZero = executable.ExecutionOptions is { } executionOptions
                ? PrepareExecArgumentZero(executionOptions, commandName) : null;
            if (executable.Resolution is CommandResolution.Function function)
            {
                var functionResult = await EvaluateFunctionAsync(
                        function.Definition,
                        byteOperands,
                        scope,
                        commandVars,
                        prefixNames,
                        false)
                    .ConfigureAwait(false);
                return CompleteCommandResult(functionResult, executable.Completion, commandInvoked: true);
            }

            if (executable.Resolution is CommandResolution.EvaluatorBuiltin evaluatorBuiltin)
            {
                var builtinScope = scope;
                if (argumentZero is { } builtinName)
                {
                    // Fork before installing temporary bindings so builtin writes cannot reach the caller's frames.
                    builtinScope = ForkExecScope(scope, builtinName);
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
                    : new Variables.CommandBindings(builtinScope.Variables, commandVars, prefixNames);
                if (bindings != null)
                    commandVars = builtinScope.Variables;

                var enclosingFunction = builtinScope.Context.FunctionContext;
                FunctionContext? scriptFunction = null;
                if (enclosingFunction != null && bindings != null
                    && evaluatorBuiltin.Kind is BuiltinKind.Eval or BuiltinKind.Source)
                {
                    scriptFunction = new FunctionContext(enclosingFunction.FunctionName,
                        enclosingFunction.Locals.WithTemporaryPrefixes(bindings), enclosingFunction);
                    builtinScope = builtinScope with
                    {
                        Context = new EvaluationContext(builtinScope.Context.LoopDepth, scriptFunction, builtinScope.Context.ReturnTarget) { GlobalPrefixes = builtinScope.Context.GlobalPrefixes, ErrexitIgnored = builtinScope.Context.ErrexitIgnored }
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
                            GlobalPrefixes = new GlobalPrefixBindings(bindings,
                                builtinScope.Context.FunctionContext?.Depth ?? 0, builtinScope.Context.GlobalPrefixes)
                        }
                    };
                }

                if (executable.ExecutionOptions is { ClearEnvironment: true })
                    ClearExecExports(builtinScope, commandVars);

                var builtinResult = evaluatorBuiltin.Kind switch
                {
                    BuiltinKind.Break or BuiltinKind.Continue => await EvaluateBuiltinLoopControlAsync(
                        evaluatorBuiltin.Kind, byteOperands, builtinScope, false).ConfigureAwait(false),
                    BuiltinKind.Exit => await EvaluateBuiltinExitAsync(byteOperands, builtinScope, executable.Completion).ConfigureAwait(false),
                    BuiltinKind.Test => await EvaluateBuiltinTestAsync(commandName, byteOperands, false).ConfigureAwait(false),
                    BuiltinKind.Echo => await EvaluateBuiltinEchoAsync(echoContent, false).ConfigureAwait(false),
                    BuiltinKind.Type => await EvaluateBuiltinTypeAsync(byteOperands, commandVars, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Help => await AwaitWordExpansionAsync(EvaluateBuiltinHelpAsync(byteOperands, false)).ConfigureAwait(false),
                    BuiltinKind.Printf => await AwaitWordExpansionAsync(EvaluateBuiltinPrintfAsync(
                        byteOperands, commandVars, bindings, builtinScope.Context, false)).ConfigureAwait(false),
                    BuiltinKind.Read => await AwaitWordExpansionAsync(EvaluateBuiltinReadAsync(
                        byteOperands, new ScopedEvaluation(this, commandVars, builtinScope.Context), false)).ConfigureAwait(false),
                    BuiltinKind.Mapfile => await AwaitWordExpansionAsync(EvaluateBuiltinMapfileAsync(
                        byteOperands, commandVars, false)).ConfigureAwait(false),
                    BuiltinKind.Export or BuiltinKind.Readonly or BuiltinKind.Declare or BuiltinKind.Local =>
                        await AwaitWordExpansionAsync(EvaluateBuiltinDeclarationAsync(
                            declaration ?? throw new InvalidOperationException("Declaration was not prepared."),
                            commandVars, bindings, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Set => await EvaluateBuiltinSetAsync(byteOperands, commandVars, false).ConfigureAwait(false),
                    BuiltinKind.Unset => await AwaitWordExpansionAsync(EvaluateBuiltinUnsetAsync(byteOperands, commandVars, builtinScope, bindings, false)).ConfigureAwait(false),
                    BuiltinKind.Eval => await EvaluateBuiltinEvalAsync(byteOperands, builtinScope, commandVars, false).ConfigureAwait(false),
                    BuiltinKind.Trap => await AwaitWordExpansionAsync(EvaluateBuiltinTrapAsync(byteOperands, builtinScope)).ConfigureAwait(false),
                    BuiltinKind.Return => await AwaitWordExpansionAsync(
                        EvaluateBuiltinReturnAsync(byteOperands, builtinScope, false)).ConfigureAwait(false),
                    BuiltinKind.Let => await EvaluateBuiltinLetAsync(byteOperands, commandVars, bindings, builtinScope.Context, false).ConfigureAwait(false),
                    BuiltinKind.Source => await EvaluateBuiltinSourceAsync(sourcePath, byteOperands, builtinScope, commandVars, false).ConfigureAwait(false),
                    BuiltinKind.Getopts => await EvaluateBuiltinGetoptsAsync(byteOperands, commandVars, bindings, builtinScope.Context, false).ConfigureAwait(false),
                    // hash only distinguishes no operands from some; their values need no decoding.
                    BuiltinKind.Hash => await FinalizeResultAsync(
                        EvaluationResult.FromExitCode(byteOperands.Count == 0 ? 0 : 1), false).ConfigureAwait(false),
                    _ => await EvaluateBuiltinAsync(
                        evaluatorBuiltin.Kind,
                        commandName,
                        arguments,
                        builtinScope,
                        commandVars,
                        bindings,
                        false).ConfigureAwait(false)
                };
                if (scriptFunction != null)
                    CompletePromotions(scriptFunction, enclosingFunction, commandVars, bindings);
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
                CollectDescriptorsAvailableToNestedEvaluation(),
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
                using var bindings = prefixNames == null ? null : new Variables.CommandBindings(scope.Variables, commandVars, prefixNames);
                commandResult = await RunExitTrapAsync(scope, EvaluationResult.Exit(commandResult.ExitCode)).ConfigureAwait(false);
            }
            return CompleteCommandResult(commandResult, executable.Completion, commandInvoked: true);
        }

        // Capture the first surviving command field, retaining other fields without replaying their expansion.
        async Task<SimpleCommand> ExpandCommandNameAsync(SimpleCommand source)
        {
            if (source.Words.Count == 0)
                return source;
            var first = source.Words[0];
            if (first.Parts.Count == 1 && first.Parts[0].Kind == WordPartKind.SingleQuoted)
                return source;

            var words = new List<Word>(source.Words.Count);
            for (var index = 0; index < source.Words.Count; index++)
            {
                var fields = await ExpandToWordsAsync(source.Words[index], scope).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    break;
                if (fields.Count == 0)
                    continue;
                foreach (var field in fields)
                    words.Add(new Word([WordPart.FromValue(WordPartKind.SingleQuoted, field)]));
                for (var remaining = index + 1; remaining < source.Words.Count; remaining++)
                    words.Add(source.Words[remaining]);
                break;
            }
            return source with { Words = words };
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
            var variables = Memory.CloneVariables(source.Variables);
            variables.ArgumentZero = name;
            return source with { Variables = variables, Context = source.Context.Fork(variables) };
        }

        void ClearExecExports(ScopedEvaluation executionScope, Variables commandVariables)
        {
            foreach (var binding in _state.Functions.Values.ToArray())
                if (binding.Attributes.HasFlag(FunctionAttributes.Exported))
                    _state.SetFunctionBinding(binding with { Attributes = binding.Attributes & ~FunctionAttributes.Exported });
            ClearExports(commandVariables);
            if (!ReferenceEquals(commandVariables, executionScope.Variables))
                ClearExports(executionScope.Variables);
            for (var prefix = executionScope.Context.GlobalPrefixes; prefix != null; prefix = prefix.Enclosing)
                ClearExports(prefix.Saved);
            for (var frame = executionScope.Context.FunctionContext; frame != null; frame = frame.Enclosing)
            {
                if (frame.Locals.ExportSnapshot is { } locals)
                    ClearExports(locals);
                if (frame.PrefixBindings is { } functionBindings)
                    ClearExports(functionBindings.SavedVariables);
            }

            // Clear only the fork: hidden exports must not reappear, nor change a substitution's caller.
            static void ClearExports(Variables variables)
            {
                foreach (var name in new List<BindingName>(variables.ExportedNames))
                    variables.SetExported(name, false);
            }
        }

        bool TryBuildFunctionExports(out IReadOnlyList<ExportedFunction> exports)
        {
            exports = [];
            if (preparation is PreparedCommand.Executable { ExecutionOptions.ClearEnvironment: true })
                return true;
            var result = new List<ExportedFunction>();
            var remaining = ExportedFunction.MaximumSourceBytes;
            foreach (var function in _state.Functions.Values)
            {
                if (!function.Attributes.HasFlag(FunctionAttributes.Exported)) continue;
                if (!function.TryGetExport(remaining, Memory.Budget, out var export)) return false;
                remaining -= export.Source.Length;
                result.Add(export);
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
                if (elementNameref || unsetNameref)
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
                    if (assignment.Operator == AssignmentOperator.Append
                        && commandVars.TryGetExpansionValue(name, out var previous))
                        value = WordExpansion.AppendAssignmentValue(previous, value, assignmentScope);

                    if (!commandVars.TrySetScalar(name, value))
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
                commandVars.SetExported(name, true);
            }
            return null;

            async Task<ShellValue> ExpandCompoundPrefixAsync(IReadOnlyList<CompoundArrayItem> words)
            {
                var items = new List<ShellValue>();
                long length = 2;
                foreach (var word in words)
                {
                    // The enclosing await reports any limit once, before active redirections unwind.
                    var expanded = await WordExpansion.ExpandWithoutBraceAsync(
                        word.Word, assignmentScope, allowWordSplitting: false, ExpansionPurpose.CompoundPrefix,
                        allowLeadingTilde: false)
                        .ConfigureAwait(false);
                    if (_state.HasPendingExpansionError)
                        return ShellValue.Empty;

                    // Even an empty aggregate occupies its source item's place in the prefix text.
                    if (expanded.Count == 0)
                        AddItem(ShellValue.Empty);
                    foreach (var field in expanded)
                        AddItem(field.ToValue(Memory));
                }

                // Build the parentheses and separators in one budgeted copy, without an intermediate join.
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
                    items[index].Bytes.Span.CopyTo(destination);
                    destination = destination[items[index].Bytes.Length..];
                }
                return ShellValue.FromBytes(joined);

                void AddItem(ShellValue item)
                {
                    length += item.Bytes.Length + (items.Count == 0 ? 0L : 1L);
                    items.Add(item);
                }
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

        async Task<IReadOnlyList<string>> ExpandArgumentsAsync(PreparedCommand.Executable command)
        {
            var builtin = command.Resolution as CommandResolution.EvaluatorBuiltin;
            var words = command.SimpleCommand.Words;
            var expanded = new List<string>();
            if (builtin != null)
                expanded.Add(command.CommandName);
            // Match existing consumer arity; unused operands still expand for effects and limits.
            var lastTextOperandIndex = builtin?.Kind switch
            {
                BuiltinKind.Colon or BuiltinKind.True or BuiltinKind.False
                    or BuiltinKind.Pwd or BuiltinKind.Popd or BuiltinKind.Caller => 0,
                BuiltinKind.Cd or BuiltinKind.Dirs or BuiltinKind.Pushd
                    or BuiltinKind.Shift => 1,
                _ => words.Count - 1
            };

            for (var index = 1; index < words.Count; index++)
            {
                // Preserve each existing text consumer's scalar-versus-field expansion contract.
                var scalar = builtin?.Kind switch
                {
                    null or BuiltinKind.Echo or BuiltinKind.Type => false,
                    BuiltinKind.Test => command.CommandName == "[" && index == words.Count - 1,
                    _ => true
                };
                if (index > lastTextOperandIndex)
                {
                    _ = await ExpandToWordsAsync(words[index], scope).ConfigureAwait(false);
                }
                else if (scalar)
                    expanded.Add(await ExpandWordAsync(words[index], scope).ConfigureAwait(false));
                else
                    expanded.AddRange(await ExpandTextWordsAsync(words[index], scope).ConfigureAwait(false));

                if (_state.HasPendingExpansionError)
                    break;
            }
            return expanded;
        }

        // Applies or inspects prepared declarations without replaying expansion effects.
        async Task<EvaluationResult> EvaluateBuiltinDeclarationAsync(
            PreparedDeclaration declaration, Variables commandVars, Variables.CommandBindings? bindings, ScopedEvaluation declarationScope, bool declarationNegated)
        {
            var options = declaration.Options;
            var executionScope = declarationScope with { Variables = commandVars };
            if (!options.InvalidOption.IsEmpty)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(options.InvalidOption, ShellValue.FromText(": invalid option"), declarationScope))
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(2), declarationNegated).ConfigureAwait(false);
            }
            if (options.Builtin == BuiltinKind.Local && declarationScope.Context.FunctionContext == null)
            {
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("can only be used in a function"))
                    .ConfigureAwait(false);
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(1), declarationNegated).ConfigureAwait(false);
            }
            var listing = declaration.Operands.Count == 0;
            if (options.Functions && (!listing || options.Builtin != BuiltinKind.Local))
                return await FinalizeResultAsync(EvaluationResult.FromExitCode(await EvaluateFunctionsAsync().ConfigureAwait(false)), declarationNegated).ConfigureAwait(false);
            if (listing || options.Print && options.Builtin is BuiltinKind.Declare or BuiltinKind.Local)
            {
                var style = listing && options.Builtin == BuiltinKind.Declare && !options.Print
                    && options.ListingAttributes == DeclarationListingAttributes.None
                    ? VariableDisplayStyle.Assignment : options.FunctionNamesOnly ? VariableDisplayStyle.DeclarationName : VariableDisplayStyle.Declaration;
                var printStatus = 0;
                var outputClosed = false;
                await foreach (var nameValue in GetNamesAsync(GetListedNames()).ConfigureAwait(false))
                {
                    if (HasPendingExpansionError)
                        break;
                    var name = BindingNameExpansion.FromValue(nameValue, Memory);
                    if (!SelectVariables(name).HasBinding(name)
                        || options.Builtin == BuiltinKind.Local && declarationScope.Context.FunctionContext?.Locals.Declares(name) != true)
                    {
                        printStatus = 1;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName),
                            WordExpansion.AppendAssignmentValue(nameValue, ShellValue.FromText(": not found"), declarationScope)).ConfigureAwait(false);
                        continue;
                    }
                    using var output = new ByteMemoryBuilder(0, Memory.Budget);
                    if (!SelectVariables(name).TryAppendVariable(name, output, style))
                    {
                        printStatus = 1;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText("output limit exceeded")).ConfigureAwait(false);
                        break;
                    }
                    if (output.Length == 0)
                        continue;
                    var result = await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
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

                // No-argument listings hide only this command's prefixes; named queries inspect them.
                Variables SelectVariables(BindingName name) => !listing ? commandVars
                    : bindings?.Contains(name) == true ? bindings.SavedVariables : declarationScope.Variables;

                IEnumerable<ShellValue> GetListedNames()
                {
                    var names = commandVars.BindingNames;
                    if (bindings != null)
                        names = names.Concat(bindings.SavedVariables.BindingNames);
                    return names.Distinct().Where(MatchesFilter).Select(EncodeName);

                    ShellValue EncodeName(BindingName name)
                    {
                        Memory.Charge(checked(4L * name.ByteLength));
                        return name.ToValue();
                    }
                }

                bool MatchesFilter(BindingName name)
                {
                    var variables = SelectVariables(name);
                    if (!variables.HasBinding(name))
                        return false;
                    // Attribute options do not narrow a local listing.
                    if (options.Builtin == BuiltinKind.Local)
                        return declarationScope.Context.FunctionContext?.Locals.Declares(name) == true;
                    var flags = options.ListingAttributes;
                    return (!flags.HasFlag(DeclarationListingAttributes.Indexed) || variables.IndexedArrays.ContainsKey(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Associative) || variables.AssociativeArrays.ContainsKey(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Nameref) || variables.TryGetNameref(name, out _))
                        && (!flags.HasFlag(DeclarationListingAttributes.Exported) || variables.IsExported(name))
                        && (!flags.HasFlag(DeclarationListingAttributes.Readonly) || variables.IsReadonlyBinding(name));
                }
            }

            async Task<int> EvaluateFunctionsAsync()
            {
                var noOperands = declaration.Operands.Count == 0;
                if (!noOperands && !options.Print && options.Builtin is BuiltinKind.Declare or BuiltinKind.Local
                    && options.InvalidFunctionAttribute.Length != 0)
                {
                    await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), ShellValue.FromText(options.InvalidFunctionAttribute + ": invalid option"))
                        .ConfigureAwait(false);
                    return 1;
                }

                var inspect = noOperands || options.Builtin is BuiltinKind.Declare or BuiltinKind.Local
                    && (options.Print || !options.RemoveFunctions
                        && options.ExportChange == DeclarationAttributeChange.Unchanged
                        && options.ReadonlyChange == DeclarationAttributeChange.Unchanged
                        && options.IndexedChange == DeclarationAttributeChange.Unchanged
                        && options.AssociativeChange == DeclarationAttributeChange.Unchanged
                        && options.NamerefChange == DeclarationAttributeChange.Unchanged);
                var filter = FunctionAttributes.None;
                if (options.ListingAttributes.HasFlag(DeclarationListingAttributes.Readonly)) filter |= FunctionAttributes.Readonly;
                if (options.ListingAttributes.HasFlag(DeclarationListingAttributes.Exported)) filter |= FunctionAttributes.Exported;
                var names = _state.Functions.Keys;
                var functionStatus = 0;
                await foreach (var name in GetNamesAsync(names).ConfigureAwait(false))
                {
                    if (HasPendingExpansionError) break;
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
                            await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(name, ShellValue.FromText(": not a function"), declarationScope)).ConfigureAwait(false);
                        else if (options.Print)
                            await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(name, ShellValue.FromText(": not found"), declarationScope)).ConfigureAwait(false);
                        continue;
                    }
                    var attributes = function.Attributes;
                    // Function lists select the union of attributes, unlike variable lists.
                    if (noOperands && options.ListingAttributes != DeclarationListingAttributes.None && (attributes & filter) == 0)
                        continue;
                    if (!inspect)
                    {
                        // Export validates transport names; declare/local only set the attribute.
                        if (options.Builtin == BuiltinKind.Export && options.ExportChange == DeclarationAttributeChange.Set
                            && !function.Definition.Name.CanExport)
                        {
                            functionStatus = 1;
                            await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(name, ShellValue.FromText(": cannot export"), declarationScope)).ConfigureAwait(false);
                            continue;
                        }
                        if (options.ReadonlyChange == DeclarationAttributeChange.Clear && attributes.HasFlag(FunctionAttributes.Readonly))
                        {
                            functionStatus = 1;
                            await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(name, ShellValue.FromText(": readonly function"), declarationScope)).ConfigureAwait(false);
                            continue;
                        }
                        if (options.Readonly) attributes |= FunctionAttributes.Readonly;
                        if (options.ExportChange == DeclarationAttributeChange.Set) attributes |= FunctionAttributes.Exported;
                        if (options.ExportChange == DeclarationAttributeChange.Clear) attributes &= ~FunctionAttributes.Exported;
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
                    var result = await ExecuteWriteAsync(GetStdOutDescriptor(), output.ToMemory()).ConfigureAwait(false);
                    if (!result.CanAcceptMore)
                        return result.IsError ? 1 : functionStatus;
                }
                return functionStatus;
            }

            async IAsyncEnumerable<ShellValue> GetNamesAsync(IEnumerable<ShellValue> listedNames)
            {
                if (declaration.Operands.Count == 0)
                {
                    foreach (var name in listedNames.Select(ChargeName).Order(ShellValue.ByteOrder))
                        yield return name;

                    ShellValue ChargeName(ShellValue name)
                    {
                        Memory.Charge(256);
                        return name;
                    }
                }
                else
                {
                    // Explicit operands remain literal and join through the shared budget/diagnostic path.
                    foreach (var operand in declaration.Operands)
                        yield return await ExpandWordValueAsync(operand.ToWord(), declarationScope).ConfigureAwait(false);
                }
            }

            var exitCode = 0;
            foreach (var operand in declaration.Operands)
            {
                if (!TryParseDeclarationReference(ShellSource.FromBytes(operand.Name.Bytes), out var reference))
                {
                    var invalidOperand = await ExpandWordValueAsync(operand.ToWord(), declarationScope).ConfigureAwait(false);
                    if (HasPendingExpansionError)
                        break;
                    await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(invalidOperand), "not a valid identifier").ConfigureAwait(false);
                    continue;
                }
                if (reference.Kind != VariableReferenceKind.Name && options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                {
                    await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(operand.Name), "not a valid identifier").ConfigureAwait(false);
                    continue;
                }
                // Bash 5.2 resolves global names but keeps element writes in the active scope.
                var sourceName = reference.Name;
                var global = options.Global && reference.Kind == VariableReferenceKind.Name;
                var local = options.IsLocal(declarationScope);
                if (!global && !local && !operand.CompoundApplied && operand.Value == null)
                {
                    var visibleName = options.Nameref ? sourceName : StripArraySubscript(commandVars.ResolveNamerefName(sourceName));
                    if (!visibleName.IsEmpty && (!commandVars.HasBinding(visibleName)
                        || commandVars.IsUnassignedScalar(visibleName) && (options.Readonly || options.ExportChange == DeclarationAttributeChange.Set)))
                    {
                        // An unset prefix exposes its caller's binding; an unset local still hides it.
                        var exposedPrefixes = new List<Variables.CommandBindings>();
                        var owner = declarationScope.Context.GetBindingOwner(visibleName, commandVars, DeclarationBindingScope.Visible, exposedPrefixes);
                        if (!ReferenceEquals(owner, commandVars))
                            commandVars.CopyBindingFrom(owner, visibleName);
                        foreach (var prefix in exposedPrefixes)
                            prefix.ExposeBinding(visibleName, commandVars);
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
                        if (!circular && options.ArrayKind != ArrayAssignmentKind.Auto
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
                    if (options.ArrayKind != ArrayAssignmentKind.Auto)
                        lastReference = targetVars.SelectArrayDeclarationBinding(sourceName, lastReference);
                    var lastTarget = lastReference.Reference;
                    reference = reference.Kind == VariableReferenceKind.Name ? lastTarget
                        : VariableReference.ForElement(lastTarget.Name, reference.Subscript);
                }
                var name = reference.Name;
                var existingLocal = declarationScope.Context.FunctionContext?.Locals.Declares(name) == true;
                if (resolution == BindingResolution.LastNameref && name != sourceName
                    && options.ArrayKind == ArrayAssignmentKind.Auto && !operand.CompoundApplied
                    && targetVars.TryGetNameref(name, out var unsetTarget) && unsetTarget.IsEmpty)
                    continue; // +n cannot detach an indirectly reached unset reference or apply attributes to it.
                if (!operand.CompoundApplied && !local && options.NamerefChange != DeclarationAttributeChange.Set
                    && options.ArrayKind != ArrayAssignmentKind.Auto
                    && (recreateArrayReference || targetVars.TryResolveBinding(name, selectOwner: null, out var arrayBinding)
                        && arrayBinding.Owner.TryGetNameref(arrayBinding.Name, out var arrayTarget) && arrayTarget.IsEmpty))
                {
                    // Recreating an unset reference discards its old attributes before checking the initializer.
                    targetVars.RecreateDeclarationArray(name,
                        options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
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
                        circularLocalReference = existingLocal && options.ArrayKind == ArrayAssignmentKind.Auto;
                    }
                    else if (options.ArrayKind == ArrayAssignmentKind.Auto)
                    {
                        if (options.Builtin == BuiltinKind.Export
                            && options.ExportChange == DeclarationAttributeChange.Clear && operand.Value == null)
                            continue;
                        // Ordinary binding cannot create through a cycle; this is a successful declaration no-op.
                        await ReportCircularNameReferenceAsync(name).ConfigureAwait(false);
                        if (operand.Value != null && options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                        {
                            if (options.ExportChange != DeclarationAttributeChange.Clear)
                                await ReportCircularNameReferenceAsync(name).ConfigureAwait(false); // Attribute target lookup.
                            exitCode = 1;
                        }
                        continue;
                    }
                    if (options.ArrayKind != ArrayAssignmentKind.Auto && (!local || existingLocal))
                        targetVars.RecreateDeclarationArray(name,
                            options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed);
                }
                ShellValue? namerefTarget = null;
                if (!operand.CompoundApplied && (options.Nameref || circularLocalReference))
                {
                    ShellValue? previous = null;
                    if (!local || existingLocal || prefixNames?.Contains(name) == true)
                        previous = targetVars.TryGetNameref(name, out var referenced)
                            ? referenced.IsEmpty ? null : referenced
                            : targetVars.Scalars.TryGetValue(name, out var scalar) ? scalar : null;
                    // Validate before creating a local: invalid targets leave the outer binding visible.
                    namerefTarget = operand.Value ?? previous;
                    if (operand.Operator == AssignmentOperator.Append && operand.Value != null)
                        namerefTarget = WordExpansion.AppendAssignmentValue(previous ?? ShellValue.Empty, operand.Value.Value, declarationScope);
                    if (namerefTarget is { } targetValue && !TryParseDeclarationReference(ShellSource.FromBytes(targetValue.Bytes), out _))
                    {
                        await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(targetValue), operand.Value is { IsEmpty: true }
                                ? "not a valid identifier" : "invalid variable name for name reference")
                            .ConfigureAwait(false);
                        continue;
                    }
                }
                if (operand.Value != null && reference.Kind != VariableReferenceKind.Name
                    && !local && options.NamerefChange != DeclarationAttributeChange.Clear
                    && targetVars.TryGetNameref(name, out _))
                {
                    if (!targetVars.TryRemoveNamerefAttribute(name))
                    {
                        await DiagnoseAsync(name.ToValue(), "readonly variable").ConfigureAwait(false);
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
                if (!operand.CompoundApplied && (operand.Value != null || local && options.ArrayKind == ArrayAssignmentKind.Auto || options.Nameref
                        || options.ReadonlyChange == DeclarationAttributeChange.Clear)
                    && (!local || existingLocal) && readonlyBinding)
                {
                    if (operand.Value != null && options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                    {
                        // These builtins apply attributes even when their assignment fails.
                        exitCode = 1;
                        await ReportExpansionDiagnosticAsync(targetVars.ResolveNamerefName(name).ToValue(), ShellValue.FromText("readonly variable")).ConfigureAwait(false);
                        await ApplyAttributesAsync(name, targetVars, global, local).ConfigureAwait(false);
                    }
                    else
                        await DiagnoseAsync(declarationName.ToValue(), "readonly variable").ConfigureAwait(false);
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
                            await DiagnoseAsync(name.ToValue(), "readonly variable").ConfigureAwait(false);
                            continue;
                        }
                        targetVars.TrySetResolvedScalar(name, retainedTarget, Memory);
                    }
                }
                var convertedReference = false;
                if (options.Nameref && (!local || existingLocal)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name.ToValue(), "reference variable cannot be an array").ConfigureAwait(false);
                    continue;
                }
                if (options.NamerefChange == DeclarationAttributeChange.Set && options.ArrayKind != ArrayAssignmentKind.Auto
                    && targetVars.TryGetNameref(name, out var arrayReference) && (!local && !arrayReference.IsEmpty || local && existingLocal))
                {
                    // With both -n and an array flag, the builtin converts the reference itself,
                    // independently of any compound write already applied to its referent.
                    var kind = options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed;
                    if (local)
                    {
                        if (!targetVars.TryDeclareResolvedArray(name, kind, ScalarArrayPromotion.DiscardValue))
                        {
                            await DiagnoseAsync(name.ToValue(), "invalid local declaration").ConfigureAwait(false);
                            continue;
                        }
                    }
                    else
                    {
                        if (!targetVars.TryRemoveNamerefAttribute(name))
                        {
                            await DiagnoseAsync(name.ToValue(), "readonly variable").ConfigureAwait(false);
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
                    var initializingReference = existingLocal && options.ArrayKind == ArrayAssignmentKind.Auto
                        && operand.Value != null && targetVars.TryResolveBinding(name, selectOwner: null, out var terminalBinding)
                        && terminalBinding.IsUnsetNameref;
                    var localValue = operand.Value ?? ShellValue.Empty;
                    if (initializingReference && !TryParseDeclarationReference(ShellSource.FromBytes(localValue.Bytes), out _))
                    {
                        await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(localValue), "invalid variable name for name reference").ConfigureAwait(false);
                        continue;
                    }
                    var hasReferenceValue = (options.Nameref || initializingReference) && operand.Value != null;
                    var localOperand = new VariableOperand(
                        hasReferenceValue ? VariableOperandKind.Assignment : VariableOperandKind.Name,
                        reference, hasReferenceValue ? namerefTarget ?? localValue : ShellValue.Empty);
                    if (!localFunction.Locals.TryDeclare(declarationScope.Variables, targetVars,
                            localOperand, prefixNames, options.ArrayKind, options.Nameref, Memory))
                    {
                        var message = targetVars.IsReadonly(name) ? "readonly variable"
                            : options.ArrayKind != ArrayAssignmentKind.Auto
                                ? ArrayConversionMessage(options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed)
                                : "invalid local declaration";
                        await DiagnoseAsync(declarationName.ToValue(), message).ConfigureAwait(false);
                        continue;
                    }
                    localReferenceAssigned = options.Nameref || initializingReference;
                }
                if ((options.Nameref || (operand.CompoundApplied || local) && options.NamerefChange == DeclarationAttributeChange.Set)
                    && (targetVars.IndexedArrays.ContainsKey(name) || targetVars.AssociativeArrays.ContainsKey(name)))
                {
                    await DiagnoseAsync(name.ToValue(), "reference variable cannot be an array").ConfigureAwait(false);
                    continue;
                }
                var resolvedName = targetVars.ResolveNamerefName(name);
                if (options.IndexedChange == DeclarationAttributeChange.Clear && targetVars.IndexedArrays.ContainsKey(resolvedName)
                    || options.AssociativeChange == DeclarationAttributeChange.Clear && targetVars.AssociativeArrays.ContainsKey(resolvedName))
                {
                    await DiagnoseAsync(name.ToValue(), "cannot destroy array variables in this way").ConfigureAwait(false);
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
                if ((!operand.CompoundApplied || convertedReference) && !local && options.ArrayKind != ArrayAssignmentKind.Auto)
                {
                    var kind = options.ArrayKind == ArrayAssignmentKind.Associative ? ArrayKind.Associative : ArrayKind.Indexed;
                    var arrayDeclarationBinding = referentAttributes ?? new VariableBinding(VariableReference.ForName(name), targetVars);
                    if (!arrayDeclarationBinding.Owner.TryDeclareArray(arrayDeclarationBinding.Name, kind, ScalarArrayPromotion.RetainValue))
                    {
                        await DiagnoseAsync((global && reference.Kind == VariableReferenceKind.Name ? sourceName : name).ToValue(), ArrayConversionMessage(kind)).ConfigureAwait(false);
                        continue;
                    }
                }
                if (options.IndexedChange == DeclarationAttributeChange.Set && options.AssociativeChange == DeclarationAttributeChange.Set)
                {
                    await DiagnoseAsync(name.ToValue(), ArrayConversionMessage(ArrayKind.Indexed)).ConfigureAwait(false);
                    continue;
                }

                var elementAttributesApplied = false;
                if (referentElement is { } elementReference && referentAttributes is { } elementAttributes
                    && operand.Value is { } elementValue)
                {
                    if (!await AwaitWordExpansionAsync(AssignReferencedElementAsync(
                            elementReference, elementAttributes, elementValue, operand.Operator, global)).ConfigureAwait(false))
                        break;
                }
                else if (!operand.CompoundApplied)
                {
                    if ((options.Nameref || circularLocalReference) && !localReferenceAssigned)
                    {
                        var target = namerefTarget ?? ShellValue.Empty;
                        Memory.Charge(checked(4L * target.Bytes.Length));
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
                        if (global && options.ArrayKind == ArrayAssignmentKind.Indexed)
                        {
                            // Bash writes element zero through the active binding here, even if that binding is readonly.
                            if (declarationScope.Variables.ResolveNamerefName(sourceName).IsEmpty)
                            {
                                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName),
                                    ShellValue.FromText("`': not a valid identifier")).ConfigureAwait(false);
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
                                await DiagnoseAsync(name.ToValue(), "invalid name reference").ConfigureAwait(false);
                                continue;
                            }
                            writeReference = binding.Reference;
                            writeVariables = writeReference.Kind == VariableReferenceKind.Name ? binding.Owner : commandVars;
                        }
                        else if (options.Builtin == BuiltinKind.Export && options.ExportChange == DeclarationAttributeChange.Clear
                            && (prefixNames?.Contains(name) == true) && !(bindings?.SavedVariables ?? declarationScope.Variables).TryGetNameref(name, out _))
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
                                    await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(value), "not a valid identifier").ConfigureAwait(false);
                                if (options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly)
                                {
                                    // Attribute-setting also diagnoses the unresolved name; export -n only fails.
                                    exitCode = 1;
                                    if (options.ExportChange != DeclarationAttributeChange.Clear)
                                        await DiagnoseAsync(ShellArgumentEscaping.QuoteDiagnosticOperand(ShellValue.Empty), "not a valid identifier").ConfigureAwait(false);
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
                                await DiagnoseAsync(name.ToValue(), "invalid name reference").ConfigureAwait(false);
                                continue;
                            }
                            SetAttributes(attributeName, targetVars);
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
                                writeScope with { Variables = writeVariables }, value, operand.Operator,
                                declarationWrite, local && reference.Kind != VariableReferenceKind.Name ? sourceName : declarationName).ConfigureAwait(false))
                        {
                            exitCode = 1;
                            if (HasPendingExpansionError) break;
                            continue;
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
                        await DiagnoseAsync(declarationName.ToValue(), "readonly variable").ConfigureAwait(false);
                        continue;
                    }
                    if (formerTarget.IsEmpty || local) targetVars.TryDeclareName(name);
                    else targetVars.TrySetScalar(name, formerTarget);
                }
                if (local && options.Nameref && operand.Value == null && prefixNames?.Contains(name) == true)
                    continue; // A valueless -n on a prefix does not create a function local.
                var attributeBinding = referentAttributes
                    ?? (local && !options.Nameref && targetVars.TryResolveBinding(name, null, out var localBinding)
                        ? localBinding : new VariableBinding(VariableReference.ForName(name), targetVars));
                if (referentElement == null
                    && (global || reference.Kind == VariableReferenceKind.Name || bindings?.Contains(attributeBinding.Name) != true))
                {
                    if (elementAttributesApplied)
                        PublishAttributes(attributeBinding.Name, attributeBinding.Owner, global, local);
                    else
                        await ApplyAttributesAsync(attributeBinding.Name, attributeBinding.Owner, global, local).ConfigureAwait(false);
                }
            }

            return await FinalizeResultAsync(EvaluationResult.FromExitCode(exitCode), declarationNegated).ConfigureAwait(false);

            async Task<bool> AssignReferencedElementAsync(
                VariableReference reference, VariableBinding attributes, ShellValue value, AssignmentOperator operation, bool global)
            {
                // Attributes select the declaration binding; element writes use the active caller array.
                if (global && bindings?.Contains(attributes.Name) == true)
                    attributes = attributes with { Owner = bindings.SavedVariables };
                if (attributes.Owner.IsReadonly(attributes.Name))
                {
                    await DiagnoseAsync(attributes.Name.ToValue(), "readonly variable").ConfigureAwait(false);
                    return true; // Visit later operands, and still detach +n, without evaluating the selector.
                }
                SetAttributes(attributes.Name, attributes.Owner);

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
                else if (!await TryAssignDeclarationValueAsync(target, reference, declarationScope with { Variables = variables },
                    value, operation, WordExpansion.DeclarationWrite.Ordinary, reference.Name).ConfigureAwait(false))
                    exitCode = 1;
                return true;
            }

            async Task<bool> TryAssignDeclarationValueAsync(
                WordExpansion.ResolvedVariableReference target, VariableReference reference, ScopedEvaluation writeScope,
                ShellValue value, AssignmentOperator operation, WordExpansion.DeclarationWrite declarationWrite, BindingName diagnosticName)
            {
                if (declarationWrite != WordExpansion.DeclarationWrite.ActiveGlobalIndexed && writeScope.Variables.IsReadonly(target.Name))
                {
                    if (reference.Kind != VariableReferenceKind.Name)
                    {
                        // A newly applied -r rejects the element write but leaves the declaration successful.
                        await ReportExpansionDiagnosticAsync(diagnosticName.ToValue(), ShellValue.FromText("readonly variable"))
                            .ConfigureAwait(false);
                        return true;
                    }
                    await DiagnoseAsync(diagnosticName.ToValue(), "readonly variable").ConfigureAwait(false);
                    return false;
                }
                return target.TryAssign(writeScope, reference.Name, value, operation, declarationWrite);
            }

            async Task ApplyAttributesAsync(BindingName name, Variables variables, bool global, bool local)
            {
                var attributeName = options.Nameref || local ? name : variables.ResolveNamerefName(name);
                if (attributeName.IsEmpty)
                {
                    // Neither an unset reference nor an element provides a whole binding to mark.
                    if (options.Builtin is BuiltinKind.Export or BuiltinKind.Readonly
                        && options.ExportChange != DeclarationAttributeChange.Clear)
                    {
                        var subject = variables.TryResolveBinding(name, selectOwner: null, out var binding)
                            && binding.Reference.Kind != VariableReferenceKind.Name
                            ? binding.Reference.ToValue(Memory) : ShellValue.Empty;
                        await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName),
                            WordExpansion.AppendAssignmentValue(ShellArgumentEscaping.QuoteDiagnosticOperand(subject),
                                ShellValue.FromText(": not a valid identifier"), declarationScope)).ConfigureAwait(false);
                    }
                    return;
                }
                SetAttributes(attributeName, variables);
                PublishAttributes(name, variables, global, local);
            }

            void PublishAttributes(BindingName name, Variables variables, bool global, bool local)
            {
                if (global && ReferenceEquals(variables, commandVars))
                {
                    bindings?.Retain(name);
                    // Active fallback attributes can promote this declaration's prefix, not the enclosing call's.
                    if (bindings == null && prefixNames?.Contains(name) == true
                        && (options.ExportChange == DeclarationAttributeChange.Set || options.Readonly))
                        declarationScope.Context.FunctionContext?.Locals.Publish(name, variables);
                }
                if (!global && (local || options.ExportChange == DeclarationAttributeChange.Set || options.Readonly))
                {
                    if (local)
                        declarationScope.Context.FunctionContext?.Locals.Publish(name, variables);
                    if (options.ExportChange == DeclarationAttributeChange.Set || options.Readonly)
                        PromoteAttributeBinding(variables, name, declarationScope.Context, bindings);
                }
            }

            void SetAttributes(BindingName name, Variables variables)
            {
                if (options.ExportChange != DeclarationAttributeChange.Unchanged)
                    variables.SetExported(name, options.ExportChange == DeclarationAttributeChange.Set);
                if (options.Readonly) variables.SetReadonlyBinding(name);
            }

            async Task DiagnoseAsync(ShellValue subject, string message)
            {
                exitCode = 1;
                await ReportExpansionDiagnosticAsync(ShellValue.FromText(commandName), WordExpansion.AppendAssignmentValue(subject, ShellValue.FromText(": " + message), declarationScope)).ConfigureAwait(false);
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

            var opening = assignment.Target.AsSpan().IndexOf((byte)'[');
            Memory.Charge(checked(4L * (opening < 0 ? assignment.Target.Length : opening)));
            if (!VariableReference.TryParse(assignment.Target, out var compoundTarget))
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
            var expanded = await ExpandCompoundArrayItemsAsync(name, items, kind, assignment.Operator, AssignmentContext.Direct, scope).ConfigureAwait(false);
            return expanded != null && await ApplyCompoundArrayItemsAsync(
                name, assignment.Operator, kind, expanded, scope, scope.Variables, compoundTarget.Name).ConfigureAwait(false);
        }
    }

    private sealed record ExpandedArrayItem(
        VariableReference? Target,
        ShellValue ExpandedSubscript,
        ShellValue Value,
        AssignmentOperator Operator,
        bool PrefixWithElementZero);

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

    private CommandResolution ResolveCommandName(ShellValue commandName, string text, CommandLookupMode lookupMode)
    {
        if (lookupMode == CommandLookupMode.Normal
            && _state.Functions.TryGetValue(commandName, out var function))
        {
            return new CommandResolution.Function(function.Definition);
        }

        if (BuiltinClassification.TryResolve(text.AsSpan(), out var builtin))
        {
            return builtin == BuiltinKind.ApplyPatch
                ? new CommandResolution.HostBuiltin(builtin)
                : new CommandResolution.EvaluatorBuiltin(builtin);
        }

        return lookupMode == CommandLookupMode.BuiltinOnly
            ? new CommandResolution.Missing()
            : new CommandResolution.Host();
    }

    /// <summary>Executes completed text operands against the command's temporary bindings.</summary>
    private async Task<EvaluationResult> EvaluateBuiltinAsync(
        BuiltinKind builtin,
        string commandName,
        IReadOnlyList<string> words,
        ScopedEvaluation scope,
        Variables commandVars,
        Variables.CommandBindings? bindings,
        bool negated)
    {
        var function = scope.Context.FunctionContext;
        switch (builtin)
        {
            case BuiltinKind.Pwd:
                return await EvaluateBuiltinPwdAsync(negated).ConfigureAwait(false);
            case BuiltinKind.Cd:
                return await EvaluateBuiltinCdAsync(words, commandVars, bindings, negated)
                    .ConfigureAwait(false);
            case BuiltinKind.Dirs:
                return await EvaluateBuiltinDirsAsync(words, negated)
                    .ConfigureAwait(false);
            case BuiltinKind.Pushd:
                return await EvaluateBuiltinPushdAsync(words, commandVars, bindings, negated)
                    .ConfigureAwait(false);
            case BuiltinKind.Popd:
                return await EvaluateBuiltinPopdAsync(commandVars, bindings, negated).ConfigureAwait(false);
            case BuiltinKind.Caller:
                return await EvaluateBuiltinCallerAsync(scope.Context, negated).ConfigureAwait(false);
            case BuiltinKind.Shift:
                return await EvaluateBuiltinShiftAsync(words, commandVars, negated)
                    .ConfigureAwait(false);
            case BuiltinKind.Colon:
            case BuiltinKind.True:
            case BuiltinKind.False:
                if (BuiltinClassification.TryGetFixedExitCode(builtin, out var fixedExitCode))
                {
                    return await FinalizeResultAsync(EvaluationResult.FromExitCode(fixedExitCode), negated)
                        .ConfigureAwait(false);
                }

                throw new InvalidOperationException($"Builtin '{commandName}' has no fixed exit code.");
            case BuiltinKind.Echo:
            case BuiltinKind.Help:
            case BuiltinKind.Printf:
            case BuiltinKind.Read:
            case BuiltinKind.Mapfile:
            case BuiltinKind.Export:
            case BuiltinKind.Readonly:
            case BuiltinKind.Declare:
            case BuiltinKind.Local:
            case BuiltinKind.Set:
            case BuiltinKind.Source:
            case BuiltinKind.Getopts:
            case BuiltinKind.Return:
            case BuiltinKind.Hash:
            case BuiltinKind.Trap:
                throw new InvalidOperationException("Byte-valued builtin operands reached text dispatch.");
            case BuiltinKind.ApplyPatch:
                throw new InvalidOperationException("Host-backed builtin reached evaluator dispatch.");
            case BuiltinKind.Command:
            case BuiltinKind.Builtin:
            case BuiltinKind.Exec:
                throw new InvalidOperationException($"Builtin '{commandName}' was not prepared before dispatch.");
            default:
                throw new ArgumentOutOfRangeException(nameof(builtin));
        }
    }

    /// <summary>
    /// Opens a lexical redirection scope and records ownership of every descriptor it creates.
    /// Descriptor-variable assignments retain their caller scope even when expansion bindings are isolated.
    /// </summary>
    private async Task<RedirectionScope> OpenRedirectionScopeAsync(
        IReadOnlyList<Redirection> redirections,
        ScopedEvaluation scope,
        ScopedEvaluation descriptorAssignmentScope)
    {
        if (redirections.Count == 0)
            return new RedirectionScope(this, active: false, []);

        var evaluation = new RedirectionEvaluation(redirections, CaptureDescriptorBindings().Fork());
        // Expose the table as setup advances, so later target expansions and their substitutions
        // inherit earlier redirections. Descriptor ownership is finalized after setup succeeds.
        _state.PushRedirectionTable(evaluation.DescriptorTable, evaluation.OpenedDescriptors, evaluation.TouchedDescriptors);
        try
        {
            await evaluation.EvaluateAsync(scope, descriptorAssignmentScope).ConfigureAwait(false);
        }
        catch
        {
            await CloseDescriptorsAsync(ReverseDescriptorsForClose(evaluation.OpenedDescriptors), operationFailed: true).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _state.PopRedirectionTable();
        }

        // Allocation may persist bindings during setup. Compare against the enclosing owner's current bindings.
        var restoredTable = _state.CurrentRedirectionTable
            ?? throw new InvalidOperationException("Redirection setup requires an enclosing descriptor table.");
        var changedDescriptors = new HashSet<LogicalDescriptor>();
        foreach (var logicalDescriptor in evaluation.TouchedDescriptors)
        {
            if (evaluation.DescriptorTable.Resolve(logicalDescriptor) != restoredTable.Resolve(logicalDescriptor)
                || evaluation.DescriptorTable.HasMapping(logicalDescriptor) != restoredTable.HasMapping(logicalDescriptor))
            {
                changedDescriptors.Add(logicalDescriptor);
            }
        }

        _state.PushRedirectionTable(
            evaluation.DescriptorTable,
            evaluation.OpenedDescriptors,
            changedDescriptors);
        return new RedirectionScope(this, active: true, evaluation.DescriptorsToReleaseWhenPreserved);

        static IReadOnlyList<FileDescriptor> ReverseDescriptorsForClose(IReadOnlyList<FileDescriptor> openedDescriptors)
        {
            if (openedDescriptors.Count == 0)
                return [];

            var reversed = new FileDescriptor[openedDescriptors.Count];
            for (var index = 0; index < openedDescriptors.Count; index++)
                reversed[index] = openedDescriptors[openedDescriptors.Count - 1 - index];

            return reversed;
        }
    }

    internal IReadOnlyList<FileDescriptor> CollectDescriptorsAvailableToNestedEvaluation()
    {
        var descriptors = new DescriptorHandleCollector(new List<FileDescriptor>());
        descriptors.Add(_stdIn);
        descriptors.Add(_stdOut);
        descriptors.Add(_stdErr);
        descriptors.Add(GetStdInDescriptor());
        descriptors.Add(GetStdOutDescriptor());
        descriptors.Add(GetStdErrDescriptor());
        descriptors.AddRange(_borrowedDescriptors);
        foreach (var borrowedScope in _subshellBorrowedDescriptorScopes)
        {
            foreach (var descriptor in borrowedScope)
                descriptors.Add(descriptor);
        }

        _state.AddRedirectionHandlesTo(ref descriptors, []);
        return descriptors.Descriptors;
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

        var borrowedDescriptors = new DescriptorHandleCollector(new List<FileDescriptor>());
        borrowedDescriptors.AddRange(_borrowedDescriptors);
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
    internal async Task<OperationError?> CloseDescriptorsAsync(IReadOnlyList<FileDescriptor> descriptors, bool operationFailed)
    {
        var failure = new DescriptorCloseResult(true, null, null);
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

        if (operationFailed)
            return null;
        failure.Exception?.Throw();
        return failure.Error;
    }

    /// <summary>Reports an expected host failure without unwinding shell control flow.</summary>
    internal Task ReportOperationErrorAsync(OperationError error) =>
        ExecuteWriteAsync(GetStdErrDescriptor(), Utf8Text.Encode($"shell: {error.Message}"));

    private async Task<EvaluationResult> ApplyCloseErrorAsync(EvaluationResult result, OperationError? error)
    {
        if (error is not { } failure)
            return result;
        await ReportOperationErrorAsync(failure).ConfigureAwait(false);
        return result.WithExitCode(1);
    }

    internal Task<ShellValue> ExpandWordValueAsync(Word word, ScopedEvaluation scope) =>
        AwaitWordExpansionAsync(WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar));

    /// <summary>Decodes the first unsplit field for existing single-text-field consumers.</summary>
    internal Task<string> ExpandWordAsync(Word word, ScopedEvaluation scope)
    {
        return AwaitWordExpansionAsync(ExpandAndDecodeAsync());

        async Task<string> ExpandAndDecodeAsync() =>
            await DecodeExpandedValueAsync(await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Normal)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<ShellValue>> ExpandToWordsAsync(Word word, ScopedEvaluation scope) =>
        AwaitWordExpansionAsync(ExpandAndFlattenAsync(word, scope));

    /// <summary>Expands byte fields and preflights decoding at text-only consumers.</summary>
    internal Task<IReadOnlyList<string>> ExpandTextWordsAsync(Word word, ScopedEvaluation scope)
    {
        return AwaitWordExpansionAsync(ExpandAndDecodeAsync());

        async Task<IReadOnlyList<string>> ExpandAndDecodeAsync()
        {
            var values = await ExpandAndFlattenAsync(word, scope).ConfigureAwait(false);
            var results = new List<string>(values.Count);
            foreach (var value in values)
            {
                var text = await DecodeExpandedValueAsync(value).ConfigureAwait(false);
                if (_state.HasPendingExpansionError)
                    return [];
                results.Add(text);
            }
            return results;
        }
    }

    private async Task<IReadOnlyList<ShellValue>> ExpandAndFlattenAsync(Word word, ScopedEvaluation scope)
    {
        var expanded = await WordExpansion.ExpandToWordsAsync(word, scope).ConfigureAwait(false);
        return await ExpandExpandedWordsAsync(expanded).ConfigureAwait(false);
    }

    private async Task<string> DecodeExpandedValueAsync(ShellValue value)
    {
        if (_state.HasPendingExpansionError)
            return string.Empty;
        Memory.Charge(checked(4L * value.Bytes.Length));
        if (value.TryGetUtf8Text(out var text))
            return text;

        await ReportFatalExpansionAsync(ShellValue.FromText("shell"),
            ShellValue.FromText("text value must be valid UTF-8"), ExpansionFailureKind.Parameter).ConfigureAwait(false);
        return string.Empty;
    }

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

    private static IReadOnlyList<EnvironmentVariable> BuildExportedEnvironment(
        Variables variables, EvaluationContext context)
    {
        return global::Lokad.Bash.Evaluation.Variables.BuildExportedEnvironment(ExportBindings());

        IEnumerable<VariableBinding> ExportBindings()
        {
            foreach (var binding in variables.ExportedBindings)
                yield return binding;
            if (context.FunctionContext == null && context.GlobalPrefixes == null)
                yield break;

            var functionPrefixes = new HashSet<BindingName>();
            for (var function = context.FunctionContext; function != null; function = function.Enclosing)
                functionPrefixes.UnionWith(function.PrefixNames);
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
                if (function.Locals.ExportSnapshot is { } locals)
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
                    // Removing a local export also hides its saved function-call prefix, but not outer exports.
                    if (callPrefixes?.Contains(binding.Name) != true
                        || visibleOwners.GetValueOrDefault(binding.Name, variables).IsExported(binding.Name))
                        yield return binding;
                }
                foreach (var name in functionPrefixes)
                    if (names.Contains(name))
                        visibleOwners[name] = saved;
            }
        }
    }

    private async Task<IReadOnlyList<ShellValue>> ExpandExpandedWordsAsync(
        IReadOnlyList<ExpandedWord> expanded)
    {
        var results = new List<ShellValue>(expanded.Count);
        foreach (var expandedWord in expanded)
        {
            if (!GlobPattern.RequiresExpansion(expandedWord))
            {
                results.Add(expandedWord.ToValue(Memory));
                continue;
            }

            if (!GlobPattern.TryFromExpandedWord(expandedWord, Memory, out var pattern))
            {
                await ReportFatalExpansionAsync(ShellValue.FromText("shell"),
                    ShellValue.FromText("pathname pattern must be valid UTF-8"), ExpansionFailureKind.Parameter)
                    .ConfigureAwait(false);
                return [];
            }
            var glob = new GlobEvaluation(pattern, _state.CurrentDirectory.Path,
                Memory, _cancellationToken);
            var matches = await glob.EvaluateAsync(this).ConfigureAwait(false);
            foreach (var match in matches)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                // Glob paths are UTF-16; reserve their UTF-8 copies before building argument values.
                Memory.Charge(checked(256L + 4L * (Encoding.UTF8.GetByteCount(match))));
                results.Add(ShellValue.FromText(match));
            }
        }

        return results;
    }

    /// <summary>
    /// Sets a variable in the evaluation state, honoring name references.
    /// </summary>
    internal void SetVariable(BindingName name, ShellValue value) =>
        _vars.SetScalarIgnoringReadonly(name, value);

    /// <summary>Imports a validated definition without invoking it or retaining parent-only readonly attributes.</summary>
    public void SetExportedFunction(FunctionDefinition definition) =>
        _state.SetFunctionBinding(new FunctionBinding(definition, FunctionAttributes.Exported));

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
        _state.SetPersistentDescriptor(descriptor, handle);

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
    internal async Task ReportFatalExpansionAsync(ShellValue name, ShellValue message, ExpansionFailureKind kind)
    {
        if (_state.FatalExpansion.HasValue)
            return;

        SetFatalExpansionError(kind);
        await ReportExpansionDiagnosticAsync(name, message).ConfigureAwait(false);
    }

    internal void SetFatalExpansionError(ExpansionFailureKind kind)
    {
        _state.FatalExpansion ??= new ExpansionFailure(kind, kind == ExpansionFailureKind.NamerefDefault ? 2 : 1);
        _state.SetExpansionError(_state.FatalExpansion.Value.ExitCode);
    }

    /// <summary>Emits an expansion diagnostic without changing the caller's expansion status.</summary>
    internal async Task ReportExpansionDiagnosticAsync(ShellValue name, ShellValue message)
    {
        var scope = _builtinDiagnostics;
        if (scope is { Result.CanAcceptMore: false })
            return;
        var diagnostic = ByteMemoryBuilder.CreateUnaccounted(0);
        diagnostic.Append(name.Bytes).Append(": "u8).Append(message.Bytes).Append((byte)'\n');
        var result = await ExecuteWriteAsync(GetStdErrDescriptor(), diagnostic.ToMemory()).ConfigureAwait(false);
        if (scope != null)
            scope.Result = result;
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

    private ShellPath? ResolvePath(string path)
    {
        if (!RawShellPath.TryParse(path, out var raw)) return null;
        return raw.TryToAbsolute(_state.CurrentDirectory, jail: false, out var absolute) ? absolute : null;
    }

    internal async Task ReportSyntaxErrorAsync(SyntaxError error)
    {
        await ExecuteWriteAsync(GetStdErrDescriptor(), Utf8Text.EncodeLine(error.ToDiagnosticMessage()))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Emits an append operation, retaining downstream closure and any known failure cause.
    /// </summary>
    internal Task<DescriptorWriteResult> ExecuteWriteAsync(FileDescriptor fileDescriptor, ReadOnlyMemory<byte> content) =>
        AwaitOperationAsync(WriteOperation.AsTask(PublishOperation, fileDescriptor, content));

    /// <summary>
    /// Executes file tests and evaluates the aggregated result with a provided evaluator.
    /// </summary>
    private async Task<int> ExecuteFileTestsForEvaluationAsync(
        IReadOnlyList<UnresolvedFileTest> tests,
        Func<IReadOnlyList<bool>, bool> evaluator)
    {
        CheckCancellation();
        var terminalDescriptors = new List<FileDescriptor>();
        var terminalResultIndices = new List<int>();
        var kindPaths = new PathLookupBatch<FilePathLookup>();
        var metadataPaths = new PathLookupBatch<FilePathLookup>();
        var sizePaths = new PathLookupBatch<FilePathLookup>();
        var pathResultIndices = new int[tests.Count];
        Array.Fill(pathResultIndices, -1);
        var comparisons = new List<(int TestIndex, FileTimeComparison Comparison, int Left, int Right)>();
        var identityTests = new List<(int TestIndex, int Left, int Right)>();
        var results = new bool[tests.Count];
        for (var index = 0; index < tests.Count; index++)
        {
            var test = tests[index];
            if (test.Kind.IsTerminal)
            {
                if (LogicalDescriptor.TryParse(test.Operand.AsSpan(), out var descriptor))
                {
                    terminalDescriptors.Add(_state.CurrentRedirectionTable?.Resolve(descriptor) ?? FileDescriptor.Closed);
                    terminalResultIndices.Add(index);
                }
            }
            else if (test.Kind.ComparesIdentity)
            {
                if (test.SecondaryOperand is { } secondary
                    && TryResolveFileOperand(test.Operand, FileLinkFollowing.Follow, out var left) && left is not null
                    && TryResolveFileOperand(secondary, FileLinkFollowing.Follow, out var right) && right is not null)
                    identityTests.Add((index, metadataPaths.Add(left), metadataPaths.Add(right)));
            }
            else if (test.Kind.TimeComparison is { } comparison)
            {
                if (test.SecondaryOperand is { } secondary
                    && TryResolveFileOperand(test.Operand, FileLinkFollowing.Follow, out var left)
                    && TryResolveFileOperand(secondary, FileLinkFollowing.Follow, out var right))
                    comparisons.Add((index, comparison, left is null ? -1 : metadataPaths.Add(left),
                        right is null ? -1 : metadataPaths.Add(right)));
            }
            else
            {
                var following = test.Kind.RequiredKind == FilePathKind.SymbolicLink
                    ? FileLinkFollowing.NoFollow : FileLinkFollowing.Follow;
                if (!TryResolveFileOperand(test.Operand, following, out var path) || path is null)
                    continue;
                var batch = test.Kind.MetadataPredicate is not null ? metadataPaths
                    : test.Kind.IsNonEmpty ? sizePaths : kindPaths;
                pathResultIndices[index] = batch.Add(path);
            }
        }

        // Kind, metadata and exact-size failures remain independent. Hosts validate traversal
        // before fetching content; lexical normalization here would erase symbolic-link semantics.
        var kinds = await ReadPathsAsync(kindPaths, FilePathInfoRequest.KindOnly).ConfigureAwait(false);
        var infos = await ReadPathsAsync(metadataPaths, FilePathInfoRequest.Metadata).ConfigureAwait(false);
        var sizes = await ReadPathsAsync(sizePaths, FilePathInfoRequest.MetadataWithExactSize).ConfigureAwait(false);
        for (var index = 0; index < tests.Count; index++)
        {
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
        foreach (var comparison in comparisons)
            results[comparison.TestIndex] = CompareTimes(comparison.Comparison,
                comparison.Left < 0 ? default : infos[comparison.Left],
                comparison.Right < 0 ? default : infos[comparison.Right]);
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
        if (terminalDescriptors.Count > 0)
        {
            var terminalStatuses = await AwaitOperationAsync(TerminalStatusesOperation.AsTask(PublishOperation, terminalDescriptors))
                .ConfigureAwait(false);
            for (var index = 0; index < terminalDescriptors.Count; index++)
                results[terminalResultIndices[index]] = terminalStatuses[index];
        }
        CheckCancellation();
        return evaluator(results) ? 0 : 1;

        async Task<IReadOnlyList<FilePathResolutionResult>> ReadPathsAsync(
            PathLookupBatch<FilePathLookup> paths, FilePathInfoRequest request)
        {
            if (paths.Paths.Count == 0)
                return Array.Empty<FilePathResolutionResult>();
            var infos = await AwaitOperationAsync(PathInfosOperation.AsTask(PublishOperation, paths.Paths, request))
                .ConfigureAwait(false);
            CheckCancellation();
            return infos;
        }

        bool TryResolveFileOperand(string operand, FileLinkFollowing following, out FilePathLookup? path)
        {
            // An empty operand denotes a missing path, including in time comparisons.
            path = null;
            if (operand.Length == 0)
                return true;
            var prefix = operand.StartsWith('/') ? "" : _state.CurrentDirectory.Path;
            if (!PathResolution.FitsPathLimits(prefix, operand) || !RawShellPath.TryParse(operand, out var raw))
                return false;
            path = new FilePathLookup(raw, _state.CurrentDirectory, following);
            return true;
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
    /// Validates and applies a directory change, emitting directory-stack output when needed.
    /// </summary>
    private async Task<int> ExecuteDirectoryChangeAsync(
        DirectoryChangeKind kind, string target, Variables variables, Variables.CommandBindings? bindings)
    {
        var resolved = ResolvePath(target);
        if (resolved is null) return 1;
        var result = await AwaitOperationAsync(PathInfosOperation.AsTask(PublishOperation,
            [new FilePathLookup(resolved, FileLinkFollowing.Follow)], FilePathInfoRequest.KindOnly)).ConfigureAwait(false);
        if (result[0].Error is not null || result[0].Info.Kind != FilePathKind.Directory)
            return 1;

        var previous = _state.CurrentDirectory;
        _state.ApplyDirectoryChange(kind, resolved);
        UpdateDirectoryVariables(previous, _state.CurrentDirectory, variables, bindings);
        if (kind == DirectoryChangeKind.PushDirectory)
            return (await ExecuteWriteAsync(GetStdOutDescriptor(), _state.FormatDirectoryStack()).ConfigureAwait(false)).IsError ? 1 : 0;

        return 0;
    }

    private static void UpdateDirectoryVariables(
        ShellPath previous, ShellPath current, Variables variables, Variables.CommandBindings? bindings)
    {
        var oldPwd = variables.TryGetScalar("PWD", out var pwd) ? pwd : ShellValue.FromText(previous.Path);
        if (!variables.IsReadonly("OLDPWD"))
        {
            variables.SetScalarIgnoringReadonly("OLDPWD", oldPwd);
            bindings?.Retain("OLDPWD");
        }

        if (!variables.IsReadonly("PWD"))
        {
            variables.SetScalarIgnoringReadonly("PWD", ShellValue.FromText(current.Path));
            bindings?.Retain("PWD");
        }
    }

    /// <summary>
    /// Emits a bounded line-read operation on a borrowed descriptor and returns its result.
    /// </summary>
    /// <param name="destinationBudget">Owns the copied batch, or null to retain it with the descriptor.</param>
    /// <param name="fileDescriptor">The host descriptor to read.</param>
    /// <param name="limit">The positive maximum number of records to return.</param>
    public Task<ReadResult> ExecuteReadAsync(FileDescriptor fileDescriptor, int limit, IMemoryBudget? destinationBudget) =>
        AwaitOperationAsync(ReadLineOperation.AsTask(PublishOperation, fileDescriptor, limit, destinationBudget, reusableLine: null));


    /// <summary>
    /// Formats an integer exit code as ASCII bytes.
    /// </summary>
    private static string FormatExitCode(int value)
    {
        if (value == 0)
            return "0";

        var negative = value < 0;
        if (value < 0)
            value = -value;

        Span<char> buffer = stackalloc char[12];
        var length = 0;
        while (value > 0)
        {
            buffer[length++] = (char)('0' + (value % 10));
            value /= 10;
        }

        var totalLength = length + (negative ? 1 : 0);
        var result = new char[totalLength];
        var offset = 0;
        if (negative)
        {
            result[offset++] = '-';
        }

        for (var index = length - 1; index >= 0; index--)
        {
            result[offset++] = buffer[index];
        }

        return new string(result);
    }

    /// <summary>
    /// Reads bounded source, then executes complete command units in the caller's context.
    /// </summary>
    private async Task<EvaluationResult> ExecuteSourceAsync(string path, ScopedEvaluation scope)
    {
        var descriptor = (await ExecuteOpenFileAsync(path, FileOpenMode.Read).ConfigureAwait(false)).FileDescriptor;
        if (!descriptor.HasValue)
            return EvaluationResult.FromExitCode(1);

        var sourceMemory = Memory.Budget.CreateScope();
        var definitionVersion = _state.FunctionDefinitionVersion;
        OperationError? closeError = null;
        try
        {
            BoundedReadResult input;
            var readFailed = false;
            Exception? readException = null;
            try
            {
                using var reader = AsyncLineReader.CreateStreaming(
                    (limit, memory, _) => new ValueTask<ReadResult>(ExecuteReadAsync(descriptor.Value, limit, memory)),
                    _cancellationToken, batchSize: 128, sourceMemory);
                input = await reader.TryReadToEndAsync(Parser.MaximumScriptBytes).ConfigureAwait(false);
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
                await ExecuteWriteAsync(GetStdErrDescriptor(), Utf8Text.Encode($"shell: {path}: {closeFailure.Message}")).ConfigureAwait(false);
                return EvaluationResult.FromExitCode(1);
            }
            if (input is BoundedReadResult.Failed)
                return EvaluationResult.FromExitCode(1);
            if (input is BoundedReadResult.TooLarge)
            {
                await ReportSyntaxErrorAsync(new SyntaxError(Parser.ScriptSizeLimitMessage, 0)).ConfigureAwait(false);
                return EvaluationResult.FromExitCode(2);
            }

            var sourceScope = scope with
            {
                Context = new EvaluationContext(scope.Context.LoopDepth, scope.Context.FunctionContext, ReturnTargetKind.Source)
                {
                    GlobalPrefixes = scope.Context.GlobalPrefixes,
                    ErrexitIgnored = scope.Context.ErrexitIgnored
                }
            };
            return await EvaluateCommandUnitsAsync(
                ShellSource.FromBytes(((BoundedReadResult.Complete)input).Content), sourceScope, ScriptExecutionKind.Source)
                .ConfigureAwait(false);
        }
        finally
        {
            // Function syntax can retain the source after return, so keep it charged until execution ends.
            // TODO: Release this charge earlier when no function retains the source after redefinition or unset.
            if (_state.FunctionDefinitionVersion == definitionVersion)
                sourceMemory.Dispose();
        }
    }

    /// <summary>
    /// Emits a run-command operation and returns its execution result.
    /// </summary>
    private Task<CommandExecutionResult> ExecuteRunCommandAsync(CommandInvocation command) =>
        AwaitOperationAsync(RunCommand.AsTask(PublishOperation, command));

    /// <summary>
    /// Emits a create-pipe operation and returns both endpoints or an acquisition failure.
    /// </summary>
    internal Task<PipeCreationResult> ExecuteCreatePipeAsync() =>
        AwaitOperationAsync(CreatePipeOperation.AsTask(PublishOperation));

    /// <summary>
    /// Emits a list-directory operation and applies glob expansion's error policy.
    /// </summary>
    private async Task<IReadOnlyList<DirectoryEntry>> ExecuteListDirectoryAsync(string path)
    {
        if (!RawShellPath.TryParse(path, out var raw)) return [];
        var result = await AwaitOperationAsync(ListDirectoryOperation.AsTask(PublishOperation,
            new FilePathLookup(raw, _state.CurrentDirectory, FileLinkFollowing.Follow))).ConfigureAwait(false);
        // Globs treat expected listing failures as no matches. Other failures still propagate.
        return result.Error is null ? result.Entries : [];
    }

    /// <summary>
    /// Emits an open-file operation and returns the allocated descriptor.
    /// </summary>
    internal Task<FileOpenResult> ExecuteOpenFileAsync(string path, FileOpenMode mode)
    {
        if (!RawShellPath.TryParse(path, out var raw))
            return Task.FromResult(FileOpenResult.Failed(new OperationError("Invalid file path\n")));
        return AwaitOperationAsync(OpenFileOperation.AsTask(PublishOperation,
            new FilePathLookup(raw, _state.CurrentDirectory, FileLinkFollowing.Follow), mode));
    }

    /// <summary>Transfers an owned descriptor to the host and awaits cleanup without cancellation.</summary>
    private async Task<OperationError?> CloseOwnedDescriptorAsync(FileDescriptor descriptor)
    {
        var completion = CloseDescriptorOperation.AsTask(PublishOperation, descriptor);
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            MarkDescriptorClosed(descriptor);
        }
    }

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
        var content = result.Output.Bytes;
        var nul = content.Span.IndexOf((byte)0);
        if (nul >= 0)
        {
            // Unlike ANSI-C literals and printf assignment, substitution discards every NUL.
            content = ShellValue.FromBytes(content).WithoutNuls().Bytes;
            await ExecuteWriteAsync(
                    GetStdErrDescriptor(),
                    "warning: command substitution: ignored null byte in input\n"u8.ToArray())
                .ConfigureAwait(false);
        }

        var end = content.Length;
        while (end > 0 && content.Span[end - 1] == (byte)'\n')
            end--;
        return ShellValue.FromBytes(content[..end]);
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

    private bool TrySetVariable(BindingName name, ShellValue value, Variables vars) =>
        vars.TrySetScalar(name, value);

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

    /// <summary>Expands a reached command or loop operand once before numeric evaluation.</summary>
    private Task<long?> EvaluateArithmeticCommandOperandAsync(ArithmeticSource expression, ScopedEvaluation scope)
    {
        return AwaitWordExpansionAsync(ExpandAndEvaluateAsync());

        async Task<long?> ExpandAndEvaluateAsync()
        {
            var operand = await WordExpansion.PrepareArithmeticAsync(expression, scope, ExpansionPurpose.ArithmeticSource).ConfigureAwait(false);
            return operand == null ? null : await WordExpansion.EvaluateArithmeticAsync(
                operand, scope, WordExpansion.ArithmeticFailureMode.Diagnostic).ConfigureAwait(false);
        }
    }

    /// <summary>Captures resolved command dispatch and completion semantics.</summary>
    private abstract record PreparedCommand(
        CommandCompletion Completion,
        RedirectionLifetime RedirectionLifetime)
    {
        public sealed record Terminal(
            EvaluationResult Result,
            ShellValue Message,
            CommandMessageChannel Channel,
            SimpleCommand SimpleCommand,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);

        public sealed record Query(
            IReadOnlyList<ShellValue> Arguments,
            CommandQueryOutputMode OutputMode,
            ExecOptions? ExecutionOptions,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);

        public sealed record Executable(
            SimpleCommand SimpleCommand,
            string CommandName,
            CommandResolution Resolution,
            ExecOptions? ExecutionOptions,
            CommandCompletion Completion,
            RedirectionLifetime RedirectionLifetime)
            : PreparedCommand(Completion, RedirectionLifetime);
    }

    private sealed record ExecOptions(ShellValue? ArgumentZero, bool Login, bool ClearEnvironment);

    private enum CommandMessageChannel
    {
        StandardOutput,
        StandardError
    }

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

    private sealed class RedirectionScope(
        Evaluation owner,
        bool active,
        IReadOnlyList<FileDescriptor> descriptorsToReleaseWhenPreserved)
    {
        private bool _active = active;

        public async Task<OperationError?> PreserveAsync()
        {
            if (!_active)
                return null;

            var openedDescriptors = owner._state.PreserveRedirectionTable();
            _active = false;
            var handles = new DescriptorHandleCollector(new List<FileDescriptor>(owner._state.TransferRedirectionDescriptors(openedDescriptors)));
            foreach (var descriptor in descriptorsToReleaseWhenPreserved)
            {
                if (TryTakeOwnedDescriptor(descriptor))
                    handles.Add(descriptor);
            }

            return await owner.CloseDescriptorsAsync(handles.Descriptors, operationFailed: false).ConfigureAwait(false);

            // Take only handles whose lifetime belongs to this evaluator and whose aliases have all been released.
            bool TryTakeOwnedDescriptor(FileDescriptor descriptor)
            {
                if (owner._borrowedDescriptors.Contains(descriptor)
                    || owner._subshellBorrowedDescriptorScopes.Any(scope => scope.Contains(descriptor))
                    || owner._state.ReferencesDescriptor(descriptor))
                {
                    return false;
                }

                return owner._state.TryTakeRedirectionDescriptor(descriptor);
            }
        }

        public async ValueTask<OperationError?> CloseAsync(bool operationFailed)
        {
            if (!_active)
                return null;

            _active = false;
            var openedDescriptors = owner._state.PopRedirectionTable();
            return await owner.CloseDescriptorsAsync(owner._state.TransferRedirectionDescriptors(openedDescriptors), operationFailed).ConfigureAwait(false);
        }

    }

    private void MarkDescriptorClosed(FileDescriptor descriptor) =>
        _pendingDescriptorCleanup.Remove(descriptor);

    private void ScheduleDescriptorCleanup(FileDescriptor descriptor)
    {
        if (!_pendingDescriptorCleanup.Contains(descriptor))
            _pendingDescriptorCleanup.Add(descriptor);
    }

    private abstract record CommandResolution
    {
        public sealed record Function(FunctionDefinition Definition) : CommandResolution;

        public sealed record EvaluatorBuiltin(BuiltinKind Kind) : CommandResolution;

        public sealed record HostBuiltin(BuiltinKind Kind) : CommandResolution;

        public sealed record Host : CommandResolution;

        public sealed record Missing : CommandResolution;
    }

    /// <summary> Describes how a queried command name resolves without executing it. </summary>
    private abstract record CommandQueryResolution
    {
        /// <summary> The name resolves to a shell function. </summary>
        public sealed record Function(FunctionDefinition Definition) : CommandQueryResolution;

        /// <summary> The name resolves to an evaluator-owned or host-backed shell builtin. </summary>
        public sealed record Builtin : CommandQueryResolution;

        /// <summary> The name resolves to a reserved shell keyword. </summary>
        public sealed record Keyword : CommandQueryResolution;

        /// <summary> The name resolves to a command provided by the shell host. </summary>
        /// <param name="ResolvedName">The host-specific name or path reported for the command.</param>
        public sealed record Host(string ResolvedName) : CommandQueryResolution;

        /// <summary> The name does not resolve to any function, builtin, keyword, or host command. </summary>
        public sealed record Missing : CommandQueryResolution;

        /// <summary>The host could not determine command availability.</summary>
        public sealed record Failed(OperationError Error) : CommandQueryResolution;
    }

    private enum CommandQueryOutputMode
    {
        CommandName,
        TypeKind,
        TypeDescription
    }

    private enum CommandLookupMode
    {
        Normal,
        BypassFunctions,
        BuiltinOnly
    }

    private enum RootFlowHandling
    {
        Normalize,
        Preserve
    }

    /// <summary>Carries function identity and its active local-binding scope, including temporary commands.</summary>
    internal sealed class FunctionContext(
        ShellValue functionName, Variables.LocalBindings locals, FunctionContext? enclosing)
    {
        public ShellValue FunctionName { get; } = functionName;
        public FunctionContext? Enclosing { get; } = enclosing;
        public int Depth { get; } = (enclosing?.Depth ?? 0) + 1;

        public Variables.LocalBindings Locals { get; } = locals;
        public Variables.CommandBindings? PrefixBindings { get; init; }
        public IReadOnlySet<BindingName> PrefixNames => PrefixBindings?.PrefixNames ?? NoPrefixNames;
        private static IReadOnlySet<BindingName> NoPrefixNames { get; } = new HashSet<BindingName>();

        /// <summary>Promotion candidates for this scope, filtered before its bindings are restored.</summary>
        public HashSet<BindingName> PromotedNames { get; } = new();

        public bool IsLocal(BindingName name) => Locals.Declares(name) || Enclosing?.IsLocal(name) == true;

        public FunctionContext Fork(Variables variables, GlobalPrefixBindings? prefixes)
        {
            while (prefixes != null && prefixes.FunctionDepth > Depth)
                prefixes = prefixes.Enclosing;
            var enclosing = Enclosing?.Fork(variables, prefixes);
            var callPrefixes = PrefixBindings?.Fork(variables);
            var localPrefixes = prefixes?.FunctionDepth == Depth ? prefixes.Bindings : callPrefixes;
            return new FunctionContext(FunctionName, Locals.Fork(variables, enclosing?.Locals, localPrefixes), enclosing)
            { PrefixBindings = callPrefixes };
        }

    }

    /// <summary>Bindings hidden by eval/source prefixes, independently of function-local declaration rules.</summary>
    internal sealed record GlobalPrefixBindings(
        Variables.CommandBindings Bindings, int FunctionDepth, GlobalPrefixBindings? Enclosing)
    {
        public Variables Saved => Bindings.SavedVariables;
        public IReadOnlySet<BindingName> Names => Bindings.PrefixNames;
        public GlobalPrefixBindings Fork(Variables variables) => new(Bindings.Fork(variables), FunctionDepth, Enclosing?.Fork(variables));
    }

    internal abstract record LocalNamerefAssignment
    {
        internal sealed record Write(VariableBinding Binding) : LocalNamerefAssignment;
        internal sealed record Circular(BindingName? LookupWarning) : LocalNamerefAssignment;
    }

    /// <summary>The nearest construct that permits and consumes return flow.</summary>
    internal enum ReturnTargetKind { None, Function, Source }

    /// <summary>Carries control-flow boundaries independently of function-local binding ownership.</summary>
    internal readonly struct EvaluationContext(int loopDepth, FunctionContext? functionContext, ReturnTargetKind returnTarget)
    {
        public int LoopDepth { get; } = loopDepth;

        public FunctionContext? FunctionContext { get; } = functionContext;
        public ReturnTargetKind ReturnTarget { get; } = returnTarget;
        public GlobalPrefixBindings? GlobalPrefixes { get; init; }
        public bool ErrexitIgnored { get; init; }

        public EvaluationContext WithErrexitIgnored() =>
            new(LoopDepth, FunctionContext, ReturnTarget) { GlobalPrefixes = GlobalPrefixes, ErrexitIgnored = true };

        public EvaluationContext WithLoopDepth(int loopDepth) =>
            new(loopDepth, FunctionContext, ReturnTarget) { GlobalPrefixes = GlobalPrefixes, ErrexitIgnored = ErrexitIgnored };

        /// <summary>Preserves local declaration identity while isolating child bindings and restoration.</summary>
        public EvaluationContext Fork(Variables variables)
        {
            var prefixes = GlobalPrefixes?.Fork(variables);
            return new(LoopDepth, FunctionContext?.Fork(variables, prefixes), ReturnTarget)
                { GlobalPrefixes = prefixes, ErrexitIgnored = ErrexitIgnored };
        }

        public Variables GetGlobalBindingOwner(BindingName name, Variables active)
            => GetBindingOwner(name, active, DeclarationBindingScope.Global, exposedPrefixes: null);

        public Variables GetBindingOwner(BindingName name, Variables active, DeclarationBindingScope lookup,
            List<Variables.CommandBindings>? exposedPrefixes)
            => GetBindingOwner(name, active, lookup, exposedPrefixes, outerBoundary: null);

        private Variables GetBindingOwner(BindingName name, Variables active, DeclarationBindingScope lookup,
            List<Variables.CommandBindings>? exposedPrefixes, FunctionContext? outerBoundary)
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
                    if (function.Locals.ExportSnapshot != null || function.Locals.BlocksPromotion(bindingName))
                        return owner;
                }
                while (prefix != null && prefix.FunctionDepth == function.Depth)
                {
                    UnhidePrefix();
                    if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                        return owner;
                }
                if (lookup == DeclarationBindingScope.Global)
                    owner = function.Locals.HiddenOwner(bindingName, owner);
                if (function.PrefixBindings is { } bindings && bindings.Contains(bindingName))
                {
                    owner = bindings.SavedVariables;
                    exposedPrefixes?.Add(bindings);
                }
                if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName) && !owner.IsUnassignedScalar(bindingName))
                    return owner;
            }
            while (prefix != null && (outerBoundary == null || prefix.FunctionDepth > outerBoundary.Depth))
            {
                UnhidePrefix();
                if (lookup == DeclarationBindingScope.Visible && owner.HasBinding(bindingName))
                    return owner;
            }
            return owner;

            void UnhidePrefix()
            {
                if (prefix is not { } current)
                    return;
                if (current.Names.Contains(bindingName))
                {
                    owner = current.Saved;
                    exposedPrefixes?.Add(current.Bindings);
                }
                prefix = current.Enclosing;
            }
        }

        public bool TryResolveGlobalBinding(BindingName name, Variables active, out VariableBinding binding)
            => TryResolveGlobalBinding(name, active, active, prefixes: null, BindingResolution.Referent, out binding);

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

                Variables CallerOwner(BindingName variable) => context.GetBindingOwner(variable, active,
                    DeclarationBindingScope.Global, exposedPrefixes: null, outerBoundary);
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
