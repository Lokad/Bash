using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>
/// Executes evaluator requests through a Shell host. The operation retains its result
/// channel and execution claim; this layer owns host calls and nested engine execution.
/// </summary>
internal static class ShellOperationDispatcher
{
    public static Task DispatchAsync(
        EvaluationOperation operation,
        IBashHost host,
        CancellationToken cancellationToken)
    {
        operation.ClaimExecution();
        // Pass state to cached static callbacks instead of allocating a closure per request.
        var state = (Host: host, CancellationToken: cancellationToken);
        return operation switch
        {
            WriteOperation append => append.UseResult(state, static (request, state) =>
                state.Host.WriteAsync(request.FileDescriptor, request.Content, state.CancellationToken)),
            CloseDescriptorOperation close => CloseAsync(close, host),
            CreatePipeOperation pipe => pipe.UseResult(state, static (request, state) =>
                state.Host.CreatePipeAsync(lifetimeToken: state.CancellationToken, state.CancellationToken)),
            StartPipeInputOperation input => input.UseResult(state, static (request, state) =>
                new ValueTask<OperationError?>(request.Writer.Start(state.Host, state.CancellationToken))),
            TerminalStatusesOperation terminals => terminals.UseResult(state, static (request, state) =>
                state.Host.GetTerminalStatusesAsync(request.Descriptors, state.CancellationToken)),
            PathInfosOperation paths => paths.UseResult(state, static (request, state) =>
                state.Host.ResolvePathsAsync(request.Paths, request.Request, FileSnapshotRequest.None, state.CancellationToken)),
            ListDirectoryOperation directory => directory.UseResult(state, static (request, state) =>
                state.Host.ListDirectoryAsync(request.Path, state.CancellationToken)),
            OpenFileOperation file => file.UseResult(state, static (request, state) =>
                request.Mode == FileOpenMode.Read
                    ? state.Host.OpenFileAsync(request.Path, state.CancellationToken)
                    : state.Host.OpenWritableFileAsync(request.Path, request.Mode.ToWritable(), FileCreationMode.OpenOrCreate, FileWriteRetention.AllowInMemory, state.CancellationToken)),
            ReadLineOperation read => HandleReadLimitAsync(read.UseResult(state, static (request, state) =>
                request.ReusableLine is { } reusableLine
                    ? DescriptorReader.ReadReusableLineAsync((state.Host, request.FileDescriptor),
                        static (source, read, token) => source.Host.ReadAsync(source.FileDescriptor, read, token),
                        reusableLine, state.CancellationToken)
                    : state.Host.ReadLinesAsync(request.FileDescriptor, request.Limit, request.DestinationBudget,
                        request.SourceDestination, state.CancellationToken))),
            ReadRecordBatchOperation batch => HandleReadLimitAsync(batch.UseResult(state, static (request, state) =>
                DescriptorReader.ReadRecordBatchAsync((state.Host, request.Descriptor),
                    static (source, read, token) => source.Host.ReadAsync(source.Descriptor, read, token),
                    request.Delimiter, request.Limit, request.DestinationBudget, state.CancellationToken))),
            ReadInputOperation input => HandleReadLimitAsync(input.UseResult(state, static (request, state) =>
                ReadInputAsync(request, state.Host, state.CancellationToken))),
            ReadSubstitutionInputOperation input => input.UseResult(state, static (request, state) =>
                state.Host.TryReadAllBytesAsync(request.Descriptor, ByteMemoryBuilder.MaxMemoryBuilderBytes - 1,
                    static _ => { }, request.DestinationBudget, state.CancellationToken)),
            CopySubstitutionInputOperation input => input.UseResult(state, static (request, state) =>
                CopySubstitutionInputAsync(request, state.Host, state.CancellationToken)),
            ResolveCommandsOperation resolve => resolve.UseResult(state, static (request, state) =>
                state.Host.ResolveCommandsAsync(
                    request.CommandNames, request.CurrentDirectory, request.Environment, state.CancellationToken)),
            RunCommand command => command.UseResult(state, static (request, state) =>
                state.Host.RunCommandAsync(request.Command, request.SelectLaunchFailureReporting(), state.CancellationToken)),
            RunPipelineEvaluations pipeline => pipeline.UseResult(state, static (request, state) =>
                ExecutePipelineAsync(request.Stages, state.Host, state.CancellationToken)),
            RunCapturedEvaluation capture => capture.UseResult(state, static (request, state) =>
                ExecuteCaptureAsync(request, state.Host, state.CancellationToken)),
            ReadTimingOperation timing => timing.UseResult(host, static (_, host) =>
                new ValueTask<ShellTimingSnapshot>(host.Timing.Read())),
            _ => throw new InvalidOperationException($"Unsupported evaluation operation: {operation.GetType().Name}.")
        };

        static async ValueTask<DescriptorWriteResult> CopySubstitutionInputAsync(
            CopySubstitutionInputOperation request, IBashHost host, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var buffer = await host.ReadAsync(request.Input, DescriptorReadRequest.UpTo(8192), cancellationToken)
                    .ConfigureAwait(false);
                var content = buffer.Content;
                if (!content.IsEmpty)
                {
                    if (request.Output.IsClosed)
                        return DescriptorWriteResult.Closed;
                    var write = await host.WriteAsync(request.Output, content, cancellationToken).ConfigureAwait(false);
                    buffer.Consume(content.Length);
                    if (!write.CanAcceptMore)
                        return write;
                }
                if (buffer.Result.Error is { } error)
                    return DescriptorWriteResult.Failure(error);
                if (buffer.Result.ReachedEoF)
                    return DescriptorWriteResult.Open;
            }
        }

