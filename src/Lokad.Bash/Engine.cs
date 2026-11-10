using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Memory;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>
/// Drives evaluation by repeatedly executing operations until completion.
/// </summary>
/// <remarks>
/// The host executes published operations and returns their outcomes to the evaluator.
/// </remarks>
public sealed class Engine
{
    /// <summary>
    /// Runs evaluation until completion or cancellation.
    /// </summary>
    /// <param name="state">The evaluation state.</param>
    /// <param name="visitor">The operation visitor responsible for execution.</param>
    /// <returns>The final status and directory after evaluation and descriptor cleanup.</returns>
    public Task<RunResult> RunAsync(
        global::Lokad.Bash.Evaluation.Evaluation state,
        IBashHost visitor) =>
        RunAsync(state, visitor, CancellationToken.None);

    /// <summary>
    /// Runs evaluation until completion or cancellation.
    /// </summary>
    /// <param name="state">The evaluation state.</param>
    /// <param name="visitor">The operation visitor responsible for execution.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The final status and directory after evaluation and descriptor cleanup.</returns>
    public Task<RunResult> RunAsync(
        global::Lokad.Bash.Evaluation.Evaluation state,
        IBashHost visitor,
        CancellationToken cancellationToken) =>
        RunAsync(state, visitor, visitor.MemoryBudget, cancellationToken);

    /// <summary>Runs with a separate owner for evaluation values; host effects keep their host's owners.</summary>
    /// <remarks>The caller keeps evaluationMemory alive through this call and any retained evaluation values.</remarks>
    public async Task<RunResult> RunAsync(
        global::Lokad.Bash.Evaluation.Evaluation state,
        IBashHost visitor,
        IMemoryBudget evaluationMemory,
        CancellationToken cancellationToken)
    {
        state.ExpansionBudget.MemoryBudget = evaluationMemory;
        using var timing = visitor.Timing.BeginEvaluation();
        // Builtins and argument expansion can wait before any tool command starts.
        // Keep this producer connection through evaluation and descriptor cleanup.
        using var execution = visitor.Execution?.BeginEvaluation(state.GetStdOutDescriptor(), state.GetStdErrDescriptor());
        var actualVisitor = visitor;
        if (state.Options.Verbosity >= 1 && visitor is not LoggingBashHost)
        {
            actualVisitor = new LoggingBashHost(visitor);
        }

        Exception? runFailure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = state.EnsureRunning(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = await state.WaitForNextStepAsync(cancellationToken).ConfigureAwait(false);
                if (operation == null)
                    break;

                try
                {
                    await ShellOperationDispatcher.DispatchAsync(operation, actualVisitor, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (MemoryBudgetExceededException)
                {
                    // Dispatch already delivered the refusal to evaluation. Let the builtin
                    // handle it through its own redirected error descriptor.
                }
            }
        }
        catch (Exception ex)
        {
            runFailure = ex;
            if (state.HasStarted)
                await DrainCleanupAsync().ConfigureAwait(false);
        }

        async Task DrainCleanupAsync()
        {
            while (true)
            {
                EvaluationOperation? operation;
                try
                {
                    operation = await state.WaitForNextStepAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (operation == null)
                    return;

                try
                {
                    await ShellOperationDispatcher.DispatchAsync(operation, actualVisitor, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The operation result has already been returned to the evaluator. Keep draining so
                    // later descriptor-close operations still run before the original failure escapes.
                }
            }
        }

        try
        {
            await state.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            runFailure ??= ex;
        }

        var cleanupFailure = await TryCleanupDescriptorsAsync().ConfigureAwait(false);
        if (runFailure != null)
            ExceptionDispatchInfo.Capture(runFailure).Throw();

        var exitCode = state.LastExitCode ?? 0;
        if (exitCode != 0)
            return new RunResult(exitCode, state.CurrentDirectory);

        cleanupFailure.Exception?.Throw();
        if (cleanupFailure.Error is { } error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await actualVisitor.WriteAsync(state.GetStdErrDescriptor(), Utf8Text.Encode($"shell: {error.Message}"), cancellationToken).ConfigureAwait(false);
            return new RunResult(1, state.CurrentDirectory);
        }

        return new RunResult(exitCode, state.CurrentDirectory);

        async Task<DescriptorCloseResult> TryCleanupDescriptorsAsync()
        {
            var failure = new DescriptorCloseResult(true, null, null);
            foreach (var descriptor in state.GetDescriptorsForCleanup())
            {
                var result = await ShellDescriptorCleanup.CloseAsync(actualVisitor, descriptor, earlierException: null)
                    .ConfigureAwait(false);
                state.MarkDescriptorCleanupCompleted(descriptor);
                failure = DescriptorCloseResult.FirstFailure(failure, result);
            }

            return failure;
        }
    }
}
