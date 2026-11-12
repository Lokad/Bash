using System.Collections.Concurrent;
using Lokad.Bash.Commands;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Benchmarks;

/// <summary>Synthetic byte host. File snapshots are immutable; file versions remain owned until the run ends.</summary>
internal sealed class BenchmarkHost : IBashHost, IAsyncDisposable
{
    private readonly IMemoryScope _memory;
    private readonly ConcurrentDictionary<int, BenchDescriptor> _handles = new();
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _files = new(StringComparer.Ordinal);
    private readonly List<ReadOnlyMemory<byte>> _versions = [];
    private readonly Lock _fileGate = new();
    private readonly DigestOutput _stdout;
    private readonly DigestOutput _stderr;
    private int _nextDescriptor = 9;
    private long _writes, _reads, _pipes, _opens, _closes, _lookups;
    public IMemoryBudget MemoryBudget => _memory;
    public IShellTiming Timing { get; } = new UntimedClock();
    public ShellExecutionContext? Execution => null;

    public BenchmarkHost(IMemoryBudget memory, ReadOnlyMemory<byte> input,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, Stream? stdout, Stream? stderr)
    {
        _memory = memory.CreateScope();
        // Input and fixtures retain their caller's longer-lived admission.
        if (!_memory.TryReserve(16 * 1024, out _))
        {
            _memory.Dispose();
            throw new MemoryBudgetExceededException();
        }
        _stdout = new DigestOutput(stdout);
        _stderr = new DigestOutput(stderr);
        _handles[0] = new MemoryInput(input, _memory);
        _handles[1] = _stdout;
        _handles[2] = _stderr;
        foreach (var file in files) _files.Add("/" + file.Key, file.Value);
    }

    public ContentDigest Stdout => _stdout.Snapshot();
    public ContentDigest Stderr => _stderr.Snapshot();
    public HostCounts Counts => new(_writes, _reads, _pipes, _opens, _closes, _lookups);
    public IReadOnlyDictionary<string, ContentDigest> FileDigests
    {
        get
        {
            lock (_fileGate)
                return _files.ToDictionary(pair => pair.Key[1..], pair => new ContentDigest(pair.Value.Length, Blob.Hash(pair.Value.Span)), StringComparer.Ordinal);
        }
    }

