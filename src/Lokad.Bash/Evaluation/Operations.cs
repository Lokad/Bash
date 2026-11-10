using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary> A request to write bytes to a file descriptor. </summary>
public sealed class WriteOperation(
    TaskCompletionSource<DescriptorWriteResult> tcs,
    FileDescriptor fileDescriptor,
    ReadOnlyMemory<byte> content) : EvaluationOperation<DescriptorWriteResult>(tcs)
{
    public FileDescriptor FileDescriptor { get; } = fileDescriptor;

    public ReadOnlyMemory<byte> Content { get; } = content;

    public static Task<DescriptorWriteResult> AsTask(
        Action<EvaluationOperation> use,
        FileDescriptor fileDescriptor,
        ReadOnlyMemory<byte> content)
    {
        var tcs = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new WriteOperation(tcs, fileDescriptor, content));
        return tcs.Task;
    }
}

/// <summary> A request to close a file descriptor. </summary>
public sealed class CloseDescriptorOperation(
    TaskCompletionSource<OperationError?> tcs,
    FileDescriptor descriptor) : EvaluationOperation<OperationError?>(tcs)
{
    public FileDescriptor Descriptor { get; } = descriptor;

    public static Task<OperationError?> AsTask(Action<EvaluationOperation> use, FileDescriptor descriptor)
    {
        var tcs = new TaskCompletionSource<OperationError?>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new CloseDescriptorOperation(tcs, descriptor));
        return tcs.Task;
    }
}

/// <summary> A request to create a pipe for pipeline execution. </summary>
public sealed class CreatePipeOperation(
    TaskCompletionSource<PipeCreationResult> tcs) : EvaluationOperation<PipeCreationResult>(tcs)
{
    public static Task<PipeCreationResult> AsTask(Action<EvaluationOperation> use)
    {
        var tcs = new TaskCompletionSource<PipeCreationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new CreatePipeOperation(tcs));
        return tcs.Task;
    }
}

/// <summary>A batch of terminal-status queries over borrowed host descriptors.</summary>
public sealed class TerminalStatusesOperation(
    TaskCompletionSource<IReadOnlyList<bool>> tcs,
    IReadOnlyList<FileDescriptor> descriptors)
    : EvaluationOperation<IReadOnlyList<bool>>(tcs)
{
    public IReadOnlyList<FileDescriptor> Descriptors { get; } = descriptors;

    public static Task<IReadOnlyList<bool>> AsTask(
        Action<EvaluationOperation> use,
        IReadOnlyList<FileDescriptor> descriptors)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new TerminalStatusesOperation(tcs, descriptors));
        return tcs.Task;
    }
}

/// <summary>A batch of path lookups for shell predicates and directory changes.</summary>
public sealed class PathInfosOperation(
    TaskCompletionSource<IReadOnlyList<FilePathResolutionResult>> tcs,
    IReadOnlyList<FilePathLookup> paths,
    FilePathInfoRequest request)
    : EvaluationOperation<IReadOnlyList<FilePathResolutionResult>>(tcs)
{
    public IReadOnlyList<FilePathLookup> Paths { get; } = paths;
    public FilePathInfoRequest Request { get; } = request;

    public static Task<IReadOnlyList<FilePathResolutionResult>> AsTask(
        Action<EvaluationOperation> use,
        IReadOnlyList<FilePathLookup> paths,
        FilePathInfoRequest request)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<FilePathResolutionResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new PathInfosOperation(tcs, paths, request));
        return tcs.Task;
    }
}

/// <summary> A request to list directory entries for globbing. </summary>
public sealed class ListDirectoryOperation(
    TaskCompletionSource<DirectoryListingResult> tcs,
    FilePathLookup path) : EvaluationOperation<DirectoryListingResult>(tcs)
{
    public FilePathLookup Path { get; } = path;

    public static Task<DirectoryListingResult> AsTask(
        Action<EvaluationOperation> use,
        FilePathLookup path)
    {
        var tcs = new TaskCompletionSource<DirectoryListingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new ListDirectoryOperation(tcs, path));
        return tcs.Task;
    }
}

