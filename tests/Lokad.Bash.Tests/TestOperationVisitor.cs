using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public sealed class TestOperationVisitor : IBashHost
{
    public Lokad.Bash.Memory.IMemoryBudget MemoryBudget { get; set; } = new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes);

    public IShellTiming Timing { get; init; } = TestTiming.Instance;
    public ShellExecutionContext? Execution => null;

    public IReadOnlyList<CommandLookupResult>? CommandLookupResults { get; init; }

    /// <summary> Stores queued completed-tool exit codes or exceptions to return or throw from RunCommandAsync. </summary>
    private readonly Queue<Func<int>> _commandOutcomes;
    /// <summary>Stores queued pipe completions to return for CreatePipeAsync.</summary>
    private readonly Queue<Task<PipeCreationResult>> _pipeDescriptors;
    /// <summary> Stores queued output outcomes, including known causes, shared by both append APIs. </summary>
    private readonly Queue<ValueTask<DescriptorWriteResult>> _appendResults;
    /// <summary>Stores queued descriptor-open completions, including failures.</summary>
    private readonly Queue<Task<FileOpenResult>> _fileOpenResults;
    /// <summary> Stores queued close completions for CloseDescriptorAsync. </summary>
    private readonly Queue<Task<OperationError?>> _closeDescriptorResults;
    /// <summary> Stores queued read completions, including failures and cancellation. </summary>
    private readonly Queue<Task<ReadResult>> _readResults;
    /// <summary> Stores queued byte reads to return for ReadBytesAsync. </summary>
    private readonly Queue<QueuedByteRead> _byteReads;
    /// <summary> Stores queued directory entries to return for ListDirectoriesAsync. </summary>
    private readonly Queue<DirectoryListingResult> _directoryEntries;
    /// <summary> Stores queued terminal statuses to return for GetTerminalStatusesAsync. </summary>
    private readonly Queue<bool[]> _terminalStatuses;

    private readonly Queue<FilePathResolutionResult> _pathInfos = [];

    public TestOperationVisitor(IReadOnlyList<int> exitCodes)
    {
        if (exitCodes == null)
        {
            throw new ArgumentNullException(nameof(exitCodes));
        }

        _commandOutcomes = new Queue<Func<int>>();
        foreach (var exitCode in exitCodes)
        {
            _commandOutcomes.Enqueue(() => exitCode);
        }
        _pipeDescriptors = new Queue<Task<PipeCreationResult>>();
        _appendResults = new Queue<ValueTask<DescriptorWriteResult>>();
        _fileOpenResults = new Queue<Task<FileOpenResult>>();
        _closeDescriptorResults = new Queue<Task<OperationError?>>();
        _terminalStatuses = new Queue<bool[]>();
        _readResults = new Queue<Task<ReadResult>>();
        _byteReads = new Queue<QueuedByteRead>();
        _directoryEntries = new Queue<DirectoryListingResult>();
    }

    public List<string> CommandNames { get; } = [];

    public Action<CommandInvocation>? BeforeRunCommand { get; set; }

    public Func<Task<CommandExecutionResult>>? RunCommandOverride { get; init; }

    public Action<FileDescriptor>? BeforeDescriptorRead { get; set; }

    public Action<FileDescriptor>? BeforeDescriptorClose { get; set; }

    public List<FileDescriptor> ClosedDescriptors { get; } = [];

    public List<FilePathLookup> PathInfoRequests { get; } = [];

    public List<FilePathLookup> DirectoryListingRequests { get; } = [];

    public List<IReadOnlyList<FileDescriptor>> TerminalStatusBatches { get; } = [];

    public List<RecordedAppend> Appends { get; } = [];

    public bool RecordAppends { get; set; }

    public Action? BeforeReadBytes { get; set; }

    public Func<CancellationToken, ValueTask<DescriptorReadBuffer>>? ReadOverride { get; init; }

    /// <summary>Supplies generated line reads without allocating a queued task per record.</summary>
    public Func<ValueTask<ReadResult>>? ReadLinesOverride { get; init; }

    public Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(IReadOnlyList<FileDescriptor> descriptors, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TerminalStatusBatches.Add(descriptors.ToArray());

        if (_terminalStatuses.Count == 0)
        {
            throw new InvalidOperationException("No terminal statuses are available for this operation.");
        }

        return Task.FromResult<IReadOnlyList<bool>>(_terminalStatuses.Dequeue());
    }

    public Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_pipeDescriptors.Count == 0)
        {
            throw new InvalidOperationException("No pipe descriptors are available for this operation.");
        }

        return _pipeDescriptors.Dequeue();
    }

    public List<IReadOnlyList<FilePathLookup>> PathInfoBatches { get; } = [];

    public async Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(
        IReadOnlyList<FilePathLookup> paths, FilePathInfoRequest request, FileSnapshotRequest snapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FilePathResolutionResult>? delegated = null;
        if (snapshots == FileSnapshotRequest.Capture)
        {
            ResolutionBatches.Add(paths.ToArray());
            delegated = PathResolutions;
            if (delegated == null && FileHost is { } host && _fileOpenResults.Count == 0)
                delegated = await host.ResolvePathsAsync(paths, request, snapshots, cancellationToken);
        }
        else
            PathInfoBatches.Add(paths.ToArray());
        var results = new FilePathResolutionResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            var file = snapshots != FileSnapshotRequest.Capture ? null : delegated?[i].File
                ?? FileSnapshotResult.Success(new PreparedRead(this, paths[i]));
            FilePathResolutionResult result;
            if (snapshots == FileSnapshotRequest.None || _pathInfos.Count > 0)
            {
                PathInfoRequests.Add(paths[i]);
                result = _pathInfos.Dequeue();
                if (result.Error is null && request == FilePathInfoRequest.KindOnly)
                    result = FilePathResolutionResult.Success(FilePathInfo.FromKind(result.Info.Kind));
            }
            else if (delegated is { } batch)
                result = batch[i];
            else
                result = FilePathResolutionResult.Success(FilePathInfo.FromKind(FilePathKind.File));
            results[i] = result with { File = file };
        }
        return results;
    }

    public void EnqueuePathInfo(FilePathInfo info) => _pathInfos.Enqueue(FilePathResolutionResult.Success(info));

    public void EnqueuePathInfoError(OperationError error) => _pathInfos.Enqueue(FilePathResolutionResult.Failure(error));

    public Task<DirectoryCreationResult> CreateDirectoryAsync(FilePathLookup path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryCreationRequests.Add(path);
        return Task.FromResult(DirectoryCreationOverride?.Invoke(path)
            ?? throw new NotSupportedException("TestOperationVisitor does not create directories."));
    }

    public List<FilePathLookup> DirectoryCreationRequests { get; } = [];

    public Func<FilePathLookup, DirectoryCreationResult>? DirectoryCreationOverride { get; init; }

    public Task<OperationError?> DeleteDirectoryAsync(FileEntryPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryDeletionRequests.Add(path);
        return FileHost is MockFileSystem host
            ? host.DeleteDirectoryAsync(path, cancellationToken)
            : Task.FromResult(_directoryDeletionResults.Dequeue());
    }

    public List<FileEntryPath> DirectoryDeletionRequests { get; } = [];
    private readonly Queue<OperationError?> _directoryDeletionResults = [];

    public void EnqueueDirectoryDeletionResult(OperationError? error) => _directoryDeletionResults.Enqueue(error);

    public List<FileEntryPath> DeleteFileRequests { get; } = [];
    private readonly Queue<OperationError?> _deleteFileResults = [];

    public void EnqueueDeleteFileResult(OperationError? error) => _deleteFileResults.Enqueue(error);

    public Task<OperationError?> DeleteFileAsync(FileEntryPath path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteFileRequests.Add(path);
        return FileHost is MockFileSystem host
            ? host.DeleteFileAsync(path, cancellationToken)
            : Task.FromResult(_deleteFileResults.Dequeue());
    }

    public List<(FilePathLookup Source, FilePathLookup Destination)> CopyRequests { get; } = [];
    private readonly Queue<FileTransferError?> _copyResults = [];

    public void EnqueueCopyResult(FileTransferError? error) => _copyResults.Enqueue(error);

    public Task<FileTransferError?> CopyFileAsync(
        FilePathLookup sourcePath,
        FilePathLookup destinationPath,
        FileOverwriteMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopyRequests.Add((sourcePath, destinationPath));
        return FileHost is MockFileSystem host
            ? host.CopyFileAsync(sourcePath, destinationPath, mode, cancellationToken)
            : Task.FromResult(_copyResults.Dequeue());
    }

    public List<(FileEntryPath Source, FileEntryPath Destination, FileOverwriteMode Mode)> RenameRequests { get; } = [];
    private readonly Queue<FileTransferError?> _renameResults = [];

    public void EnqueueRenameResult(FileTransferError? error) => _renameResults.Enqueue(error);

    public Task<FileTransferError?> RenameEntryAsync(FileEntryPath sourcePath, FileEntryPath destinationPath,
        FileOverwriteMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenameRequests.Add((sourcePath, destinationPath, mode));
        return FileHost is MockFileSystem host
            ? host.RenameEntryAsync(sourcePath, destinationPath, mode, cancellationToken)
            : Task.FromResult(_renameResults.Dequeue());
    }

    public Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> commandNames,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CommandLookupResults is { } results
            ? Task.FromResult(results)
            : throw new NotSupportedException("TestOperationVisitor does not resolve commands.");
    }

    public Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command,
        CommandLaunchFailureReporting failureReporting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CommandNames.Add(command.CommandName);
        BeforeRunCommand?.Invoke(command);

        if (RunCommandOverride is { } run)
            return run();

        if (_commandOutcomes.Count == 0)
        {
            throw new InvalidOperationException("No command outcomes are available for this operation.");
        }

        return Task.FromResult<CommandExecutionResult>(
            new CommandExecutionResult.Completed(_commandOutcomes.Dequeue()()));
    }

    public Action<FileDescriptor, ReadOnlyMemory<byte>>? BeforeAppend { get; set; }

    public ValueTask<DescriptorWriteResult> WriteAsync(
        FileDescriptor fileDescriptor,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fileDescriptor.IsDevNull)
            return ValueTask.FromResult(fileDescriptor.IsReadOnlyDevNull ? DescriptorWriteResult.WriteFailure : DescriptorWriteResult.Open);
        if (RecordAppends)
            Appends.Add(new RecordedAppend(fileDescriptor, content.ToArray()));

        BeforeAppend?.Invoke(fileDescriptor, content);

        if (_appendResults.Count == 0)
        {
            return FileHost?.WriteAsync(fileDescriptor, content, cancellationToken)
                ?? throw new InvalidOperationException("No append results are available for this operation.");
        }

        return _appendResults.Dequeue();
    }

    public List<(FilePathLookup Path, WritableFileOpenMode Mode, FileCreationMode CreationMode, FileWriteRetention Retention)> WritableOpenRequests { get; } = [];

    public Task<FileOpenResult> OpenWritableFileAsync(
        FilePathLookup path, WritableFileOpenMode mode, FileCreationMode creationMode, FileWriteRetention retention,
        CancellationToken cancellationToken)
    {
        if (mode is not (WritableFileOpenMode.WriteTruncate or WritableFileOpenMode.WriteAppend or WritableFileOpenMode.ReadWrite))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (creationMode is not (FileCreationMode.OpenOrCreate or FileCreationMode.CreateNew or FileCreationMode.OpenExisting))
            throw new ArgumentOutOfRangeException(nameof(creationMode));
        if (retention is not (FileWriteRetention.AllowInMemory or FileWriteRetention.ReleaseOnClose))
            throw new ArgumentOutOfRangeException(nameof(retention));
        cancellationToken.ThrowIfCancellationRequested();
        WritableOpenRequests.Add((path, mode, creationMode, retention));
        if (path.Path == FileDescriptor.DevNullPath.Path)
            return Task.FromResult(creationMode == FileCreationMode.CreateNew
                ? FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists })
                : FileOpenResult.Opened(FileDescriptor.ForWritableDevNull(mode), readSize: null));

        return _fileOpenResults.Count == 0 && FileHost is { } host
            ? host.OpenWritableFileAsync(path, mode, creationMode, retention, cancellationToken)
            : TakeFileOpenResult();
    }

    private Task<FileOpenResult> TakeFileOpenResult()
    {
        if (_fileOpenResults.Count == 0)
        {
            throw new InvalidOperationException("No open file descriptors are available for this operation.");
        }

        return _fileOpenResults.Dequeue();
    }

    /// <summary>Optional filesystem delegate for tests that override discovery or output.</summary>
    public IBashHost? FileHost { get; init; }

    public IReadOnlyList<FilePathResolutionResult>? PathResolutions { get; init; }

    public List<IReadOnlyList<FilePathLookup>> ResolutionBatches { get; } = [];

    public Task<FileOpenResult> OpenReadAsync(FileSnapshot file, FileReadOpenMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (file is PreparedRead read && read.Owner == this)
            return read.Path.Path == FileDescriptor.DevNullPath.Path
                ? Task.FromResult(FileOpenResult.Opened(FileDescriptor.ForDevNull(FileOpenMode.Read), readSize: 0))
                : TakeFileOpenResult();
        return FileHost?.OpenReadAsync(file, mode, cancellationToken) ?? throw new NotSupportedException();
    }

    private sealed class PreparedRead(TestOperationVisitor owner, FilePathLookup path) : FileSnapshot(null, true)
    {
        public TestOperationVisitor Owner { get; } = owner;
        public FilePathLookup Path { get; } = path;
        public override bool TryGetKnownMd5(Span<byte> destination) => false;
    }

    public Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
    {
        if (descriptor.IsDevNull)
        {
            return Task.FromResult<OperationError?>(null);
        }

        ClosedDescriptors.Add(descriptor);
        BeforeDescriptorClose?.Invoke(descriptor);

        if (_closeDescriptorResults.Count == 0)
            return FileHost?.CloseDescriptorAsync(descriptor)
                ?? throw new InvalidOperationException("No descriptor close outcomes are available for this operation.");

        return _closeDescriptorResults.Dequeue();
    }

    public Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(
        IReadOnlyList<FilePathLookup> paths, DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots,
        DirectoryAccessPattern accessPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new DirectoryListingResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            DirectoryListingRequests.Add(paths[i]);
            if (_directoryEntries.Count == 0)
                throw new InvalidOperationException("No directory entries are available for this operation.");
            results[i] = _directoryEntries.Dequeue();
            if (results[i].Error == null)
            {
                var entries = snapshots == DirectorySnapshotRequest.None
                    ? results[i].Entries.Select(entry => entry with { Snapshot = null }).ToArray()
                    : results[i].Entries;
                results[i] = metadata == DirectoryMetadataRequest.Available && results[i].Metadata is { } available
                    ? DirectoryListingResult.Success(entries, available)
                    : DirectoryListingResult.Success(entries);
            }
        }
        return Task.FromResult<IReadOnlyList<DirectoryListingResult>>(results);
    }

    private readonly Dictionary<FileDescriptor, PendingRead> _pendingReads = [];

    public ValueTask<DescriptorReadBuffer> ReadAsync(
        FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken)
    {
        BeforeDescriptorRead?.Invoke(descriptor);
        BeforeReadBytes?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadOverride is { } read)
            return read(cancellationToken);
        if (descriptor.IsDevNull)
            return ValueTask.FromResult(descriptor.IsWriteOnlyDevNull
                ? DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure) : DescriptorReadBuffer.EndOfFile);
        if (!_pendingReads.TryGetValue(descriptor, out var pending))
            _pendingReads.Add(descriptor, pending = new PendingRead());
        while (!pending.IsReady(request) && _byteReads.Count > 0)
            pending.Append(_byteReads.Dequeue());
        if (pending.IsReady(request))
            return ValueTask.FromResult(pending.Borrow(request.MaximumBytes));
        if (!pending.HasValue && _readResults.Count == 0 && ReadLinesOverride == null)
            return FileHost?.ReadAsync(descriptor, request, cancellationToken)
                ?? throw new InvalidOperationException("No read results are available for this operation.");
        return ReadMoreAsync(this, pending, request);

        static async ValueTask<DescriptorReadBuffer> ReadMoreAsync(TestOperationVisitor host, PendingRead pending, DescriptorReadRequest request)
        {
            while (!pending.IsReady(request))
            {
                var task = host._readResults.Count > 0 ? new ValueTask<ReadResult>(host._readResults.Dequeue())
                    : (host.ReadLinesOverride ?? throw new InvalidOperationException("No read results are available."))();
                var result = await task.ConfigureAwait(false);
                pending.Append(new QueuedByteRead(result.Content, result.Error is { } error
                    ? ByteReadResult.Failure(0, error, result.ReachedEoF)
                    : result.Content.IsEmpty ? ByteReadResult.EndOfFile
                    : ByteReadResult.Success(result.Content.Length, result.ReachedEoF)));
            }
            return pending.Borrow(request.MaximumBytes);
        }
    }

    private sealed class PendingRead
    {
        private QueuedByteRead _pending;
        private readonly DescriptorReadBufferOwner _owner;
        public bool HasValue { get; private set; }

        public PendingRead()
        {
            _owner = new(Commit, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

            void Commit(int count)
            {
                if (count == _pending.Content.Length)
                {
                    HasValue = false;
                    return;
                }
                var remainder = _pending.Content[count..];
                var result = _pending.Result.Error is { } error
                    ? ByteReadResult.Failure(remainder.Length, error, _pending.Result.ReachedEoF)
                    : ByteReadResult.Success(remainder.Length, _pending.Result.ReachedEoF);
                _pending = new QueuedByteRead(remainder, result);
            }
        }

        public bool IsReady(DescriptorReadRequest request) => HasValue
            && (_pending.Content.Length >= request.MinimumBytes || _pending.Result.ReachedEoF || _pending.Result.IsError);

        public void Append(QueuedByteRead read)
        {
            if (HasValue && !_pending.Content.IsEmpty)
            {
                var content = new byte[_pending.Content.Length + read.Content.Length];
                _pending.Content.CopyTo(content);
                read.Content.CopyTo(content.AsMemory(_pending.Content.Length));
                read = new QueuedByteRead(content, read.Result.Error is { } error
                    ? ByteReadResult.Failure(content.Length, error, read.Result.ReachedEoF)
                    : ByteReadResult.Success(content.Length, read.Result.ReachedEoF));
            }
            _pending = read;
            HasValue = true;
        }

        public DescriptorReadBuffer Borrow(int maxBytes)
        {
            var length = Math.Min(maxBytes, _pending.Content.Length);
            var final = length == _pending.Content.Length;
            var result = final ? _pending.Result : ByteReadResult.Success(length, reachedEoF: false);
            return _owner.Borrow(_pending.Content[..length], result, canRetain: true);
        }
    }

    public void EnqueueCommandExitCode(int exitCode) => _commandOutcomes.Enqueue(() => exitCode);

    public void EnqueueCommandException(Exception exception)
    {
        _commandOutcomes.Enqueue(() => throw exception);
    }

    public void EnqueuePipeDescriptors(PipeDescriptors descriptors)
    {
        EnqueuePipeResult(PipeCreationResult.Created(descriptors));
    }

    public void EnqueuePipeResult(PipeCreationResult result) => EnqueuePipeTask(Task.FromResult(result));

    public void EnqueuePipeTask(Task<PipeCreationResult> completion) => _pipeDescriptors.Enqueue(completion);

    public void EnqueueAppendResult(DescriptorWriteResult result) => _appendResults.Enqueue(ValueTask.FromResult(result));

    public void EnqueueAppendTask(Task<DescriptorWriteResult> completion) => _appendResults.Enqueue(new(completion));

    public void EnqueueOpenFileDescriptor(int descriptor)
    {
        EnqueueFileOpenResult(FileOpenResult.Opened(new FileDescriptor(descriptor), readSize: null));
    }

    public void EnqueueFileOpenResult(FileOpenResult result) => EnqueueFileOpenTask(Task.FromResult(result));

    public void EnqueueFileOpenTask(Task<FileOpenResult> completion) => _fileOpenResults.Enqueue(completion);

    public void EnqueueCloseDescriptor()
    {
        _closeDescriptorResults.Enqueue(Task.FromResult<OperationError?>(null));
    }

    public void EnqueueCloseDescriptorResult(OperationError error) =>
        _closeDescriptorResults.Enqueue(Task.FromResult<OperationError?>(error));

    public void EnqueueCloseDescriptorTask(Task closeTask)
    {
        _closeDescriptorResults.Enqueue(AwaitCloseAsync());

        async Task<OperationError?> AwaitCloseAsync()
        {
            await closeTask.ConfigureAwait(false);
            return null;
        }
    }

    public void EnqueueCloseDescriptorException(Exception exception) =>
        EnqueueCloseDescriptorTask(Task.FromException(exception));

    public void EnqueueReadResult(ReadResult result)
    {
        _readResults.Enqueue(Task.FromResult(result));
    }

    public void EnqueueReadTask(Task<ReadResult> result) =>
        _readResults.Enqueue(result);

    public void EnqueueByteReadResult(
        ReadOnlyMemory<byte> content,
        ByteReadResult result)
    {
        if (content.Length != result.BytesRead)
            throw new ArgumentException("The queued content must match the result byte count.", nameof(content));

        _byteReads.Enqueue(new QueuedByteRead(content, result));
    }

    public void EnqueueDirectoryEntries(DirectoryEntry[] entries)
    {
        if (entries == null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        _directoryEntries.Enqueue(DirectoryListingResult.Success(entries));
    }

    public void EnqueueDirectoryResult(DirectoryListingResult result) => _directoryEntries.Enqueue(result);

    public void EnqueueTerminalStatuses(bool[] results) => _terminalStatuses.Enqueue(results);

    private readonly record struct QueuedByteRead(
        ReadOnlyMemory<byte> Content,
        ByteReadResult Result);

    public readonly record struct RecordedAppend(
        FileDescriptor Descriptor,
        byte[] Content);
}
