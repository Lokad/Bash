using System;
using System.Collections.Concurrent;
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
        FileDescriptor[] descriptors;
        lock (_descriptorLock)
            descriptors = _openFiles.Keys.ToArray();
        foreach (var descriptor in descriptors)
            await CloseDescriptorAsync(descriptor).ConfigureAwait(false);
    }
    public Lokad.Bash.Memory.IMemoryBudget MemoryBudget { get; set; } = new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes);

    public IShellTiming Timing { get; init; } = TestTiming.Instance;
    public ShellExecutionContext? Execution => null;

    /// <summary> Stores the root directory node for the in-memory filesystem. </summary>
    private readonly DirectoryNode _root = new(DateTimeOffset.UtcNow, FileTreePermission.ReadWrite);
    // Guards directory entries and operation checks. Test callbacks run outside this lock.
    private readonly Lock _treeLock = new();
    /// <summary> Tracks open file descriptors and their associated read state. </summary>
    private readonly Dictionary<FileDescriptor, OpenFileState> _openFiles = new();
    private readonly Lock _descriptorLock = new();
    /// <summary> Stores output buffers for descriptors written via WriteAsync. </summary>
    private readonly Dictionary<FileDescriptor, ByteMemoryBuilder> _outputs = new();
    private readonly Lock _outputLock = new();
    /// <summary> Stores read-open failures keyed by absolute path. </summary>
    private readonly ConcurrentDictionary<string, OperationError> _readOpenErrors = new(StringComparer.Ordinal);
    /// <summary> Stores the stdin read state when configured. </summary>
    private OpenFileState? _stdinState;
    /// <summary> Tracks the next file descriptor to allocate for opened files. </summary>
    private int _nextDescriptor = 3;

    public List<CommandInvocation> CommandInvocations { get; } = [];

    public int CommandExitCode { get; set; }

    public int OpenFileCount
    {
        get { lock (_descriptorLock) return _openFiles.Count; }
    }

    public void ResetOutput()
    {
        lock (_outputLock)
            _outputs.Clear();
    }

    public bool PreparedReadsAreBuffered { get; init; } = true;

    public bool PreparedReadSizeKnown { get; init; } = true;

    public bool FailOpenFileAppends { get; set; }

    private int _appendCallCount;
    public int AppendCallCount => Volatile.Read(ref _appendCallCount);

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

    public int MaximumReadWindowBytes { get; init; } = int.MaxValue;
    private int _readCallCount;
    private long _readBytesConsumed;
    public int ReadCallCount => Volatile.Read(ref _readCallCount);
    public long ReadBytesConsumed => Volatile.Read(ref _readBytesConsumed);
    private void RecordConsumption(int count) => Interlocked.Add(ref _readBytesConsumed, count);

    public void AddDirectory(string path, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        lock (_treeLock)
            EnsureDirectory(path, modifiedAt, permission);
    }

    public void AddFile(string path, string content, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite) =>
        AddFile(path, Encoding.UTF8.GetBytes(content), modifiedAt, permission);

    public void AddFile(string path, byte[] content, DateTimeOffset? modifiedAt = null, FileTreePermission permission = FileTreePermission.ReadWrite)
    {
        var (parent, name) = GetParentAndName(path);
        lock (_treeLock)
        {
            var directory = EnsureDirectory(parent, modifiedAt);
            directory.Children[name] = new FileNode(content, modifiedAt ?? DateTimeOffset.UtcNow, permission);
        }
    }

    public void AddReadOpenError(string path, string message)
    {
        EnsureAbsolutePath(path);
        _readOpenErrors[path] = new OperationError(message);
    }

    public bool FileExists(string path)
    {
        lock (_treeLock)
            return ResolveNode(path) is FileNode;
    }

    public byte[] GetOutputBytes(FileDescriptor descriptor)
    {
        lock (_outputLock)
            return _outputs.TryGetValue(descriptor, out var bytes) ? bytes.ToMemory().ToArray() : [];
    }

    public byte[] ReadFileBytes(string path) => ReadFileContent(path).ToArray();

    private byte[] ReadFileContent(string path)
    {
        lock (_treeLock)
            return ResolveNode(path) is FileNode file ? file.Content : throw new InvalidOperationException("File not found.");
    }

    /// <summary>Captures the complete synthetic tree after execution and descriptor cleanup have settled.</summary>
    internal IReadOnlyDictionary<string, (FilePathKind Kind, string ContentHex)> CaptureEntries()
    {
        lock (_treeLock)
        {
            var entries = new SortedDictionary<string, (FilePathKind Kind, string ContentHex)>(StringComparer.Ordinal);
            var pending = new Stack<(string Path, DirectoryNode Directory)>();
            pending.Push((string.Empty, _root));
            while (pending.TryPop(out var parent))
            {
                foreach (var (name, node) in parent.Directory.Children)
                {
                    var path = parent.Path.Length == 0 ? name : parent.Path + "/" + name;
                    if (node is DirectoryNode directory)
                    {
                        entries.Add(path, (FilePathKind.Directory, string.Empty));
                        pending.Push((path, directory));
                    }
                    else if (node is FileNode file)
                        entries.Add(path, (FilePathKind.File, Convert.ToHexString(file.Content)));
                    else
                        throw new InvalidOperationException("Unknown fixture node.");
                }
            }
            return entries;
        }
    }

    public void SetStandardInput(string content) => SetStandardInputBytes(Encoding.UTF8.GetBytes(content));

    public void SetStandardInputBytes(byte[] content)
    {
        lock (_descriptorLock)
            _stdinState = OpenFileState.ForRead(content, RecordConsumption);
    }

    public string GetOutput(FileDescriptor descriptor)
    {
        lock (_outputLock)
            return _outputs.TryGetValue(descriptor, out var bytes)
                ? Encoding.UTF8.GetString(bytes.ToMemory().Span)
                : string.Empty;
    }

    public string ReadFile(string path) => Encoding.UTF8.GetString(ReadFileContent(path));

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

        Interlocked.Increment(ref _appendCallCount);
        var success = AppendRemainsOpen ? DescriptorWriteResult.Open : DescriptorWriteResult.Closed;
        if (fileDescriptor.IsDevNull)
        {
            return ValueTask.FromResult(fileDescriptor.IsReadOnlyDevNull
                ? DescriptorWriteResult.WriteFailure : success);
        }

        if (FindOpenFile(fileDescriptor) is { } openFile)
        {
            if (FailOpenFileAppends)
            {
                return ValueTask.FromResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
            }

            return ValueTask.FromResult(openFile.TryAppend(content)
                ? success
                : DescriptorWriteResult.Failure(new OperationError("Write failed\n")));
        }

        lock (_outputLock)
        {
            if (!_outputs.TryGetValue(fileDescriptor, out var bytes))
            {
                bytes = ByteMemoryBuilder.CreateUnaccounted(256);
                _outputs[fileDescriptor] = bytes;
            }

            bytes.Append(content);
        }
        return ValueTask.FromResult(success);
    }

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
        // This fixture retains its test file content until the fixture is discarded.
        BeforeWritableOpen?.Invoke(new ShellPath(path.Path));
        cancellationToken.ThrowIfCancellationRequested();
        var (resolved, directories) = path.ResolveWithoutSymbolicLinks();
        lock (_treeLock)
            return Task.FromResult(OpenFile());

        FileOpenResult OpenFile()
        {
            if (CheckTraversal(resolved, directories, nullDeviceIsFile: true, cancellationToken) is { } traversalError)
                return FileOpenResult.Failed(traversalError);
            if (resolved.Equals(FileDescriptor.DevNullPath))
            {
                return creationMode == FileCreationMode.CreateNew
                    ? FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists })
                    : FileOpenResult.Opened(FileDescriptor.ForWritableDevNull(mode), readSize: null);
            }

            var (parentPath, name) = GetParentAndName(resolved.Path);
            if (string.IsNullOrEmpty(name))
            {
                return creationMode == FileCreationMode.CreateNew
                    ? FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists })
                    : FileOpenResult.Failed(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory });
            }

            var parentNode = ResolveNode(parentPath, out var missingKind);
            if (parentNode == null)
                return FileOpenResult.Failed(MissingPathError(missingKind));
            if (parentNode is not DirectoryNode parent)
                return FileOpenResult.Failed(new OperationError("Not a directory\n") { Kind = OperationErrorKind.NotDirectory });

            var target = parent.Children.GetValueOrDefault(name);
            if (target != null && creationMode == FileCreationMode.CreateNew)
                return FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists });
            if (target is DirectoryNode)
                return FileOpenResult.Failed(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory });
            if (target == null && creationMode == FileCreationMode.OpenExisting)
                return FileOpenResult.Failed(MissingPathError(OperationErrorKind.NotFound));

            var modifiedAt = DateTimeOffset.UtcNow;
            var file = target as FileNode ?? new FileNode([], modifiedAt, FileTreePermission.ReadWrite);
            if (mode == WritableFileOpenMode.WriteTruncate)
                file.ReplaceContent([], modifiedAt);
            if (target == null)
                parent.Children.Add(name, file);
            var descriptor = AddOpenFile(OpenFileState.ForWrite(file, mode, RecordConsumption));
            return FileOpenResult.Opened(descriptor, readSize: null);
        }
    }

    public List<IReadOnlyList<FilePathLookup>> ResolutionBatches { get; } = [];
    public Action? AfterResolveFiles { get; set; }
    public bool IncludeListingSnapshots { get; init; } = true;
    public bool IncludeListingMetadata { get; init; } = true;

    private OperationError? CheckTraversal(
        ShellPath path, IReadOnlyList<ShellPath> directories, bool nullDeviceIsFile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var directory in directories)
        {
            if (nullDeviceIsFile && FileDescriptor.DevNullPath.IsEqualToOrParentOf(directory))
                return MissingPathError(OperationErrorKind.NotDirectory);

            var parent = ResolveNode(directory.Path, out var missingKind);
            if (parent == null)
                return MissingPathError(missingKind);
            if (parent is not DirectoryNode)
                return MissingPathError(OperationErrorKind.NotDirectory);
        }
        return nullDeviceIsFile && FileDescriptor.DevNullPath.IsParentOf(path)
            ? MissingPathError(OperationErrorKind.NotDirectory) : null;
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
        var descriptor = AddOpenFile(OpenFileState.ForRead(read.Content, RecordConsumption));
        try
        {
            if (AfterReadOpen is { } afterReadOpen)
                await afterReadOpen(read.Path).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return FileOpenResult.Opened(descriptor, readSize: read.Size);
        }
        catch
        {
            CloseOpenFile(descriptor);
            throw;
        }
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
        if (!descriptor.IsDevNull)
            CloseOpenFile(descriptor);
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
        lock (_treeLock)
        {
            DirectoryListingBatches.Add(paths.ToArray());
            DirectoryMetadataRequests.Add(metadata);
            DirectorySnapshotRequests.Add(snapshots);
        }
        var results = new DirectoryListingResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
            results[i] = await ListDirectoryAsync(paths[i]).ConfigureAwait(false);
        return results;

        async Task<DirectoryListingResult> ListDirectoryAsync(FilePathLookup lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
            if (BeforeListDirectory is { } beforeListDirectory)
            {
                lock (_treeLock)
                    if (CheckDirectoryTraversal() is { } error)
                        return DirectoryListingResult.Failure(error);
                beforeListDirectory(path);
            }

            DirectoryListingResult result;
            lock (_treeLock)
            {
                if (CheckDirectoryTraversal() is { } error)
                    return DirectoryListingResult.Failure(error);

                var node = ResolveNode(path.Path, out var missingKind);
                if (node == null)
                    return DirectoryListingResult.Failure(MissingPathError(missingKind));
                if (node is not DirectoryNode directory)
                    return DirectoryListingResult.Failure(MissingPathError(OperationErrorKind.NotDirectory));

                var entries = new List<DirectoryEntry>(directory.Children.Count);
                List<FileMetadata?>? available = metadata == DirectoryMetadataRequest.Available && IncludeListingMetadata
                    ? new List<FileMetadata?>(directory.Children.Count) : null;
                foreach (var (name, child) in directory.Children)
                {
                    entries.Add(new DirectoryEntry(name, child is DirectoryNode ? FilePathKind.Directory : FilePathKind.File)
                    {
                        Snapshot = snapshots == DirectorySnapshotRequest.CaptureAvailable && IncludeListingSnapshots && child is FileNode file
                            ? new PreparedRead(this, path.Child(name), file.Content) : null
                    });
                    available?.Add(GetMetadata(child));
                }
                result = available == null ? DirectoryListingResult.Success(entries) : DirectoryListingResult.Success(entries, available);
            }

            if (AfterListDirectory is { } afterListDirectory)
                await afterListDirectory(path).ConfigureAwait(false);
            return result;

            OperationError? CheckDirectoryTraversal() =>
                CheckTraversal(path, directories, nullDeviceIsFile: true, cancellationToken)
                ?? (FileDescriptor.DevNullPath.Equals(path) ? MissingPathError(OperationErrorKind.NotDirectory) : null);
        }
    }

    public virtual ValueTask<DescriptorReadBuffer> ReadAsync(
        FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readCallCount);
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
        lock (_treeLock)
        {
            if (snapshots == FileSnapshotRequest.Capture)
                ResolutionBatches.Add(paths.ToArray());
            else
                PathInfoBatches.Add((paths.ToArray(), request));
        }
        var results = new FilePathResolutionResult[paths.Count];
        for (var i = 0; i < paths.Count; i++)
            results[i] = await GetInfoAsync(paths[i]);
        if (snapshots == FileSnapshotRequest.Capture)
            AfterResolveFiles?.Invoke();
        return results;

        FilePathResolutionResult TraversalFailure(OperationError error) =>
            (error.Kind == OperationErrorKind.NotFound
                ? FilePathResolutionResult.Success(FilePathInfo.FromKind(FilePathKind.Missing))
                : FilePathResolutionResult.Failure(error)) with
            {
                File = snapshots == FileSnapshotRequest.Capture ? FileSnapshotResult.Failure(error) : null
            };

        async Task<FilePathResolutionResult> GetInfoAsync(FilePathLookup lookup)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
                if (BeforeGetPathInfo is { } beforeGetPathInfo)
                {
                    lock (_treeLock)
                        if (CheckTraversal(path, directories, nullDeviceIsFile: true, cancellationToken) is { } error)
                            return TraversalFailure(error);
                    beforeGetPathInfo(path);
                }

                FilePathResolutionResult result;
                lock (_treeLock)
                {
                    if (CheckTraversal(path, directories, nullDeviceIsFile: true, cancellationToken) is { } error)
                        return TraversalFailure(error);

                    var node = ResolveNode(path.Path, out var missingKind);
                    var isNullDevice = path.Equals(FileDescriptor.DevNullPath);
                    var injectedFailure = _readOpenErrors.ContainsKey(path.Path);
                    var kind = isNullDevice ? FilePathKind.CharacterDevice : node is DirectoryNode ? FilePathKind.Directory
                        : node is FileNode || injectedFailure ? FilePathKind.File : FilePathKind.Missing;
                    if (kind == FilePathKind.Missing && missingKind != OperationErrorKind.NotFound)
                    {
                        var missing = MissingPathError(missingKind);
                        return FilePathResolutionResult.Failure(missing) with
                        {
                            File = snapshots == FileSnapshotRequest.Capture ? FileSnapshotResult.Failure(missing) : null
                        };
                    }

                    var info = request == FilePathInfoRequest.KindOnly ? FilePathInfo.FromKind(kind)
                        : new FilePathInfo(kind, node == null ? default : GetMetadata(node));
                    result = FilePathResolutionResult.Success(info) with
                    {
                        File = snapshots != FileSnapshotRequest.Capture ? null
                            : node is FileNode sourceFile && !isNullDevice ? FileSnapshotResult.Success(new PreparedRead(this, path, sourceFile.Content))
                            // Injected open failures still need a snapshot without a backing file.
                            : isNullDevice || injectedFailure ? FileSnapshotResult.Success(new PreparedRead(this, path, []))
                            : FileSnapshotResult.NotFile(kind)
                    };
                }

                if (AfterGetPathInfo is { } afterGetPathInfo)
                    await afterGetPathInfo(path, request).ConfigureAwait(false);
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
            FileNode file => file.CaptureMetadata(access),
            DirectoryNode directory => new FileMetadata(0, directory.ModifiedAt, access, directory.StatusChangedAt,
                accessedAt: null, specialModeBits: FileSpecialModeBits.None, ownershipMatch: null, identity: null),
            _ => default
        };
    }

    public Task<DirectoryCreationResult> CreateDirectoryAsync(FilePathLookup path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateDirectoryOverride?.Invoke(path) is { } result)
            return Task.FromResult(result);

        var resolved = path.ResolveDirectoryCreationWithoutSymbolicLinks();
        lock (_treeLock)
            return Task.FromResult(Create());

        DirectoryCreationResult Create()
        {
            if (CheckTraversal(resolved.Path, resolved.RequiredDirectories, nullDeviceIsFile: false, cancellationToken) is { } error)
                return new DirectoryCreationResult.Failed(error);
            var (parentPath, name) = GetParentAndName(resolved.Path.Path);
            if (string.IsNullOrEmpty(name))
                return new DirectoryCreationResult.AlreadyExistsAsDirectory();

            var parentNode = ResolveNode(parentPath, out var missingKind);
            if (parentNode == null)
                return new DirectoryCreationResult.Failed(MissingPathError(missingKind));
            if (parentNode is not DirectoryNode parent)
                return new DirectoryCreationResult.Failed(MissingPathError(OperationErrorKind.NotDirectory));

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
    }

    public Task<OperationError?> DeleteDirectoryAsync(FileEntryPath lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
        if (BeforeDeleteDirectory != null || DeleteDirectoryError != null)
        {
            lock (_treeLock)
                if (CheckTarget() is { } error)
                    return Task.FromResult<OperationError?>(error);
            BeforeDeleteDirectory?.Invoke(path);
            if (DeleteDirectoryError?.Invoke(path, cancellationToken) is { } failure)
                return Task.FromResult<OperationError?>(failure);
        }

        lock (_treeLock)
            return Task.FromResult(Delete());

        OperationError? CheckTarget() => CheckTraversal(path, directories, nullDeviceIsFile: false, cancellationToken)
            ?? (lookup.HasFinalDotComponent
                ? new OperationError("Cannot delete '.' or '..' directory entries\n") { Kind = OperationErrorKind.PermissionDenied }
                : null);

        OperationError? Delete()
        {
            if (CheckTarget() is { } error)
                return error;
            var (parentPath, name) = GetParentAndName(path.Path);
            if (string.IsNullOrEmpty(name))
                return new OperationError("Operation failed.\n") { Kind = OperationErrorKind.PermissionDenied };

            var parentNode = ResolveNode(parentPath, out var missingKind);
            if (parentNode is not DirectoryNode parent)
                return MissingPathError(parentNode == null ? missingKind : OperationErrorKind.NotDirectory);

            if (!parent.Children.TryGetValue(name, out var node))
                return MissingPathError(OperationErrorKind.NotFound);
            if (node is not DirectoryNode directory)
                return MissingPathError(OperationErrorKind.NotDirectory);
            if (directory.Permission == FileTreePermission.ReadOnly
                || parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
                return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };
            if (directory.Children.Count > 0)
                return new OperationError("Directory not empty\n") { Kind = OperationErrorKind.DirectoryNotEmpty };

            parent.Children.Remove(name);
            return null;
        }
    }

    public Task<OperationError?> DeleteFileAsync(FileEntryPath lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
        if (BeforeDeleteFile != null || DeleteFileError != null)
        {
            lock (_treeLock)
                if (CheckTraversal(path, directories, nullDeviceIsFile: false, cancellationToken) is { } traversalError)
                    return Task.FromResult<OperationError?>(traversalError);
            BeforeDeleteFile?.Invoke(path);
            if (DeleteFileError is { } error)
                return Task.FromResult<OperationError?>(error);
        }

        lock (_treeLock)
            return Task.FromResult(Delete());

        OperationError? Delete()
        {
            if (CheckTraversal(path, directories, nullDeviceIsFile: false, cancellationToken) is { } traversalError)
                return traversalError;
            var (parentPath, name) = GetParentAndName(path.Path);
            if (string.IsNullOrEmpty(name))
                return new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory };

            var parentNode = ResolveNode(parentPath, out var missingKind);
            if (parentNode == null)
                return MissingPathError(missingKind);
            if (parentNode is not DirectoryNode parent)
                return MissingPathError(OperationErrorKind.NotDirectory);
            if (!parent.Children.TryGetValue(name, out var node))
                return MissingPathError(OperationErrorKind.NotFound);
            if (node is not FileNode file)
                return new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory };
            if (file.Permission == FileTreePermission.ReadOnly
                || parentPath != "/" && parent.Permission == FileTreePermission.ReadOnly)
                return new OperationError("Permission denied\n") { Kind = OperationErrorKind.PermissionDenied };

            parent.Children.Remove(name);
            return null;
        }
    }

    private static OperationError MissingPathError(OperationErrorKind kind) =>
        new(kind == OperationErrorKind.NotDirectory ? "Not a directory\n" : "No such file or directory\n") { Kind = kind };

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

    private Task<FileTransferError?> TransferAsync(FileEntryPath sourceLookup, FileEntryPath destinationLookup,
        FileOverwriteMode mode, TransferKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode is not (FileOverwriteMode.ReplaceExisting or FileOverwriteMode.RequireNewDestination))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (kind == TransferKind.Rename)
        {
            if (sourceLookup.HasFinalDotComponent || destinationLookup.HasFinalDotComponent)
                return Task.FromResult<FileTransferError?>(new FileTransferError(
                    new OperationError("Cannot rename '.' or '..' directory entries\n"),
                    sourceLookup.HasFinalDotComponent && destinationLookup.HasFinalDotComponent ? FileTransferOperand.Operation
                    : sourceLookup.HasFinalDotComponent ? FileTransferOperand.Source : FileTransferOperand.Destination));
            if (RenameEntryError?.Invoke(sourceLookup, destinationLookup, cancellationToken) is { } failure)
                return Task.FromResult<FileTransferError?>(failure);
        }

        var (sourcePath, sourceDirectories) = sourceLookup.ResolveWithoutSymbolicLinks();
        var (destinationEntryPath, destinationDirectories) = destinationLookup.ResolveWithoutSymbolicLinks();
        if (BeforeFileTransfer is { } beforeFileTransfer)
        {
            ShellPath destination;
            lock (_treeLock)
                if (CheckPaths(out destination, out _) is { } error)
                    return Task.FromResult<FileTransferError?>(error);
            beforeFileTransfer(destination);
        }

        lock (_treeLock)
            return Task.FromResult(Transfer());

        FileTransferError? CheckPaths(out ShellPath destination, out Node? source)
        {
            var directories = destinationDirectories;
            destination = destinationEntryPath;
            source = null;
            if (CheckTraversal(sourcePath, sourceDirectories, nullDeviceIsFile: false, cancellationToken) is { } sourceError)
                return new FileTransferError(sourceError, FileTransferOperand.Source);
            source = ResolveNode(sourcePath.Path);
            if (kind == TransferKind.Rename && source is DirectoryNode)
                (destination, directories) = destinationLookup.ResolveDirectoryCreationWithoutSymbolicLinks();
            return CheckTraversal(destination, directories, nullDeviceIsFile: false, cancellationToken) is { } destinationError
                ? new FileTransferError(destinationError, FileTransferOperand.Destination) : null;
        }

        FileTransferError? Transfer()
        {
            if (CheckPaths(out var destinationPath, out var sourceNode) is { } error)
                return error;
            if (sourceNode == null || kind == TransferKind.Copy && sourceNode is not FileNode)
                return new FileTransferError(new OperationError("Operation failed.\n"), FileTransferOperand.Source);

            var (parentPath, name) = GetParentAndName(destinationPath.Path);
            if (string.IsNullOrEmpty(name))
            {
                return new FileTransferError(new OperationError("Operation failed.\n"), FileTransferOperand.Destination);
            }

            if (ResolveNode(parentPath) is not DirectoryNode parent)
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
                if (ResolveNode(sourceParentPath) is not DirectoryNode sourceParent)
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
                    if (node is FileNode file)
                        file.RecordRename(statusChangedAt);
                    else if (node is DirectoryNode directory)
                    {
                        directory.StatusChangedAt = statusChangedAt;
                        foreach (var child in directory.Children.Values)
                            RecordRename(child, statusChangedAt);
                    }
                }
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = ((FileNode)sourceNode).Content.ToArray();
                if (destination is FileNode target)
                {
                    target.ReplaceContent(content, DateTimeOffset.UtcNow);
                }
                else
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    parent.Children[name] = new FileNode(content, timestamp, FileTreePermission.ReadWrite) { StatusChangedAt = timestamp };
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Returns the open-file state for a descriptor, creating stdin state on demand.
    /// </summary>
    private OpenFileState? GetStateForDescriptor(FileDescriptor descriptor)
    {
        if (descriptor != FileDescriptor.StdIn)
            return FindOpenFile(descriptor);

        lock (_descriptorLock)
            return _stdinState ??= OpenFileState.ForRead([], RecordConsumption);
    }

    private FileDescriptor AddOpenFile(OpenFileState state)
    {
        lock (_descriptorLock)
        {
            var descriptor = new FileDescriptor(_nextDescriptor++);
            _openFiles.Add(descriptor, state);
            return descriptor;
        }
    }

    private OpenFileState? FindOpenFile(FileDescriptor descriptor)
    {
        lock (_descriptorLock)
            return _openFiles.GetValueOrDefault(descriptor);
    }

    private void CloseOpenFile(FileDescriptor descriptor)
    {
        // A duplicate close observes completed synchronous cleanup.
        lock (_descriptorLock)
            if (_openFiles.Remove(descriptor, out var state))
                state.Close();
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

    // All namespace reads and writes run under the tree lock.
    private Node? ResolveNode(string path) => ResolveNode(path, out _);

    private Node? ResolveNode(string path, out OperationErrorKind missingKind)
    {
        var current = (Node)_root;
        missingKind = OperationErrorKind.NotFound;
        foreach (var part in SplitPath(path))
        {
            if (current is not DirectoryNode directory)
            {
                missingKind = OperationErrorKind.NotDirectory;
                return null;
            }
            if (!directory.Children.TryGetValue(part, out var child))
                return null;
            current = child;
        }
        return current;
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
        private readonly Lock _contentLock = new();
        private byte[] _snapshot;
        private bool _snapshotCurrent = true;
        private MemoryStream? _writeData;
        private int _writers;

        public FileNode(byte[] content, DateTimeOffset modifiedAt, FileTreePermission permission) : base(permission)
        {
            _snapshot = content;
            ModifiedAt = modifiedAt;
        }

        // Snapshot copies happen on observation, not after every small append. Existing
        // snapshots remain immutable while writers share one geometrically grown store.
        public byte[] Content
        {
            get
            {
                lock (_contentLock)
                {
                    if (!_snapshotCurrent && _writeData is { } data)
                    {
                        _snapshot = data.ToArray();
                        _snapshotCurrent = true;
                    }
                    return _snapshot;
                }
            }
        }

        public void ReplaceContent(byte[] content, DateTimeOffset modifiedAt)
        {
            lock (_contentLock)
            {
                _writeData?.Dispose();
                _writeData = null;
                _snapshot = content;
                _snapshotCurrent = true;
                StatusChangedAt = ModifiedAt = modifiedAt;
            }
        }

        public void RecordRename(DateTimeOffset statusChangedAt)
        {
            lock (_contentLock) StatusChangedAt = statusChangedAt;
        }

        public FileMetadata CaptureMetadata(FileAccessRights access)
        {
            lock (_contentLock)
                return new FileMetadata(CurrentLength, ModifiedAt, access, StatusChangedAt,
                    accessedAt: null, specialModeBits: FileSpecialModeBits.None, ownershipMatch: null, identity: null);
        }

        private int CurrentLength => _writeData is { } data ? checked((int)data.Length) : _snapshot.Length;

        public int Length
        {
            get { lock (_contentLock) return CurrentLength; }
        }

        public void OpenWriter() { lock (_contentLock) _writers++; }

        public void CloseWriter()
        {
            lock (_contentLock)
            {
                if (--_writers != 0 || _writeData is not { } data) return;
                if (!_snapshotCurrent) _snapshot = data.ToArray();
                _snapshotCurrent = true;
                _writeData = null;
                data.Dispose();
            }
        }

        public int Write(int position, ReadOnlyMemory<byte> content, bool append)
        {
            // This per-file lock covers synchronous storage only, never host I/O or a borrow.
            lock (_contentLock)
            {
                if (content.IsEmpty) return position;
                if (_writeData == null)
                {
                    _writeData = new MemoryStream(Math.Max(256, _snapshot.Length));
                    _writeData.Write(_snapshot);
                }
                _writeData.Position = append ? _writeData.Length : position;
                _writeData.Write(content.Span);
                if (!content.IsEmpty)
                {
                    _snapshot = [];
                    _snapshotCurrent = false;
                    StatusChangedAt = ModifiedAt = DateTimeOffset.UtcNow;
                }
                return checked((int)_writeData.Position);
            }
        }

        private DateTimeOffset ModifiedAt { get; set; }
    }


    /// <summary> Tracks read state for an opened file or stdin. </summary>
    private sealed class OpenFileState
    {
        /// <summary> Stores the file content bytes. </summary>
        private readonly byte[] _content;
        private readonly FileNode? _file;
        private readonly WritableFileOpenMode? _mode;
        private readonly Action<int> _onRead;
        /// <summary> Tracks the current read offset into <see cref="_content"/>. </summary>
        private int _offset;

        private OpenFileState(byte[] content, FileNode? file, WritableFileOpenMode? mode, Action<int> onRead)
        {
            _content = content;
            _file = file;
            _mode = mode;
            _onRead = onRead;
            if (file != null)
            {
                file.OpenWriter();
                if (mode == WritableFileOpenMode.WriteAppend) _offset = file.Length;
            }
        }

        public static OpenFileState ForRead(byte[] content, Action<int> onRead) => new(content, null, null, onRead);

        public static OpenFileState ForWrite(FileNode file, WritableFileOpenMode mode, Action<int> onRead) => new([], file, mode, onRead);

        public bool TryAppend(ReadOnlyMemory<byte> content)
        {
            if (_file == null)
            {
                return false;
            }

            _offset = _file.Write(_offset, content, _mode == WritableFileOpenMode.WriteAppend);
            return true;
        }

        public void Close()
        {
            _file?.CloseWriter();
        }

        private DescriptorReadBufferOwner? _readBuffers;

        public DescriptorReadBuffer Read(int maxBytes)
        {
            if (_mode is not null and not WritableFileOpenMode.ReadWrite)
                return DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure);
            var content = _file?.Content ?? _content;
            if (_offset >= content.Length)
                return DescriptorReadBuffer.EndOfFile;
            var length = Math.Min(maxBytes, content.Length - _offset);
            return (_readBuffers ??= new(count => { _offset += count; _onRead(count); }, new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes))).Borrow(content.AsMemory(_offset, length),
                ByteReadResult.Success(length, _offset + length == content.Length), canRetain: true);
        }
    }
}