/// <summary> A request to open a file for redirection. </summary>
public sealed class OpenFileOperation(
    TaskCompletionSource<FileOpenResult> tcs,
    FilePathLookup path,
    FileOpenMode mode) : EvaluationOperation<FileOpenResult>(tcs)
{
    /// <summary>The preserved traversal and final-link policy for the open.</summary>
    public FilePathLookup Path { get; } = path;

    public FileOpenMode Mode { get; } = mode;

    public static Task<FileOpenResult> AsTask(
        Action<EvaluationOperation> use,
        FilePathLookup path,
        FileOpenMode mode)
    {
        var tcs = new TaskCompletionSource<FileOpenResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new OpenFileOperation(tcs, path, mode));
        return tcs.Task;
    }
}

/// <summary> A request to read up to a caller-specified number of lines from a file descriptor. </summary>
/// <exception cref="ArgumentOutOfRangeException">The requested line limit is not positive.</exception>
/// <param name="destinationBudget">Owns the copied batch when supplied; otherwise the
/// descriptor's owner retains it. Keep this owner alive through dispatch and consumption.</param>
/// <param name="fileDescriptor">The host descriptor to read.</param>
/// <param name="limit">The positive maximum number of records to return.</param>
/// <param name="tcs">The completion channel for this request.</param>
public sealed class ReadLineOperation(
    TaskCompletionSource<ReadResult> tcs,
    FileDescriptor fileDescriptor,
    int limit,
    IMemoryBudget? destinationBudget) : EvaluationOperation<ReadResult>(tcs)
{
    public FileDescriptor FileDescriptor { get; } = fileDescriptor;

    /// <summary>The positive maximum number of records requested from the host.</summary>
    public int Limit { get; } = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    /// <summary>Optional owner of copied input; otherwise the descriptor retains the result.</summary>
    public IMemoryBudget? DestinationBudget { get; } = destinationBudget;

    /// <summary>Optional single-line storage, borrowed until its next dispatch or disposal.</summary>
    internal ByteMemoryBuilder? ReusableLine { get; private init; }

    public static Task<ReadResult> AsTask(
        Action<EvaluationOperation> use,
        FileDescriptor fileDescriptor,
        int limit,
        IMemoryBudget? destinationBudget,
        ByteMemoryBuilder? reusableLine)
    {
        if (reusableLine != null && (limit != 1 || destinationBudget != null))
            throw new ArgumentException("Reusable line storage requires one record and no copied-batch owner.");
        var tcs = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new ReadLineOperation(tcs, fileDescriptor, limit, destinationBudget) { ReusableLine = reusableLine });
        return tcs.Task;
    }

    /// <summary>Rejects batches above the requested line limit before completing the operation.</summary>
    public override void SetResult(ReadResult result)
    {
        var lineCount = result.Content.IsEmpty ? 0 : result.LineStartOffsets.Count + 1;
        if (lineCount > Limit)
            throw new InvalidOperationException($"The read result contains {lineCount} logical lines for a limit of {Limit}.");

        base.SetResult(result);
    }
}

/// <summary> A request to resolve command names through the shell host. </summary>
public sealed class ResolveCommandsOperation(
    TaskCompletionSource<IReadOnlyList<CommandLookupResult>> tcs,
    IReadOnlyList<string> commandNames,
    ShellPath currentDirectory,
    IReadOnlyList<EnvironmentVariable> environment) : EvaluationOperation<IReadOnlyList<CommandLookupResult>>(tcs)
{
    /// <summary> Gets the command names to resolve. </summary>
    public IReadOnlyList<string> CommandNames { get; } = commandNames;

    /// <summary>Gets the working directory for relative command paths, independently of PWD.</summary>
    public ShellPath CurrentDirectory { get; } = currentDirectory;

    /// <summary> Gets the environment to use for command lookup. </summary>
    public IReadOnlyList<EnvironmentVariable> Environment { get; } = environment;

    /// <summary>
    /// Creates a command-resolution operation and returns a task that completes with its results.
    /// </summary>
    public static Task<IReadOnlyList<CommandLookupResult>> AsTask(
        Action<EvaluationOperation> use,
        IReadOnlyList<string> commandNames,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<CommandLookupResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        use(new ResolveCommandsOperation(tcs, commandNames, currentDirectory, environment));
        return tcs.Task;
    }
}

