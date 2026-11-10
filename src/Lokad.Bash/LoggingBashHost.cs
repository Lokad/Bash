using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Memory;
using Lokad.Bash.Files;
namespace Lokad.Bash;

/// <summary>
/// Wraps an <see cref="IBashHost"/> and logs every method call with elapsed time.
/// </summary>
public class LoggingBashHost(IBashHost inner) : IBashHost
{
    private readonly IBashHost _inner = inner;

    public IMemoryBudget MemoryBudget => _inner.MemoryBudget;

    public IShellTiming Timing => _inner.Timing;

    public ShellExecutionContext? Execution => _inner.Execution;

    public Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> commandNames,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        CancellationToken cancellationToken) =>
        LogAsync(
            "ResolveCommandsAsync",
            () => _inner.ResolveCommandsAsync(commandNames, currentDirectory, environment, cancellationToken));

    public Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command,
        CommandLaunchFailureReporting failureReporting,
        CancellationToken cancellationToken) =>
        LogAsync("RunCommandAsync", () => _inner.RunCommandAsync(command, failureReporting, cancellationToken));

    public ValueTask<DescriptorWriteResult> WriteAsync(
        FileDescriptor fileDescriptor,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        LogValueTaskAsync(
            "WriteAsync",
            (Inner: _inner, Descriptor: fileDescriptor, Content: content, CancellationToken: cancellationToken),
            static state => state.Inner.WriteAsync(
                state.Descriptor,
                state.Content,
                state.CancellationToken),
            static _ => ValueTask.CompletedTask);

    public Task<FileOpenResult> OpenWritableFileAsync(
        FilePathLookup path, WritableFileOpenMode mode, FileCreationMode creationMode, FileWriteRetention retention,
        CancellationToken cancellationToken) =>
        LogAsync("OpenWritableFileAsync", () => _inner.OpenWritableFileAsync(path, mode, creationMode, retention, cancellationToken),
            DiscardOpenedFileAsync);

    public Task<FileOpenResult> OpenReadAsync(FileSnapshot file, FileReadOpenMode mode, CancellationToken cancellationToken) =>
        LogAsync("OpenReadAsync", () => _inner.OpenReadAsync(file, mode, cancellationToken), DiscardOpenedFileAsync);

    public async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
    {
        var started = Stopwatch.StartNew();
        ExceptionDispatchInfo? loggingFailure = null;
        Log("start");
        // Tracing failures cannot prevent terminal cleanup or replace its exception.
        var result = await ShellDescriptorCleanup.TryCloseOwnedAsync(_inner, descriptor).ConfigureAwait(false);
        Log($"end ({started.Elapsed})");
        result.Exception?.Throw();
        if (result.Error == null)
            loggingFailure?.Throw();
        return result.Error;

        void Log(string message)
        {
            try
            {
                Console.WriteLine($"IBashHost.CloseDescriptorAsync {message}");
            }
            catch (Exception exception)
            {
                loggingFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
    }

    public Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(
        IReadOnlyList<FilePathLookup> paths, DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots,
        DirectoryAccessPattern accessPattern, CancellationToken cancellationToken) =>
        LogAsync("ListDirectoriesAsync", () => _inner.ListDirectoriesAsync(paths, metadata, snapshots, accessPattern, cancellationToken));

    public ValueTask<DescriptorReadBuffer> ReadAsync(
        FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken) =>
        LogValueTaskAsync(
            "ReadAsync",
            (Inner: _inner, Descriptor: descriptor, Request: request, CancellationToken: cancellationToken),
            static state => state.Inner.ReadAsync(state.Descriptor, state.Request, state.CancellationToken),
            static read =>
            {
                read.Dispose();
                return ValueTask.CompletedTask;
            });

    public Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(IReadOnlyList<FileDescriptor> descriptors, CancellationToken cancellationToken) =>
        LogAsync("GetTerminalStatusesAsync", () => _inner.GetTerminalStatusesAsync(descriptors, cancellationToken));

    public Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken)
    {
        return LogAsync("CreatePipeAsync", () => _inner.CreatePipeAsync(lifetimeToken, cancellationToken), DiscardPipeAsync);

        async ValueTask DiscardPipeAsync(PipeCreationResult result)
        {
            if (result.Descriptors is { } pipe)
            {
                _ = await ShellDescriptorCleanup.TryCloseOwnedAsync(_inner, pipe.Read).ConfigureAwait(false);
                _ = await ShellDescriptorCleanup.TryCloseOwnedAsync(_inner, pipe.Write).ConfigureAwait(false);
            }
        }
    }

    public Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(
        IReadOnlyList<FilePathLookup> paths, FilePathInfoRequest request, FileSnapshotRequest snapshots, CancellationToken cancellationToken) =>
        LogAsync("ResolvePathsAsync", () => _inner.ResolvePathsAsync(paths, request, snapshots, cancellationToken));

    private async ValueTask DiscardOpenedFileAsync(FileOpenResult result)
    {
        if (result.FileDescriptor is { } descriptor)
            _ = await ShellDescriptorCleanup.TryCloseOwnedAsync(_inner, descriptor).ConfigureAwait(false);
    }

    protected static Task<T> LogAsync<T>(string name, Func<Task<T>> action) =>
        LogAsync(name, action, static _ => ValueTask.CompletedTask);

    private static async Task<T> LogAsync<T>(string name, Func<Task<T>> action, Func<T, ValueTask> discardResult) =>
        await LogValueTaskAsync(name, action, static action => new ValueTask<T>(action()), discardResult).ConfigureAwait(false);

    private static async ValueTask<T> LogValueTaskAsync<TState, T>(
        string name,
        TState state,
        Func<TState, ValueTask<T>> action,
        Func<T, ValueTask> discardResult)
    {
        var started = Stopwatch.StartNew();
        Console.WriteLine($"IBashHost.{name} start");
        T result;
        try
        {
            result = await action(state).ConfigureAwait(false);
        }
        catch
        {
            try { Console.WriteLine($"IBashHost.{name} end ({started.Elapsed})"); }
            catch { /* Preserve the operation failure. */ }
            throw;
        }
        try
        {
            Console.WriteLine($"IBashHost.{name} end ({started.Elapsed})");
            return result;
        }
        catch
        {
            // Ownership has not transferred to the caller. Release any acquired result,
            // preserving the logging failure even if cleanup also fails.
            try
            {
                await discardResult(result).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
    }
}