        static async Task CloseAsync(CloseDescriptorOperation operation, IBashHost host)
        {
            try
            {
                var error = operation.PipeInput is { } input
                    ? await input.CloseAsync(host).ConfigureAwait(false)
                    : await host.CloseDescriptorAsync(operation.Descriptor).ConfigureAwait(false);
                operation.SetResult(error);
            }
            catch (OperationCanceledException exception)
            {
                operation.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                operation.TrySetException(exception);
            }
        }

        static async Task<CapturedEvaluationResult> ExecuteCaptureAsync(
            RunCapturedEvaluation request, IBashHost host, CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var producerFailed = false;
            var producer = RunProducerAsync();
            BoundedReadResult output;
            try
            {
                output = await host.TryReadAllBytesAsync(request.Input, request.MaximumBytes,
                    static _ => { }, request.MemoryBudget, lifetime.Token).ConfigureAwait(false);
                if (output is not BoundedReadResult.Complete)
                    lifetime.Cancel();
            }
            catch (Exception captureFailure)
            {
                // Snapshot the cause before stopping the producer. A provider may report
                // cancellation with its own linked token.
                var canceledByProducer = captureFailure is OperationCanceledException
                    && Volatile.Read(ref producerFailed) && !cancellationToken.IsCancellationRequested;
                var canceledByCaller = captureFailure is OperationCanceledException
                    && !canceledByProducer && cancellationToken.IsCancellationRequested;
                lifetime.Cancel();
                try { await producer.ConfigureAwait(false); }
                catch (Exception) when (canceledByProducer)
                {
                    // Producer failure canceled the pending read. Preserve the actual cause.
                    throw;
                }
                catch { /* Preserve the capture failure after producer cleanup. */ }
                if (canceledByCaller)
                    cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            int exitCode;
            try
            {
                exitCode = await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception) when (
                !cancellationToken.IsCancellationRequested && output is not BoundedReadResult.Complete)
            {
                // Rejection stops the producer; cancellation and cleanup must not hide the capture failure.
                exitCode = 1;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new CapturedEvaluationResult(output, exitCode);

            async Task<int> RunProducerAsync()
            {
                try
                {
                    return await ExecuteStageAsync(request.Stage, host, lifetime.Token, reportFailure: null).ConfigureAwait(false);
                }
                catch
                {
                    // A failed writer close must also wake a reader still waiting for EOF.
                    Volatile.Write(ref producerFailed, true);
                    lifetime.Cancel();
                    throw;
                }
            }
        }

        static async ValueTask<ReadInputResult> ReadInputAsync(
            ReadInputOperation request, IBashHost host, CancellationToken cancellationToken)
        {
            var input = new ReadInputBuffer(request.Content, request.BackslashMode);
            var remaining = request.Count ?? int.MaxValue;
            // Uncounted custom records also bound source bytes skipped by normalization.
            int? sourceRemaining = request.Count.HasValue ? null : ByteMemoryBuilder.MaxMemoryBuilderBytes;
            while (true)
            {
                using var buffer = await host.ReadAsync(request.Descriptor,
                    DescriptorReadRequest.UpTo(DescriptorReader.BufferSize), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = buffer.Content.Span;
                if (buffer.Result.Error is { } error)
                {
                    // Preserve the custom record reader's cursor at the failed physical record.
                    if (request.Count == null && request.Delimiter is { } delimiter)
                    {
                        var stop = bytes.IndexOf(delimiter);
                        buffer.Consume(stop < 0 ? bytes.Length : stop + 1);
                    }
                    return new ReadInputResult(false, input.FieldBackslashMode, error);
                }

                var window = sourceRemaining is { } limit && limit < bytes.Length ? bytes[..limit] : bytes;
                var complete = input.Append(window, request.Delimiter, ref remaining, out var consumed,
                    cancellationToken);
                if (!complete && consumed < bytes.Length)
                    throw new ByteMemoryBuilder.LimitExceededException();
                sourceRemaining -= consumed;
                buffer.Consume(consumed);
                if (complete || buffer.Result.ReachedEoF)
                    return new ReadInputResult(complete, input.FieldBackslashMode, null);
            }
        }

        static async Task HandleReadLimitAsync(Task pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (ByteMemoryBuilder.LimitExceededException)
            {
                // Completion already delivered the failure to evaluation. The builtin reports
                // the limit with its redirections active; retaining it in Engine would override that result.
            }
        }

        static async Task<IReadOnlyList<int>> ExecutePipelineAsync(
            IReadOnlyList<PipelineEvaluationStage> stages,
            IBashHost host,
            CancellationToken cancellationToken)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Exception? firstFailure = null;
            var stopCallbacks = Task.CompletedTask;
            Action<Exception> reportFailure = StopStages;
            var tasks = new Task<int>[stages.Count];
            for (var index = 0; index < stages.Count; index++)
                tasks[index] = ExecuteStageAsync(stages[index], host, lifetime.Token, reportFailure);

            try
            {
                // The nongeneric join avoids retaining a second vector of stage tasks.
                await Task.WhenAll((Task[])tasks).ConfigureAwait(false);
            }
            catch
            {
                try { await stopCallbacks.ConfigureAwait(false); }
                catch (AggregateException) { /* A cancellation callback cannot replace the stage failure. */ }
                if (firstFailure != null)
                    ExceptionDispatchInfo.Capture(firstFailure).Throw();
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }

            var statuses = new int[tasks.Length];
            for (var index = 0; index < statuses.Length; index++)
                statuses[index] = tasks[index].Result;
            return statuses;

            void StopStages(Exception failure)
            {
                if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    return;
                if (Interlocked.CompareExchange(ref firstFailure, failure, null) == null)
                    stopCallbacks = lifetime.CancelAsync();
            }
        }
    }

    private static async Task<int> ExecuteStageAsync(
        PipelineEvaluationStage stage,
        IBashHost host,
        CancellationToken cancellationToken,
        Action<Exception>? reportFailure)
    {
        try
        {
            int? exitCode = null;
            var closeFailure = new DescriptorCloseResult(true, null, null);
            try
            {
                var result = await new Engine().RunAsync(stage.Evaluation, host, stage.Evaluation.Memory.Budget,
                    cancellationToken, reportFailure).ConfigureAwait(false);
                exitCode = result.ExitCode;
            }
            catch (Exception error)
            {
                reportFailure?.Invoke(error);
                throw;
            }
            finally
            {
                foreach (var descriptor in stage.DescriptorsToClose)
                {
                    // The request owns these streams through this awaited close;
                    // cleanup must not acquire new tracking storage after execution.
                    var result = await ShellDescriptorCleanup.TryCloseOwnedAsync(host, descriptor).ConfigureAwait(false);
                    stage.Evaluation.MarkDescriptorCleanupCompleted(descriptor);
                    if (exitCode == 0 && closeFailure.Closed && result.Exception is { } failure)
                        reportFailure?.Invoke(failure.SourceException);
                    closeFailure = DescriptorCloseResult.FirstFailure(closeFailure, result);
                }
            }

            if (exitCode == 0)
            {
                closeFailure.Exception?.Throw();
                if (closeFailure.Error is { } error)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await host.WriteAsync(stage.Evaluation.GetStdErrDescriptor(), Utf8Text.Encode($"shell: {error.Message}"),
                        cancellationToken).ConfigureAwait(false);
                    return 1;
                }
            }
            return exitCode.GetValueOrDefault();
        }
        catch (Exception error)
        {
            reportFailure?.Invoke(error);
            throw;
        }
    }
}