    public ValueTask<DescriptorWriteResult> WriteAsync(FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        Interlocked.Increment(ref _writes);
        return _handles.TryGetValue(descriptor.Id, out var handle) ? handle.WriteAsync(content, token)
            : ValueTask.FromResult(DescriptorWriteResult.WriteFailure);
    }
    public ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken token)
    {
        Interlocked.Increment(ref _reads);
        return _handles.TryGetValue(descriptor.Id, out var handle) ? handle.ReadAsync(request, token)
            : ValueTask.FromResult(DescriptorReadBuffer.Failure(ByteReadResult.ReadFailure));
    }
    public async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
    {
        Interlocked.Increment(ref _closes);
        if (!_handles.TryGetValue(descriptor.Id, out var handle)) return null;
        try { return await handle.CloseAsync().ConfigureAwait(false); }
        finally { _handles.TryRemove(new KeyValuePair<int, BenchDescriptor>(descriptor.Id, handle)); }
    }
    public async Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _pipes);
        var pipe = new BoundedBytePipe(_memory, lifetime);
        PipeInput? input = null;
        FileDescriptor? read = null;
        try
        {
            input = new PipeInput(pipe, _memory);
            read = Add(input);
            var write = Add(new PipeOutput(pipe));
            return PipeCreationResult.Created(new PipeDescriptors(read.Value, write));
        }
        catch
        {
            if (read is { } acquired) await CloseDescriptorAsync(acquired).ConfigureAwait(false);
            else if (input != null) await input.CloseAsync().ConfigureAwait(false);
            else await pipe.CloseReaderAsync().ConfigureAwait(false);
            pipe.CloseWriter();
            throw;
        }
    }

    public Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(IReadOnlyList<FilePathLookup> paths,
        FilePathInfoRequest request, FileSnapshotRequest snapshots, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Add(ref _lookups, paths.Count);
        var results = new FilePathResolutionResult[paths.Count];
        lock (_fileGate)
        {
            for (var index = 0; index < paths.Count; index++)
            {
                var path = Resolve(paths[index]);
                var exists = _files.TryGetValue(path, out var bytes);
                var kind = exists ? FilePathKind.File : path == "/" ? FilePathKind.Directory : FilePathKind.Missing;
                var info = request == FilePathInfoRequest.KindOnly || kind == FilePathKind.Missing
                    ? FilePathInfo.FromKind(kind) : new FilePathInfo(kind, new FileMetadata(exists ? bytes.Length : 0,
                        DateTimeOffset.UnixEpoch, FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute,
                        null, null, null, null, null));
                results[index] = FilePathResolutionResult.Success(info) with
                {
                    File = snapshots == FileSnapshotRequest.Capture
                        ? exists ? FileSnapshotResult.Success(new Snapshot(this, bytes)) : FileSnapshotResult.NotFile(kind) : null
                };
            }
        }
        return Task.FromResult<IReadOnlyList<FilePathResolutionResult>>(results);
    }
    public Task<FileOpenResult> OpenReadAsync(FileSnapshot file, FileReadOpenMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _opens);
        if (file is not Snapshot snapshot || !ReferenceEquals(snapshot.Owner, this))
            throw new ArgumentException("Snapshot belongs to another host.", nameof(file));
        return Task.FromResult(FileOpenResult.Opened(Add(new MemoryInput(snapshot.Content, _memory)), snapshot.Content.Length));
    }
    public Task<FileOpenResult> OpenWritableFileAsync(FilePathLookup lookup, WritableFileOpenMode mode,
        FileCreationMode creation, FileWriteRetention retention, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mode is not (WritableFileOpenMode.WriteTruncate or WritableFileOpenMode.WriteAppend))
            throw new ArgumentOutOfRangeException(nameof(mode), "Benchmark files expose truncate and append only.");
        var path = Resolve(lookup);
        if (path == "/")
            return Task.FromResult(FileOpenResult.Failed(new OperationError("Is a directory\n") { Kind = OperationErrorKind.IsDirectory }));
        Interlocked.Increment(ref _opens);
        lock (_fileGate)
        {
            var exists = _files.ContainsKey(path);
            if (creation == FileCreationMode.CreateNew && exists)
                return Task.FromResult(FileOpenResult.Failed(new OperationError("File exists\n") { Kind = OperationErrorKind.AlreadyExists }));
            if (creation == FileCreationMode.OpenExisting && !exists)
                return Task.FromResult(FileOpenResult.Failed(new OperationError("No such file or directory\n") { Kind = OperationErrorKind.NotFound }));
            if (!exists || mode == WritableFileOpenMode.WriteTruncate) _files[path] = ReadOnlyMemory<byte>.Empty;
        }
        return Task.FromResult(FileOpenResult.Opened(Add(new FileOutput(this, path, mode == WritableFileOpenMode.WriteAppend)), null));
    }

    private FileDescriptor Add(BenchDescriptor descriptor)
    {
        var id = Interlocked.Increment(ref _nextDescriptor);
        if (!_handles.TryAdd(id, descriptor)) throw new InvalidOperationException("Repeated descriptor.");
        return new FileDescriptor(id);
    }
    private static string Resolve(FilePathLookup lookup)
    {
        var (path, directories) = lookup.ResolveWithoutSymbolicLinks();
        if (directories.Any(directory => directory.ToString() != "/"))
            throw new InvalidOperationException("Benchmark fixtures are flat and have no symbolic links.");
        return path.ToString();
    }
    public Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(IReadOnlyList<string> names, ShellPath directory,
        IReadOnlyList<EnvironmentVariable> environment, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<CommandLookupResult>>(names.Select(_ => (CommandLookupResult)new CommandLookupResult.Missing()).ToArray());
    public Task<CommandExecutionResult> RunCommandAsync(CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken token) =>
        throw new InvalidOperationException("Workload unexpectedly invoked an external utility: " + command.CommandName);
    public Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(IReadOnlyList<FileDescriptor> descriptors, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<bool>>(descriptors.Select(_ => false).ToArray());
    public Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(IReadOnlyList<FilePathLookup> paths,
        DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots, DirectoryAccessPattern access, CancellationToken token) =>
        throw new InvalidOperationException("Pathname enumeration is outside the current benchmark host capabilities.");

    public async ValueTask DisposeAsync()
    {
        // Close both pipe ends concurrently so a pending producer cannot block reader retirement.
        await Task.WhenAll(_handles.Keys.Select(id => CloseDescriptorAsync(new FileDescriptor(id)))).ConfigureAwait(false);
        _stdout.Dispose(); _stderr.Dispose();
        _files.Clear(); _versions.Clear();
        _memory.Dispose();
    }

    private sealed class Snapshot(BenchmarkHost owner, ReadOnlyMemory<byte> bytes) : FileSnapshot(bytes.Length, true)
    {
        public BenchmarkHost Owner => owner;
        public ReadOnlyMemory<byte> Content => bytes;
        public override bool TryGetKnownMd5(Span<byte> destination) => false;
    }
    private sealed class FileOutput(BenchmarkHost owner, string path, bool append) : BenchDescriptor
    {
        private int _position;
        protected override ValueTask<DescriptorWriteResult> WriteCoreAsync(ReadOnlyMemory<byte> content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (owner._fileGate)
            {
                var previous = owner._files[path];
                if (append) _position = previous.Length;
                var length = Math.Max(previous.Length, checked(_position + content.Length));
                owner._memory.Reserve(checked(64L + length));
                var bytes = new byte[length];
                previous.CopyTo(bytes);
                content.CopyTo(bytes.AsMemory(_position));
                _position += content.Length;
                owner._versions.Add(bytes);
                owner._files[path] = bytes;
            }
            return ValueTask.FromResult(DescriptorWriteResult.Open);
        }
    }
    private sealed class UntimedClock : IShellTiming, IDisposable
    {
        public IDisposable BeginEvaluation() => this;
        public ShellTimingSnapshot Read() => throw new InvalidOperationException("Shell time is excluded; harness clocks measure the declared boundary.");
        public void Dispose() { }
    }
}

internal sealed record HostCounts(long Writes, long Reads, long Pipes, long Opens, long Closes, long PathLookups);
