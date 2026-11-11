using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public class MockFileSystem : IBashHost, IAsyncDisposable
{
    public virtual async ValueTask DisposeAsync()
    {
        foreach (var descriptor in _openFiles.Keys.ToArray())
            await CloseDescriptorAsync(descriptor).ConfigureAwait(false);
    }
    public Lokad.Bash.Memory.IMemoryBudget MemoryBudget { get; set; } = new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes);

    public IShellTiming Timing { get; init; } = TestTiming.Instance;
    public ShellExecutionContext? Execution => null;

    /// <summary> Stores the root directory node for the in-memory filesystem. </summary>
    private readonly DirectoryNode _root = new(DateTimeOffset.UtcNow, FileTreePermission.ReadWrite);
    /// <summary> Tracks open file descriptors and their associated read state. </summary>
    private readonly Dictionary<FileDescriptor, OpenFileState> _openFiles = new();
    /// <summary> Stores output buffers for descriptors written via WriteAsync. </summary>
    private readonly Dictionary<FileDescriptor, ByteMemoryBuilder> _outputs = new();
    /// <summary> Stores read-open failures keyed by absolute path. </summary>
    private readonly Dictionary<string, OperationError> _readOpenErrors = new(StringComparer.Ordinal);
    /// <summary> Stores the stdin read state when configured. </summary>
    private OpenFileState? _stdinState;
    /// <summary> Tracks the next file descriptor to allocate for opened files. </summary>
    private int _nextDescriptor = 3;

    public List<CommandInvocation> CommandInvocations { get; } = [];

    public int CommandExitCode { get; set; }

    public int OpenFileCount => _openFiles.Count;

    public void ResetOutput() => _outputs.Clear();

    public bool PreparedReadsAreBuffered { get; init; } = true;

    public bool PreparedReadSizeKnown { get; init; } = true;

    public bool FailOpenFileAppends { get; set; }

    public int MaximumOpenFileAppendLength { get; private set; }

    public int AppendCallCount { get; private set; }

    public bool AppendRemainsOpen { get; set; } = true;

    public OperationError? AppendError { get; set; }

    public Action? BeforeRead { get; set; }

    public Action<FileDescriptor>? BeforeDescriptorRead { get; set; }

    public Action<int>? BeforeReadBuffer { get; set; }

    public Func<ShellPath, Task>? AfterReadOpen { get; set; }

    public Action<ShellPath>? BeforeGetPathInfo { get; set; }

    public Func<ShellPath, FilePathInfoRequest, Task>? AfterGetPathInfo { get; set; }

    public Action<ShellPath>? BeforeListDirectory { get; set; }
    public Func<ShellPath, Task>? AfterListDirectory { get; set; }

    public Action<ShellPath>? BeforeDeleteDirectory { get; set; }

    public Action<ShellPath>? BeforeDeleteFile { get; set; }

    public Action<ShellPath>? BeforeFileTransfer { get; set; }

    public Action<ShellPath>? BeforeWritableOpen { get; set; }

    public Action<FileDescriptor, ReadOnlyMemory<byte>, CancellationToken>? BeforeAppend { get; set; }

    public Func<FileEntryPath, FileEntryPath, CancellationToken, FileTransferError?>? RenameEntryError { get; set; }

    public Func<ShellPath, CancellationToken, OperationError?>? DeleteDirectoryError { get; set; }

    public OperationError? DeleteFileError { get; init; }

    public Func<FilePathLookup, DirectoryCreationResult?>? CreateDirectoryOverride { get; set; }

    public HashSet<FileDescriptor> ReadDescriptors { get; } = [];
    public int MaximumReadWindowBytes { get; init; } = int.MaxValue;
    public int ReadCallCount { get; private set; }
    public long ReadBytesConsumed { get; private set; }
    private void RecordConsumption(int count) => ReadBytesConsumed += count;

    public void AddDirectory(string path, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        EnsureAbsolutePath(path);
        EnsureDirectory(path, modifiedAt, permission);
    }

    public void AddFile(string path, string content, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        EnsureAbsolutePath(path);
        var (parent, name) = GetParentAndName(path);
        var directory = EnsureDirectory(parent, modifiedAt);
        directory.Children[name] = new FileNode(Encoding.UTF8.GetBytes(content), modifiedAt ?? DateTimeOffset.UtcNow, permission);
    }

    public void AddFile(string path, byte[] content, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        EnsureAbsolutePath(path);
        var (parent, name) = GetParentAndName(path);
        var directory = EnsureDirectory(parent, modifiedAt);
        directory.Children[name] = new FileNode(content, modifiedAt ?? DateTimeOffset.UtcNow, permission);
    }

    public void AddReadOpenError(string path, string message)
    {
        EnsureAbsolutePath(path);
        _readOpenErrors[path] = new OperationError(message);
    }

    public bool FileExists(string path)
    {
        EnsureAbsolutePath(path);
        return TryResolveNode(path, out var node) && node is FileNode;
    }

    public byte[] GetOutputBytes(FileDescriptor descriptor)
    {
        if (!_outputs.TryGetValue(descriptor, out var bytes))
        {
            return [];
        }

        return bytes.ToMemory().ToArray();
    }

    public byte[] ReadFileBytes(string path)
    {
        EnsureAbsolutePath(path);
        if (!TryResolveNode(path, out var node) || node is not FileNode file)
        {
            throw new InvalidOperationException("File not found.");
        }

        return file.Content.ToArray();
    }

    public void SetStandardInput(string content)
    {
        _stdinState = OpenFileState.ForRead(Encoding.UTF8.GetBytes(content), RecordConsumption);
    }

    public void SetStandardInputBytes(byte[] content)
    {
        _stdinState = OpenFileState.ForRead(content, RecordConsumption);
    }

    public string GetOutput(FileDescriptor descriptor)
    {
        if (!_outputs.TryGetValue(descriptor, out var bytes))
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(bytes.ToMemory().Span);
    }

    public string ReadFile(string path)
    {
        EnsureAbsolutePath(path);
        if (!TryResolveNode(path, out var node) || node is not FileNode file)
        {
            throw new InvalidOperationException("File not found.");
        }

        return Encoding.UTF8.GetString(file.Content);
    }

    public virtual Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> commandNames,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("MockFileSystem does not resolve commands.");

    public virtual Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command,
        CommandLaunchFailureReporting failureReporting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        CommandInvocations.Add(command);
        return Task.FromResult<CommandExecutionResult>(
            new CommandExecutionResult.Completed(CommandExitCode));
    }

    public virtual ValueTask<DescriptorWriteResult> WriteAsync(FileDescriptor fileDescriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeAppend?.Invoke(fileDescriptor, content, cancellationToken);
        if (AppendError is { } error)
            return ValueTask.FromResult(DescriptorWriteResult.Failure(error));

        AppendCallCount++;
        var success = AppendRemainsOpen ? DescriptorWriteResult.Open : DescriptorWriteResult.Closed;
        if (fileDescriptor.IsDevNull)
        {
            return ValueTask.FromResult(fileDescriptor.IsReadOnlyDevNull
                ? DescriptorWriteResult.WriteFailure : success);
        }

        if (_openFiles.TryGetValue(fileDescriptor, out var openFile))
        {
            MaximumOpenFileAppendLength = Math.Max(MaximumOpenFileAppendLength, content.Length);
            if (FailOpenFileAppends)
            {
                return ValueTask.FromResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
            }

            return ValueTask.FromResult(openFile.TryAppend(content)
                ? success
                : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
        }

        if (!_outputs.TryGetValue(fileDescriptor, out var bytes))
        {
            bytes = ByteMemoryBuilder.CreateUnaccounted(256);
            _outputs[fileDescriptor] = bytes;
        }

        bytes.Append(content);
        return ValueTask.FromResult(success);
    }

    public async Task<FileOpenResult> OpenWritableFileAsync(
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
        // This fixture retains its test file content until the fixture is discarded.
        BeforeWritableOpen?.Invoke(new ShellPath(path.Path));
        cancellationToken.ThrowIfCancellationRequested();
        var (resolved, directories) = path.ResolveWithoutSymbolicLinks();
        if (await CheckTraversalAsync(resolved, directories, nullDeviceIsFile: true, cancellationToken) is { } traversalError)
            return FileOpenResult.Failed(traversalError);
        var pathString = resolved.Path;
        if (creationMode == FileCreationMode.CreateNew
            && (resolved.Equals(FileDescriptor.DevNullPath) || TryResolveNode(pathString, out _)))
            return FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists });
        if (string.Equals(pathString, FileDescriptor.DevNullPath.Path, StringComparison.Ordinal))
        {
            return FileOpenResult.Opened(FileDescriptor.ForWritableDevNull(mode), readSize: null);
        }

        if (mode == WritableFileOpenMode.WriteTruncate)
        {
            var (parentPath, name) = GetParentAndName(pathString);
            if (string.IsNullOrEmpty(name))
            {
                return FileOpenResult.Failed(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory });
            }

            if (!TryResolveNode(parentPath, out var parentNode))
                return FileOpenResult.Failed(await MissingPathErrorAsync(resolved, "No such file or directory\n", cancellationToken));
            if (parentNode is not DirectoryNode parent)
                return FileOpenResult.Failed(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory });

            if (parent.Children.TryGetValue(name, out var target) && target is DirectoryNode)
                return FileOpenResult.Failed(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory });
            if (target == null && creationMode == FileCreationMode.OpenExisting)
                return FileOpenResult.Failed(new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound });

            var descriptor = new FileDescriptor(_nextDescriptor++);
            var modifiedAt = DateTimeOffset.UtcNow;
            var file = target as FileNode ?? new FileNode([], modifiedAt, FileTreePermission.ReadWrite);
            file.Content = [];
            file.StatusChangedAt = file.ModifiedAt = modifiedAt;
            parent.Children[name] = file;
            _openFiles[descriptor] = OpenFileState.ForWrite(content =>
            {
                file.Content = content;
                file.StatusChangedAt = file.ModifiedAt = DateTimeOffset.UtcNow;
            });
            return FileOpenResult.Opened(descriptor, readSize: null);
        }

        return FileOpenResult.Failed(new OperationError("Mock filesystem does not support this write mode\n"));
    }

    public List<IReadOnlyList<FilePathLookup>> ResolutionBatches { get; } = [];
    public Action? AfterResolveFiles { get; set; }
    public bool IncludeListingSnapshots { get; init; } = true;
    public bool IncludeListingMetadata { get; init; } = true;

    private async Task<OperationError?> CheckTraversalAsync(
        ShellPath path, IReadOnlyList<ShellPath> directories, bool nullDeviceIsFile, CancellationToken cancellationToken)
    {
        foreach (var directory in directories)
        {
            TryResolveNode(directory.Path, out var parent);
            var blockedByNullDevice = nullDeviceIsFile && FileDescriptor.DevNullPath.IsEqualToOrParentOf(directory);
            if (parent is DirectoryNode && !blockedByNullDevice)
                continue;
            if (parent is null && !blockedByNullDevice)
                return await MissingPathErrorAsync(directory, "No such file or directory\n", cancellationToken);
            return new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory };
        }
        return nullDeviceIsFile && FileDescriptor.DevNullPath.IsParentOf(path)
            ? new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory } : null;
    }

    public async Task<FileOpenResult> OpenReadAsync(FileSnapshot file, FileReadOpenMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (file is not PreparedRead read || read.Owner != this)
            throw new ArgumentException("The read was prepared by another host.", nameof(file));
        if (Equals(read.Path, FileDescriptor.DevNullPath))
            return FileOpenResult.Opened(FileDescriptor.ForDevNull(FileOpenMode.Read), readSize: 0);
        if (_readOpenErrors.TryGetValue(read.Path.Path, out var error))
            return FileOpenResult.Failed(error);
        var descriptor = new FileDescriptor(_nextDescriptor++);
        _openFiles[descriptor] = OpenFileState.ForRead(read.Content, RecordConsumption);
        if (AfterReadOpen is { } afterReadOpen)
            await afterReadOpen(read.Path);
        return FileOpenResult.Opened(descriptor, readSize: read.Size);
    }

    private sealed class PreparedRead(MockFileSystem owner, ShellPath path, byte[] content)
        : FileSnapshot(owner.PreparedReadSizeKnown ? content.Length : null, owner.PreparedReadsAreBuffered)
    {
        public MockFileSystem Owner { get; } = owner;
        public ShellPath Path { get; } = path;
        public byte[] Content { get; } = content;
        public override bool TryGetKnownMd5(Span<byte> destination) => false;
    }

    public virtual Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
    {
        if (descriptor.IsDevNull)
        {
            return Task.FromResult<OperationError?>(null);
        }

        if (_openFiles.Remove(descriptor, out var state))
        {
            state.Close();
            return Task.FromResult<OperationError?>(null);
        }

        return Task.FromResult<OperationError?>(null);
    }

    public List<IReadOnlyList<FilePathLookup>> DirectoryListingBatches { get; } = [];
    public List<DirectoryMetadataRequest> DirectoryMetadataRequests { get; } = [];
    public List<DirectorySnapshotRequest> DirectorySnapshotRequests { get; } = [];

    public async Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(
        IReadOnlyList<FilePathLookup> paths, DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots,
        DirectoryAccessPattern accessPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryListingBatches.Add(paths.ToArray());
        DirectoryMetadataRequests.Add(metadata);
        DirectorySnapshotRequests.Add(snapshots);
        var results = new DirectoryListingResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
            results[i] = await ListDirectoryAsync(paths[i]).ConfigureAwait(false);
        return results;

        async Task<DirectoryListingResult> ListDirectoryAsync(FilePathLookup lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
            if (await CheckTraversalAsync(path, directories, nullDeviceIsFile: true, cancellationToken) is { } error)
                return DirectoryListingResult.Failure(error);
            if (FileDescriptor.DevNullPath.Equals(path))
                return DirectoryListingResult.Failure(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory });
            BeforeListDirectory?.Invoke(path);
            if (!TryResolveNode(path.Path, out var node))
                return DirectoryListingResult.Failure(await MissingPathErrorAsync(path, "No such file or directory\n", cancellationToken));
            if (node is not DirectoryNode directory)
                return DirectoryListingResult.Failure(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory });

            var entries = new List<DirectoryEntry>(directory.Children.Count);
            List<FileMetadata?>? available = metadata == DirectoryMetadataRequest.Available && IncludeListingMetadata ? [] : null;
            foreach (var entry in directory.Children)
            {
                entries.Add(new DirectoryEntry(entry.Key, entry.Value is DirectoryNode ? FilePathKind.Directory : FilePathKind.File)
                {
                    Snapshot = snapshots == DirectorySnapshotRequest.CaptureAvailable && IncludeListingSnapshots && entry.Value is FileNode file
                        ? new PreparedRead(this, path.Child(entry.Key), file.Content) : null
                });
                available?.Add(GetMetadata(entry.Value));
            }
            var result = available == null ? DirectoryListingResult.Success(entries) : DirectoryListingResult.Success(entries, available);
            if (AfterListDirectory is { } afterListDirectory)
                await afterListDirectory(path);
            return result;
        }
    }

    public virtual ValueTask<DescriptorReadBuffer> ReadAsync(
        FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken)
    {
        ReadCallCount++;
        ReadDescriptors.Add(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        BeforeRead?.Invoke();
        BeforeDescriptorRead?.Invoke(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        BeforeReadBuffer?.Invoke(request.MaximumBytes);
        if (descriptor.IsDevNull)
            return ValueTask.FromResult(descriptor.IsWriteOnlyDevNull
                ? DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure) : DescriptorReadBuffer.EndOfFile);
        return ValueTask.FromResult(GetStateForDescriptor(descriptor)?.Read(Math.Min(request.MaximumBytes, Math.Max(request.MinimumBytes, MaximumReadWindowBytes)))
            ?? DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure));
    }

    public Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(IReadOnlyList<FileDescriptor> descriptors, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This mock has no terminal descriptors.
        return Task.FromResult<IReadOnlyList<bool>>(new bool[descriptors.Count]);
    }

    public virtual Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetimeToken, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("MockFileSystem does not create pipes.");
    }

    public List<(IReadOnlyList<FilePathLookup> Paths, FilePathInfoRequest Request)> PathInfoBatches { get; } = [];

    public async Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(
        IReadOnlyList<FilePathLookup> paths, FilePathInfoRequest request, FileSnapshotRequest snapshots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshots == FileSnapshotRequest.Capture)
            ResolutionBatches.Add(paths.ToArray());
        else
            PathInfoBatches.Add((paths.ToArray(), request));
        var results = new FilePathResolutionResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
            results[i] = await GetInfoAsync(paths[i]);
        if (snapshots == FileSnapshotRequest.Capture)
            AfterResolveFiles?.Invoke();
        return results;

        async Task<FilePathResolutionResult> GetInfoAsync(FilePathLookup lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
                if (await CheckTraversalAsync(path, directories, nullDeviceIsFile: true, cancellationToken) is { } traversalError)
                    return (traversalError.Kind == OperationErrorKind.NotFound
                        ? FilePathResolutionResult.Success(FilePathInfo.FromKind(FilePathKind.Missing))
                        : FilePathResolutionResult.Failure(traversalError)) with
                    {
                        File = snapshots == FileSnapshotRequest.Capture ? FileSnapshotResult.Failure(traversalError) : null
                    };
                BeforeGetPathInfo?.Invoke(path);
                cancellationToken.ThrowIfCancellationRequested();
                TryResolveNode(path.Path, out var node);
                var isNullDevice = path.Equals(FileDescriptor.DevNullPath);
                var kind = isNullDevice ? FilePathKind.CharacterDevice : node is DirectoryNode ? FilePathKind.Directory
                    : node is FileNode || _readOpenErrors.ContainsKey(path.Path) ? FilePathKind.File : FilePathKind.Missing;
                if (kind == FilePathKind.Missing
                    && await MissingPathErrorAsync(path, "No such file or directory\n", cancellationToken) is { Kind: not OperationErrorKind.NotFound } missingError)
                    return FilePathResolutionResult.Failure(missingError) with
                    {
                        File = snapshots == FileSnapshotRequest.Capture ? FileSnapshotResult.Failure(missingError) : null
                    };
                var metadata = node is null ? default : GetMetadata(node);
                var info = request == FilePathInfoRequest.KindOnly ? FilePathInfo.FromKind(kind) : new FilePathInfo(kind, metadata);
                var result = FilePathResolutionResult.Success(info) with
                {
                    File = snapshots != FileSnapshotRequest.Capture ? null
                        : isNullDevice ? FileSnapshotResult.Success(new PreparedRead(this, path, []))
                        : node is FileNode sourceFile ? FileSnapshotResult.Success(new PreparedRead(this, path, sourceFile.Content))
                        // Injected open failures still need a snapshot without a backing file.
                        : _readOpenErrors.ContainsKey(path.Path) ? FileSnapshotResult.Success(new PreparedRead(this, path, []))
                        : FileSnapshotResult.NotFile(kind)
                };
                if (AfterGetPathInfo is { } afterGetPathInfo)
                    await afterGetPathInfo(path, request);
                return result;
            }
            catch (Exception exception) when (exception is PermissionDeniedException or MissingPathException)
            {
                var error = OperationError.FromException(exception);
                return FilePathResolutionResult.Failure(error) with
                {
                    File = snapshots == FileSnapshotRequest.Capture ? FileSnapshotResult.Failure(error) : null
                };
            }
        }
    }

    private static FileMetadata GetMetadata(Node node)
    {
        var access = node.Permission == FileTreePermission.ReadOnly
            ? FileAccessRights.Read | FileAccessRights.Execute : FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute;
        return node switch
        {
            FileNode file => new FileMetadata(file.Content.Length, file.ModifiedAt, access, file.StatusChangedAt,
                accessedAt: null, specialModeBits: FileSpecialModeBits.None, ownershipMatch: null, identity: null),
            DirectoryNode directory => new FileMetadata(0, directory.ModifiedAt, access, directory.StatusChangedAt,
                accessedAt: null, specialModeBits: FileSpecialModeBits.None, ownershipMatch: null, identity: null),
            _ => default
        };
    }

    public async Task<DirectoryCreationResult> CreateDirectoryAsync(FilePathLookup path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateDirectoryOverride?.Invoke(path) is { } result)
            return result;

        var resolved = path.ResolveDirectoryCreationWithoutSymbolicLinks();
        if (await CheckTraversalAsync(resolved.Path, resolved.RequiredDirectories, nullDeviceIsFile: false, cancellationToken) is { } error)
            return new DirectoryCreationResult.Failed(error);

        var (parentPath, name) = GetParentAndName(resolved.Path.Path);
        if (string.IsNullOrEmpty(name))
        {
            return new DirectoryCreationResult.AlreadyExistsAsDirectory();
        }

        if (!TryResolveNode(parentPath, out var parentNode))
        {
            return new DirectoryCreationResult.Failed(await MissingPathErrorAsync(new ShellPath(parentPath),
                "No such file or directory\n", cancellationToken));
        }

        if (parentNode is not DirectoryNode parent)
            return new DirectoryCreationResult.Failed(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory });

        if (parent.Children.TryGetValue(name, out var existing))
        {
            return existing is DirectoryNode
                ? new DirectoryCreationResult.AlreadyExistsAsDirectory()
                : new DirectoryCreationResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists });
        }

        if (parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
            return new DirectoryCreationResult.Failed(new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied });

        var timestamp = DateTimeOffset.UtcNow;
        parent.Children[name] = new DirectoryNode(timestamp, FileTreePermission.ReadWrite) { StatusChangedAt = timestamp };
        return new DirectoryCreationResult.Created();
    }

    public async Task<OperationError?> DeleteDirectoryAsync(FileEntryPath lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
        if (await CheckTraversalAsync(path, directories, nullDeviceIsFile: false, cancellationToken) is { } error)
            return error;
        if (lookup.HasFinalDotComponent)
            return new OperationError("Cannot delete '.' or '..' directory entries\n") { Kind = OperationErrorKind.PermissionDenied };
        BeforeDeleteDirectory?.Invoke(path);
        if (DeleteDirectoryError?.Invoke(path, cancellationToken) is { } failure)
            return failure;

        var (parentPath, name) = GetParentAndName(path.Path);
        if (string.IsNullOrEmpty(name))
        {
            return new OperationError("Operation failed.\n") { Kind = OperationErrorKind.PermissionDenied };
        }

        if (!TryResolveNode(parentPath, out var parentNode) || parentNode is not DirectoryNode parent)
        {
            return await MissingPathErrorAsync(path, "No such file or directory\n", cancellationToken);
        }

        if (!parent.Children.TryGetValue(name, out var node))
        {
            return new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound };
        }

        if (node is not DirectoryNode directory)
            return new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory };

        if (directory.Permission == FileTreePermission.ReadOnly)
            return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };
        if (parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
            return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };

        if (directory.Children.Count > 0)
        {
            return new OperationError("Directory not empty\n") { Kind = OperationErrorKind.DirectoryNotEmpty };
        }

        parent.Children.Remove(name);
        return null;
    }

    public async Task<OperationError?> DeleteFileAsync(FileEntryPath lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
        if (await CheckTraversalAsync(path, directories, nullDeviceIsFile: false, cancellationToken) is { } traversalError)
            return traversalError;
        BeforeDeleteFile?.Invoke(path);
        if (DeleteFileError is { } error)
            return error;

        var (parentPath, name) = GetParentAndName(path.Path);
        if (string.IsNullOrEmpty(name))
        {
            return new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory };
        }

        if (!TryResolveNode(parentPath, out var parentNode))
        {
            return await MissingPathErrorAsync(path, "No such file or directory\n", cancellationToken);
        }
        if (parentNode is not DirectoryNode parent)
            return new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory };

        if (!parent.Children.TryGetValue(name, out var node))
        {
            return new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound };
        }
        if (node is not FileNode file)
            return new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory };
        if (file.Permission == FileTreePermission.ReadOnly)
            return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };
        if (parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
            return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };

        parent.Children.Remove(name);
        return null;
    }

    private async Task<OperationError> MissingPathErrorAsync(ShellPath path, string message, CancellationToken cancellationToken)
    {
        var kind = await PathResolution.ClassifyMissingPathAsync(
            path, static current => current.Parent, GetNamespaceKindAsync, cancellationToken);
        return new OperationError(kind == OperationErrorKind.NotDirectory ? "Not a directory\n" : message) { Kind = kind };

        Task<FilePathKind> GetNamespaceKindAsync(ShellPath current, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            TryResolveNode(current.Path, out var node);
            return Task.FromResult(node is DirectoryNode ? FilePathKind.Directory
                : node is FileNode ? FilePathKind.File : FilePathKind.Missing);
        }
    }

    public Task<FileTransferError?> CopyFileAsync(
        FilePathLookup sourceLookup,
        FilePathLookup destinationLookup,
        FileOverwriteMode mode,
        CancellationToken cancellationToken) =>
        TransferAsync(sourceLookup.EntryPath, destinationLookup.EntryPath, mode, TransferKind.Copy, cancellationToken);

    public Task<FileTransferError?> RenameEntryAsync(
        FileEntryPath sourceLookup,
        FileEntryPath destinationLookup,
        FileOverwriteMode mode,
        CancellationToken cancellationToken) =>
        TransferAsync(sourceLookup, destinationLookup, mode, TransferKind.Rename, cancellationToken);

    private enum TransferKind { Copy, Rename }

    private async Task<FileTransferError?> TransferAsync(FileEntryPath sourceLookup, FileEntryPath destinationLookup,
        FileOverwriteMode mode, TransferKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode is not (FileOverwriteMode.ReplaceExisting or FileOverwriteMode.RequireNewDestination))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (kind == TransferKind.Rename)
        {
            if (sourceLookup.HasFinalDotComponent || destinationLookup.HasFinalDotComponent)
                return new FileTransferError(new OperationError("Cannot rename '.' or '..' directory entries\n"),
                    sourceLookup.HasFinalDotComponent && destinationLookup.HasFinalDotComponent ? FileTransferOperand.Operation
                    : sourceLookup.HasFinalDotComponent ? FileTransferOperand.Source : FileTransferOperand.Destination);
            if (RenameEntryError?.Invoke(sourceLookup, destinationLookup, cancellationToken) is { } failure)
                return failure;
        }
        var (sourcePath, sourceDirectories) = sourceLookup.ResolveWithoutSymbolicLinks();
        var (destinationPath, destinationDirectories) = destinationLookup.ResolveWithoutSymbolicLinks();
        if (await CheckTraversalAsync(sourcePath, sourceDirectories, nullDeviceIsFile: false, cancellationToken) is { } sourceError)
            return new FileTransferError(sourceError, FileTransferOperand.Source);
        if (kind == TransferKind.Rename && TryResolveNode(sourcePath.Path, out var sourceEntry) && sourceEntry is DirectoryNode)
            (destinationPath, destinationDirectories) = destinationLookup.ResolveDirectoryCreationWithoutSymbolicLinks();
        if (await CheckTraversalAsync(destinationPath, destinationDirectories, nullDeviceIsFile: false, cancellationToken) is { } destinationError)
            return new FileTransferError(destinationError, FileTransferOperand.Destination);
        BeforeFileTransfer?.Invoke(destinationPath);

        if (!TryResolveNode(sourcePath.Path, out var sourceNode)
            || kind == TransferKind.Copy && sourceNode is not FileNode)
        {
            return new FileTransferError(new OperationError("Operation failed.\n"), FileTransferOperand.Source);
        }

        var (parentPath, name) = GetParentAndName(destinationPath.Path);
        if (string.IsNullOrEmpty(name))
        {
            return new FileTransferError(new OperationError("Operation failed.\n"), FileTransferOperand.Destination);
        }

        if (!TryResolveNode(parentPath, out var parentNode) || parentNode is not DirectoryNode parent)
        {
            return new FileTransferError(new OperationError("Operation failed.\n"), FileTransferOperand.Destination);
        }

        var destination = parent.Children.GetValueOrDefault(name);
        if (destination != null && mode == FileOverwriteMode.RequireNewDestination)
            return new FileTransferError(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists }, FileTransferOperand.Destination);
        if (sourcePath.Equals(destinationPath))
            return new FileTransferError(new OperationError("Source and destination are the same file\n"), FileTransferOperand.Operation);
        if (sourceNode is DirectoryNode && sourcePath.IsEqualToOrParentOf(destinationPath))
            return new FileTransferError(new OperationError("Cannot move a directory to itself or a subdirectory\n"), FileTransferOperand.Operation);
        if (destination is DirectoryNode && sourceNode is not DirectoryNode)
            return new FileTransferError(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory }, FileTransferOperand.Destination);
        if (destination != null && destination is not DirectoryNode && sourceNode is DirectoryNode)
            return new FileTransferError(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory }, FileTransferOperand.Destination);
        if (destination is DirectoryNode { Children.Count: > 0 })
            return new FileTransferError(new OperationError("Directory not empty\n") { Kind = OperationErrorKind.DirectoryNotEmpty }, FileTransferOperand.Destination);
        var permission = destination?.Permission ?? parent.Permission;
        if ((destination != null || parentPath != "/") && permission == FileTreePermission.ReadOnly)
            return new FileTransferError(new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied }, FileTransferOperand.Destination);

        if (kind == TransferKind.Rename)
        {
            var (sourceParentPath, sourceName) = GetParentAndName(sourcePath.Path);
            if (!TryResolveNode(sourceParentPath, out var sourceParentNode) || sourceParentNode is not DirectoryNode sourceParent)
                throw new InvalidOperationException("The resolved source file has no parent directory.");
            if (sourceNode.Permission == FileTreePermission.ReadOnly
                || sourceParentPath != "/" && sourceParent.Permission == FileTreePermission.ReadOnly)
                return new FileTransferError(new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied }, FileTransferOperand.Source);
            if (parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
                return new FileTransferError(new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied }, FileTransferOperand.Destination);
            cancellationToken.ThrowIfCancellationRequested();
            sourceParent.Children.Remove(sourceName);
            parent.Children[name] = sourceNode;
            RecordRename(sourceNode, DateTimeOffset.UtcNow);

            static void RecordRename(Node node, DateTimeOffset statusChangedAt)
            {
                node.StatusChangedAt = statusChangedAt;
                if (node is DirectoryNode directory)
                    foreach (var child in directory.Children.Values)
                        RecordRename(child, statusChangedAt);
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ((FileNode)sourceNode).Content.ToArray();
            if (destination is FileNode target)
            {
                target.Content = content;
                target.StatusChangedAt = target.ModifiedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                var timestamp = DateTimeOffset.UtcNow;
                parent.Children[name] = new FileNode(content, timestamp, FileTreePermission.ReadWrite) { StatusChangedAt = timestamp };
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the open-file state for a descriptor, creating stdin state on demand.
    /// </summary>
    private OpenFileState? GetStateForDescriptor(int descriptor)
    {
        if (descriptor == FileDescriptor.StdIn)
        {
            return _stdinState ??= OpenFileState.ForRead([], RecordConsumption);
        }

        _openFiles.TryGetValue(descriptor, out var state);
        return state;
    }

    /// <summary>
    /// Ensures a directory path exists, creating intermediate directories as needed.
    /// </summary>
    private DirectoryNode EnsureDirectory(string path, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        var parts = SplitPath(path);
        var current = _root;
        foreach (var part in parts)
        {
            if (!current.Children.TryGetValue(part, out var child))
            {
                var directory = new DirectoryNode(modifiedAt ?? DateTimeOffset.UtcNow, permission);
                current.Children.Add(part, directory);
                current = directory;
                continue;
            }

            if (child is DirectoryNode directoryNode)
            {
                current = directoryNode;
                continue;
            }

            throw new InvalidOperationException("A file already exists at this path.");
        }

        return current;
    }

    /// <summary>
    /// Resolves a path to a node, returning false when any segment is missing.
    /// </summary>
    private bool TryResolveNode(string path, out Node node)
    {
        EnsureAbsolutePath(path);
        var parts = SplitPath(path);
        var current = (Node)_root;
        foreach (var part in parts)
        {
            if (current is not DirectoryNode directory)
            {
                node = null!;
                return false;
            }

            if (!directory.Children.TryGetValue(part, out var child))
            {
                node = null!;
                return false;
            }

            current = child;
        }

        node = current;
        return true;
    }

    /// <summary>
    /// Splits a path into normalized parent and leaf name components.
    /// </summary>
    private static (string Parent, string Name) GetParentAndName(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return ("/", string.Empty);
        }

        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return ("/", normalized);
        }

        var parent = "/" + normalized[..lastSlash];
        var name = normalized[(lastSlash + 1)..];
        return (parent, name);
    }

    /// <summary>
    /// Normalizes a path and splits it into segments without empty components.
    /// </summary>
    private static IReadOnlyList<string> SplitPath(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized.Length == 0)
        {
            return [];
        }

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Normalizes an absolute path by trimming leading slash and whitespace.
    /// </summary>
    private static string NormalizePath(string path)
    {
        EnsureAbsolutePath(path);
        var trimmed = path.Trim();
        if (trimmed == "/")
        {
            return string.Empty;
        }

        trimmed = trimmed.TrimStart('/');

        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed;
    }

    /// <summary>
    /// Throws when a path is null, empty, or not absolute.
    /// </summary>
    private static void EnsureAbsolutePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
        {
            throw new InvalidOperationException("MockFileSystem requires absolute paths.");
        }
    }

    private abstract class Node(FileTreePermission permission)
    {
        public FileTreePermission Permission { get; } = permission;
        public DateTimeOffset? StatusChangedAt { get; set; }
    }

    /// <summary> Represents an in-memory directory node. </summary>
    private sealed class DirectoryNode : Node
    {
        public DirectoryNode(DateTimeOffset modifiedAt, FileTreePermission permission) : base(permission)
        {
            ModifiedAt = modifiedAt;
        }

        public SortedDictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset ModifiedAt { get; }
    }

    /// <summary> Represents an in-memory file node. </summary>
    private sealed class FileNode : Node
    {
        public FileNode(byte[] content, DateTimeOffset modifiedAt, FileTreePermission permission) : base(permission)
        {
            Content = content ?? [];
            ModifiedAt = modifiedAt;
        }

        public byte[] Content { get; set; }

        public DateTimeOffset ModifiedAt { get; set; }
    }


    /// <summary> Tracks read state for an opened file or stdin. </summary>
    private sealed class OpenFileState
    {
        /// <summary> Stores the file content bytes. </summary>
        private readonly byte[] _content;
        private readonly ByteMemoryBuilder? _writeBuffer;
        private readonly Action<byte[]>? _onClose;
        private readonly Action<int> _onRead;
        /// <summary> Tracks the current read offset into <see cref="_content"/>. </summary>
        private int _offset;

        private OpenFileState(byte[] content, ByteMemoryBuilder? writeBuffer, Action<byte[]>? onClose, Action<int> onRead)
        {
            _content = content ?? [];
            _writeBuffer = writeBuffer;
            _onClose = onClose;
            _onRead = onRead;
        }

        public static OpenFileState ForRead(byte[] content, Action<int> onRead) => new(content, null, null, onRead);

        public static OpenFileState ForWrite(Action<byte[]> onClose) => new([], ByteMemoryBuilder.CreateUnaccounted(256), onClose, static _ => { });

        public bool IsWritable => _writeBuffer != null;

        public bool TryAppend(ReadOnlyMemory<byte> content)
        {
            if (_writeBuffer == null)
            {
                return false;
            }

            _writeBuffer.Append(content);
            return true;
        }

        public void Close()
        {
            if (_writeBuffer == null || _onClose == null)
            {
                return;
            }

            _onClose(_writeBuffer.ToMemory().ToArray());
        }

        private DescriptorReadBufferOwner? _readBuffers;

        public DescriptorReadBuffer Read(int maxBytes)
        {
            if (_offset >= _content.Length)
                return DescriptorReadBuffer.EndOfFile;
            var length = Math.Min(maxBytes, _content.Length - _offset);
            return (_readBuffers ??= new(count => { _offset += count; _onRead(count); }, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes))).Borrow(_content.AsMemory(_offset, length),
                ByteReadResult.Success(length, _offset + length == _content.Length), canRetain: true);
        }
    }
}