/// <summary>Requests execution of one command with resolved arguments.</summary>
public sealed class RunCommand(
    TaskCompletionSource<CommandExecutionResult> tcs,
    CommandInvocation command) : EvaluationOperation<CommandExecutionResult>(tcs)
{
    /// <summary>Gets the command invocation to execute.</summary>
    public CommandInvocation Command { get; } = command;

    /// <summary>
    /// Creates a <see cref="RunCommand"/> operation and returns a task for its execution result.
    /// </summary>
    public static Task<CommandExecutionResult> AsTask(
        Action<EvaluationOperation> use,
        CommandInvocation command)
    {
        var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new RunCommand(tcs, command));
        return tcs.Task;
    }
}

/// <summary>One isolated evaluator participating in a streaming pipeline.</summary>
public sealed record PipelineEvaluationStage(
    Evaluation Evaluation,
    IReadOnlyList<FileDescriptor> DescriptorsToClose);

/// <summary>Requests concurrent execution of at least two isolated pipeline-stage evaluators.</summary>
public sealed class RunPipelineEvaluations(
    TaskCompletionSource<IReadOnlyList<int>> tcs,
    IReadOnlyList<PipelineEvaluationStage> stages) : EvaluationOperation<IReadOnlyList<int>>(tcs)
{
    public IReadOnlyList<PipelineEvaluationStage> Stages { get; } = stages.Count >= 2
        ? stages
        : throw new ArgumentException("A pipeline requires at least two stages.", nameof(stages));

    public static Task<IReadOnlyList<int>> AsTask(
        Action<EvaluationOperation> use,
        IReadOnlyList<PipelineEvaluationStage> stages)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new RunPipelineEvaluations(tcs, stages));
        return tcs.Task;
    }

    /// <summary>Completes with exactly one status per stage, in source order.</summary>
    public override void SetResult(IReadOnlyList<int> exitCodes)
    {
        if (exitCodes.Count != Stages.Count)
        {
            throw new ArgumentException(
                $"A {Stages.Count}-stage pipeline requires exactly {Stages.Count} statuses.",
                nameof(exitCodes));
        }

        base.SetResult(exitCodes);
    }
}

/// <summary>Captured output and the producer's command status.</summary>
public sealed record CapturedEvaluationResult(BoundedReadResult Output, int ExitCode);

/// <summary>Runs an isolated evaluator while draining its output with a consumer-owned byte limit.</summary>
/// <remarks>Hosting settles both activities and stage cleanup before completing this operation.</remarks>
public sealed class RunCapturedEvaluation(
    TaskCompletionSource<CapturedEvaluationResult> tcs,
    PipelineEvaluationStage stage,
    FileDescriptor input,
    int maximumBytes,
    IMemoryBudget memoryBudget) : EvaluationOperation<CapturedEvaluationResult>(tcs)
{
    public PipelineEvaluationStage Stage { get; } = stage;
    public FileDescriptor Input { get; } = input;
    public int MaximumBytes { get; } = maximumBytes;
    public IMemoryBudget MemoryBudget { get; } = memoryBudget;

    public static Task<CapturedEvaluationResult> AsTask(
        Action<EvaluationOperation> use, PipelineEvaluationStage stage, FileDescriptor input, int maximumBytes,
        IMemoryBudget memoryBudget)
    {
        var tcs = new TaskCompletionSource<CapturedEvaluationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        use(new RunCapturedEvaluation(tcs, stage, input, maximumBytes, memoryBudget));
        return tcs.Task;
    }
}
