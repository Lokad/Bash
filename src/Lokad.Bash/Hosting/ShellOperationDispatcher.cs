using System;
using System.Collections.Generic;
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
            ReadLineOperation read => ReadAsync(read, host, cancellationToken),
            ReadRecordOperation record => record.UseResult(state, static (request, state) =>
                ReadRecordAsync(request, state.Host, state.CancellationToken)),
            ResolveCommandsOperation resolve => resolve.UseResult(state, static (request, state) =>
                state.Host.ResolveCommandsAsync(
                    request.CommandNames, request.CurrentDirectory, request.Environment, state.CancellationToken)),
            RunCommand command => command.UseResult(state, static (request, state) =>
                state.Host.RunCommandAsync(request.Command, CommandLaunchFailureReporting.Host, state.CancellationToken)),
            RunPipelineEvaluations pipeline => pipeline.UseResult(state, static (request, state) =>
                ExecutePipelineAsync(request.Stages, state.Host, state.CancellationToken)),
            RunCapturedEvaluation capture => capture.UseResult(state, static (request, state) =>
                ExecuteCaptureAsync(request, state.Host, state.CancellationToken)),
            ReadTimingOperation timing => timing.UseResult(host, static (_, host) =>
                new ValueTask<ShellTimingSnapshot>(host.Timing.Read())),
            _ => throw new InvalidOperationException($"Unsupported evaluation operation: {operation.GetType().Name}.")
        };

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
                    return await ExecuteStageAsync(request.Stage, host, lifetime.Token).ConfigureAwait(false);
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

        static async ValueTask<ReadRecordResult> ReadRecordAsync(
            ReadRecordOperation request, IBashHost host, CancellationToken cancellationToken)
        {
            using var reader = new DelimitedRecordReader(host, request.Descriptor, request.Delimiter,
                DelimitedRecordStorage.Reusable, request.Memory.Grow, keepBorrow: false, cancellationToken);
            var record = await reader.ReadNextBoundedAsync(request.MaximumBytes, DelimitedRecordOverflow.Stop)
                .ConfigureAwait(false);
            // Return only this record's bytes; do not allocate or retain a full reader buffer per read builtin.
            if (record is { } value)
            {
                const int ArrayHeaderBytes = 32;
                request.Memory.Grow(value.Content.Length + ArrayHeaderBytes);
                record = value with { Content = value.Content.ToArray() };
            }
            return new ReadRecordResult(record, reader.ReadFailure);
        }

        static async Task ReadAsync(ReadLineOperation operation, IBashHost host, CancellationToken cancellationToken)
        {
            try
            {
                await operation.UseResult((Host: host, CancellationToken: cancellationToken), static (request, state) =>
                    request.ReusableLine is { } reusableLine
                        ? DescriptorReader.ReadReusableLineAsync((state.Host, request.FileDescriptor),
                            static (source, read, token) => source.Host.ReadAsync(source.FileDescriptor, read, token),
                            reusableLine, state.CancellationToken)
                        : request.DestinationBudget is { } budget
                        ? state.Host.ReadLinesAsync(request.FileDescriptor, request.Limit, budget, state.CancellationToken)
                        : state.Host.ReadLinesAsync(request.FileDescriptor, request.Limit, state.CancellationToken))
                    .ConfigureAwait(false);
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
            var tasks = new Task<int>[stages.Count];
            for (var index = 0; index < stages.Count; index++)
                tasks[index] = ExecuteStageAsync(stages[index], host, cancellationToken);

            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteStageAsync(
        PipelineEvaluationStage stage,
        IBashHost host,
        CancellationToken cancellationToken)
    {
        Exception? stageException = null;
        var exitCode = 0;
        var closeFailure = new DescriptorCloseResult(true, null, null);
        try
        {
            var result = await new Engine().RunAsync(stage.Evaluation, host, stage.Evaluation.Memory.Budget, cancellationToken).ConfigureAwait(false);
            exitCode = result.ExitCode;
        }
        catch (Exception ex)
        {
            stageException = ex;
            throw;
        }
        finally
        {
            foreach (var descriptor in stage.DescriptorsToClose)
            {
                stage.Evaluation.TrackOwnedDescriptor(descriptor);
                var result = await ShellDescriptorCleanup.TryCloseOwnedAsync(host, descriptor).ConfigureAwait(false);
                stage.Evaluation.MarkDescriptorCleanupCompleted(descriptor);
                closeFailure = DescriptorCloseResult.FirstFailure(closeFailure, result);
            }

            if (stageException == null && exitCode == 0)
                closeFailure.Exception?.Throw();
        }
        if (exitCode == 0 && closeFailure.Error is { } error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await host.WriteAsync(stage.Evaluation.GetStdErrDescriptor(), Utf8Text.Encode($"shell: {error.Message}"), cancellationToken).ConfigureAwait(false);
            return 1;
        }
        return exitCode;
    }
}
