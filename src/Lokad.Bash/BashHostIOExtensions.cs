using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Exceptions;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Tree;
namespace Lokad.Bash;

/// <summary> Extension helpers specific to <see cref="IBashHost"/>. </summary>
public static class BashHostIOExtensions
{
    /// <summary>
    /// Writes a line of text to a descriptor, ensuring it ends with a newline.
    /// </summary>
    /// <param name="host">The shell host.</param>
    /// <param name="descriptor">The descriptor to write to.</param>
    /// <param name="line">The line of text to write.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The write result, preserving downstream closure separately from failure.</returns>
    public static async ValueTask<DescriptorWriteResult> WriteLineAsync(
        this IBashHost host,
        FileDescriptor descriptor,
        string line,
        CancellationToken cancellationToken)
    {
        // If the string is small (majority case), over-allocate to avoid traversing string twice
        var maxByteCount = (line.Length > 10240 ? Encoding.UTF8.GetByteCount(line) : line.Length * 4) + 1;
        var bytes = ArrayPool<byte>.Shared.Rent(maxByteCount);
        try
        {
            var byteCount = Encoding.UTF8.GetBytes(line, bytes);
            bytes[byteCount] = (byte)'\n'; // WriteLine includes its LF terminator.
            return await host.WriteAsync(descriptor, bytes[..(byteCount + 1)], cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Inspects one preserved lookup with explicit information detail and final-link policy.</summary>
    /// <exception cref="ShellPathInfoException">The host reported an expected lookup failure.</exception>
    public static async Task<FilePathInfo> GetPathInfoAsync(
        this IBashHost host, FilePathLookup path, FilePathInfoRequest request, CancellationToken cancellationToken)
    {
        var result = (await host.ResolvePathsAsync([path], request, FileSnapshotRequest.None, cancellationToken).ConfigureAwait(false))[0];
        if (result.Error is { } error)
            throw new ShellPathInfoException(path, error);
        return result.Info;
    }

    /// <summary>Gets existence and kind without requesting metadata or reading file contents.</summary>
    /// <exception cref="ShellPathInfoException">The host reported an expected lookup failure.</exception>
    public static async Task<FilePathKind> GetPathKindAsync(
        this IBashHost host, FilePathLookup path, CancellationToken cancellationToken) =>
        (await host.GetPathInfoAsync(path,
            FilePathInfoRequest.KindOnly, cancellationToken).ConfigureAwait(false)).Kind;

    /// <summary>Lists one directory without requesting metadata, snapshots or recursive read-ahead.</summary>
    public static Task<DirectoryListingResult> ListDirectoryAsync(
        this IBashHost host, FilePathLookup path, CancellationToken cancellationToken) =>
        host.ListDirectoryAsync(path, DirectoryMetadataRequest.None, DirectorySnapshotRequest.None, DirectoryAccessPattern.SingleDirectory, cancellationToken);

    /// <summary>Lists one directory with explicit metadata, snapshot and read-ahead requests.</summary>
    public static async Task<DirectoryListingResult> ListDirectoryAsync(
        this IBashHost host, FilePathLookup path, DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots,
        DirectoryAccessPattern accessPattern, CancellationToken cancellationToken) =>
        (await host.ListDirectoriesAsync([path], metadata, snapshots, accessPattern, cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Captures one content version and retains its path information through the host's batched operation.</summary>
    public static async Task<FilePathResolutionResult> ResolveFileAsync(
        this IBashHost host, FilePathLookup path, CancellationToken cancellationToken)
    {
        var result = (await host.ResolvePathsAsync([path], FilePathInfoRequest.KindOnly, FileSnapshotRequest.Capture, cancellationToken)
            .ConfigureAwait(false))[0];
        if (result.File == null)
            throw new InvalidOperationException("The host did not return the requested snapshot result.");
        return result;
    }

    /// <summary>Captures the current file version and opens an owned read-only descriptor.</summary>
    /// <remarks>
    /// Retrieval may be deferred until the first read. Callers scheduling retrieval separately
    /// can prepare snapshots themselves and open them with FileReadOpenMode.Immediate.
    /// The caller closes every successful descriptor through CloseDescriptorAsync.
    /// </remarks>
    public static Task<FileOpenResult> OpenFileAsync(
        this IBashHost host, FilePathLookup path, CancellationToken cancellationToken) =>
        host.OpenFileAsync(path, captured: null, cancellationToken);

    /// <summary>Opens a discovery result, resolving the path only when no result was supplied.</summary>
    /// <remarks>Capture and open failures are reported without retrying the path against a different version.</remarks>
    public static async Task<FileOpenResult> OpenFileAsync(
        this IBashHost host, FilePathLookup path, FileSnapshotResult? captured, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            captured ??= (await host.ResolveFileAsync(path, cancellationToken).ConfigureAwait(false)).File;
            cancellationToken.ThrowIfCancellationRequested();
            return captured?.Snapshot is { } snapshot
                ? await host.OpenReadAsync(snapshot, FileReadOpenMode.Deferred, cancellationToken).ConfigureAwait(false)
                : FileOpenResult.Failed(captured?.Error ?? new OperationError("Cannot open file\n"));
        }
        catch (Exception exception) when (exception is PermissionDeniedException or MissingPathException or IOException)
        {
            return FileOpenResult.Failed(OperationError.FromException(exception));
        }
    }

    /// <summary>Reads complete raw input through bounded byte requests, never through line batches.</summary>
    /// <remarks>
    /// Borrows the descriptor without closing it. Only successful EOF publishes bytes; failures,
    /// cancellation and size rejection discard partial output. A full buffer needs at most one
    /// additional byte to distinguish EOF from excess input, consuming at most one byte beyond the limit.
    /// The host must honor the supplied buffer. Cancellation, thrown I/O failures and accounting exceptions propagate.
    /// </remarks>
    /// <param name="host">The descriptor owner.</param>
    /// <param name="descriptor">The borrowed input descriptor.</param>
    /// <param name="maximumBytes">
    /// The largest accepted input, below the CLR array length limit; scratch storage is separate.
    /// </param>
    /// <param name="chargeRead">
    /// Accounts for each nonfailed read before accumulation, including zero-byte EOF and the overflow probe;
    /// may reject further work by throwing.
    /// </param>
    /// <param name="destinationBudget">Owns the completed bytes; consumers must finish before its scope is disposed.</param>
    /// <param name="cancellationToken">Cancellation for every read and publication of the completed result.</param>
    /// <returns>Complete bytes after EOF, the original descriptor failure, or a size rejection without partial output.</returns>
    public static async ValueTask<BoundedReadResult> TryReadAllBytesAsync(
        this IBashHost host,
        FileDescriptor descriptor,
        int maximumBytes,
        Action<int> chargeRead,
        IMemoryBudget destinationBudget,
        CancellationToken cancellationToken)
    {
        const int InitialOutputCapacity = 4096;
        const int ReadBufferBytes = 8192;
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new BoundedByteMemoryBuilder(InitialOutputCapacity, maximumBytes, destinationBudget);
        using var scratch = destinationBudget.Reserve(Math.Min(ReadBufferBytes, maximumBytes + 1));
        var buffer = new byte[(int)scratch.Bytes];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length, output.RemainingLength + 1);
            var read = await host.ReadBytesAsync(descriptor, buffer.AsMemory(0, length), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read.IsError)
                return new BoundedReadResult.Failed(read);

            chargeRead(read.BytesRead);
            cancellationToken.ThrowIfCancellationRequested();
            if (!output.TryAppend(buffer.AsSpan(0, read.BytesRead)))
                return new BoundedReadResult.TooLarge();
            if (read.ReachedEoF)
                return new BoundedReadResult.Complete(output.DetachSnapshot());
        }
    }

    /// <summary>
    /// Closes an owned descriptor and awaits terminal cleanup.
    /// </summary>
    /// <remarks>Runs without cancellation. The caller retains ownership tracking and any earlier failure.</remarks>
    /// <param name="host">The host that owns the handle.</param>
    /// <param name="descriptor">An owned handle to close, never a borrowed handle.</param>
    /// <param name="operationFailed">True when the caller already has a failed status or exception; suppresses cleanup failures.</param>
    /// <returns>The expected cleanup failure, unless an earlier failure takes precedence.</returns>
    public static async Task<OperationError?> CloseOwnedDescriptorAsync(
        this IBashHost host,
        FileDescriptor descriptor,
        bool operationFailed)
    {
        var result = await ShellDescriptorCleanup.TryCloseOwnedAsync(host, descriptor).ConfigureAwait(false);
        if (operationFailed)
            return null;
        result.Exception?.Throw();
        return result.Error;
    }

    /// <summary>Copies a bounded byte read into caller-owned storage.</summary>
    public static ValueTask<ByteReadResult> ReadBytesAsync(this IBashHost host,
        FileDescriptor descriptor, Memory<byte> buffer, CancellationToken cancellationToken) =>
        DescriptorReader.CopyAsync((host, descriptor),
            static (state, request, token) => state.host.ReadAsync(state.descriptor, request, token), buffer, cancellationToken);

    /// <summary>Frames retained LF-delimited batches above the host's byte-read contract.</summary>
    public static ValueTask<ReadResult> ReadLinesAsync(this IBashHost host,
        FileDescriptor descriptor, int limit, CancellationToken cancellationToken) =>
        DescriptorReader.ReadLinesAsync((host, descriptor),
            static (state, request, token) => state.host.ReadAsync(state.descriptor, request, token),
            limit, destinationBudget: null, cancellationToken);

    /// <summary>Copies LF-delimited batches into the supplied owner, including their line offsets.
    /// All consumers of the returned bytes must finish before that owner is disposed.</summary>
    public static ValueTask<ReadResult> ReadLinesAsync(this IBashHost host,
        FileDescriptor descriptor, int limit, IMemoryBudget destinationBudget, CancellationToken cancellationToken) =>
        DescriptorReader.ReadLinesAsync((host, descriptor),
            static (state, request, token) => state.host.ReadAsync(state.descriptor, request, token),
            limit, destinationBudget, cancellationToken);

    /// <summary>
    /// Reads a single line from a descriptor, preserving the previous single-line behavior.
    /// </summary>
    public static ValueTask<ReadResult> ReadLineAsync(
        this IBashHost host,
        FileDescriptor fileDescriptor,
        CancellationToken cancellationToken) =>
        host.ReadLinesAsync(fileDescriptor, 1, cancellationToken);

    /// <summary>
    /// Writes UTF-8 text to a descriptor.
    /// </summary>
    public static ValueTask<DescriptorWriteResult> WriteAsync(
        this IBashHost host,
        FileDescriptor fileDescriptor,
        string content,
        CancellationToken cancellationToken) =>
        host.WriteAsync(fileDescriptor, Utf8Text.Encode(content), cancellationToken);

    /// <summary>Reads the remaining descriptor bytes using the shared memory allowance.</summary>
    /// <remarks>The caller retains descriptor ownership. Failures discard partial output.</remarks>
    public static ValueTask<ReadOnlyMemory<byte>> ReadToEndAsync(
        this IBashHost host,
        FileDescriptor descriptor,
        CancellationToken cancellationToken) =>
        host.ReadToEndAsync(descriptor, host.MemoryBudget, cancellationToken);

    /// <summary>Reads remaining bytes into the supplied owner; consumers must finish before it is disposed.</summary>
    public static async ValueTask<ReadOnlyMemory<byte>> ReadToEndAsync(
        this IBashHost host,
        FileDescriptor descriptor,
        IMemoryBudget destinationBudget,
        CancellationToken cancellationToken)
    {
        var result = await host.TryReadAllBytesAsync(descriptor,
            ByteMemoryBuilder.MaxMemoryBuilderBytes - 1, static _ => { }, destinationBudget, cancellationToken).ConfigureAwait(false);
        return result.GetContentOrThrow();
    }

    /// <summary>
    /// Reads the full text contents of a file from the host.
    /// Throws when the file cannot be opened.
    /// </summary>
    /// <remarks>Reserves input capacity and decoded text from the shared governor.</remarks>
    public static Task<string> ReadFileAsync(
        this IBashHost host,
        FilePathLookup path,
        CancellationToken cancel) =>
        host.ReadFileAsync(path, host.MemoryBudget, cancel);

    /// <summary>Reads decoded text into the supplied owner; consumers must finish before it is disposed.</summary>
    public static async Task<string> ReadFileAsync(
        this IBashHost host,
        FilePathLookup path,
        IMemoryBudget destinationBudget,
        CancellationToken cancel)
    {
        var result = await host.TryReadFileAsync(path, destinationBudget, cancel).ConfigureAwait(false);
        result.ThrowIfReadFailed();
        return result.Content ?? throw new ArgumentException($"Cannot open file '{path.Path}'", nameof(path));
    }

    /// <summary>Reads a captured resolution using the host's memory allowance.</summary>
    public static Task<string> ReadFileAsync(
        this IBashHost host, FilePathLookup path, FilePathResolutionResult resolved, CancellationToken cancel) =>
        host.ReadFileAsync(path, resolved, host.MemoryBudget, cancel);

    /// <summary>Reads a captured version into the supplied owner, without resolving its path again.</summary>
    public static async Task<string> ReadFileAsync(
        this IBashHost host, FilePathLookup path, FilePathResolutionResult resolved,
        IMemoryBudget destinationBudget, CancellationToken cancel)
    {
        var result = await host.TryReadFileAsync(path, resolved, destinationBudget, cancel).ConfigureAwait(false);
        result.ThrowIfReadFailed();
        return result.Content ?? throw new ArgumentException($"Cannot open file '{path.Path}'", nameof(path));
    }

    /// <summary>Reads a complete text file, returning null only for a known absent or non-file path.</summary>
    /// <remarks>
    /// Uses the shared memory allowance. Open, read, size, cancellation and cleanup failures
    /// propagate instead of becoming absence.
    /// </remarks>
    public static async Task<string?> ReadFileIfExistsAsync(
        this IBashHost host,
        FilePathLookup path,
        CancellationToken cancel)
    {
        var result = await host.TryReadFileAsync(path, cancel).ConfigureAwait(false);
        result.ThrowIfReadFailed();
        if (result.Outcome == TextFileReadOutcome.OpenFailed)
            throw new IOException(result.Error?.Message);
        return result.Content;
    }

    /// <summary>Reads a complete text file without throwing for returned open/descriptor failures or size rejection.</summary>
    /// <remarks>
    /// Input capacity and decoded text use the shared memory allowance.
    /// Partial text is discarded. Provider exceptions and cancellation propagate;
    /// cleanup failures propagate only when there is no earlier read or size failure.
    /// </remarks>
    public static Task<TextFileReadResult> TryReadFileAsync(
        this IBashHost host,
        FilePathLookup path,
        CancellationToken cancel) =>
        host.TryReadFileAsync(path, host.MemoryBudget, cancel);

    /// <summary>Reads decoded text under an explicit owner, preserving read and cleanup failures.</summary>
    public static async Task<TextFileReadResult> TryReadFileAsync(
        this IBashHost host,
        FilePathLookup path,
        IMemoryBudget destinationBudget,
        CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var resolved = await host.ResolveFileAsync(path, cancel).ConfigureAwait(false);
        return await host.TryReadFileAsync(path, resolved, destinationBudget, cancel).ConfigureAwait(false);
    }

    /// <summary>Reads a captured resolution without retrying its path, with the same limits and cleanup as the path overload.</summary>
    public static Task<TextFileReadResult> TryReadFileAsync(
        this IBashHost host, FilePathLookup path, FilePathResolutionResult resolved, CancellationToken cancel) =>
        host.TryReadFileAsync(path, resolved, host.MemoryBudget, cancel);

    /// <summary>Reads a captured version into the supplied owner, without resolving its path again.</summary>
    public static async Task<TextFileReadResult> TryReadFileAsync(
        this IBashHost host, FilePathLookup path, FilePathResolutionResult resolved,
        IMemoryBudget destinationBudget, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (resolved.File == null)
            throw new ArgumentException("The resolution must include a snapshot result.", nameof(resolved));
        if (resolved.File?.Snapshot is not { } snapshot)
        {
            if (resolved.File?.Outcome == FileSnapshotOutcome.NotFile)
                return TextFileReadResult.NotFile;
            return TextFileReadResult.OpenFailed(resolved.File?.Error ?? resolved.Error
                ?? new OperationError($"Cannot open file '{path.Path}'\n"));
        }
        var opened = await host.OpenReadAsync(snapshot, FileReadOpenMode.Deferred, cancel).ConfigureAwait(false);
        if (opened.FileDescriptor is not { } descriptor)
        {
            cancel.ThrowIfCancellationRequested();
            return TextFileReadResult.OpenFailed(opened.Error ?? new OperationError($"Cannot open file '{path.Path}'\n"));
        }

        var readFailed = false;
        Exception? failure = null;
        TextFileReadResult text;
        OperationError? closeError = null;
        IMemoryReservation? decodedMemory = null;
        try
        {
            using var inputMemory = destinationBudget.CreateScope();
            var result = await host.TryReadAllBytesAsync(descriptor,
                ByteMemoryBuilder.MaxMemoryBuilderBytes - 1, static _ => { }, inputMemory, cancel).ConfigureAwait(false);
            readFailed = result is not BoundedReadResult.Complete;
            text = result switch
            {
                BoundedReadResult.Complete complete => TextFileReadResult.Completed(
                    ScopedMemory.DecodeUtf8(complete.Content, destinationBudget, 0, out decodedMemory)),
                BoundedReadResult.Failed failed => TextFileReadResult.ReadFailed(
                    ReadResult.Failure(failed.Failure.Error ?? OperationError.BadFileDescriptor)),
                BoundedReadResult.TooLarge => TextFileReadResult.TooLarge,
                _ => throw new InvalidOperationException("Unexpected whole-input read outcome.")
            };
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var cleanup = await ShellDescriptorCleanup.TryCloseOwnedAsync(host, descriptor).ConfigureAwait(false);
            if (failure == null && !readFailed)
            {
                if (cleanup.Exception != null || cleanup.Error != null)
                    decodedMemory?.Dispose();
                cleanup.Exception?.Throw();
                closeError = cleanup.Error;
            }
        }
        return closeError is { } closeFailure ? TextFileReadResult.ReadFailed(ReadResult.Failure(closeFailure)) : text;
    }

    /// <summary>
    /// Writes the full text contents of a file through the host.
    /// The file is truncated or created if the host supports it.
    /// </summary>
    public static async Task<bool> WriteFileAsync(
        this IBashHost host,
        FilePathLookup path,
        string content,
        CancellationToken cancel)
    {
        var descriptor = (await host.OpenWritableFileAsync(path, WritableFileOpenMode.WriteTruncate, FileCreationMode.OpenOrCreate, FileWriteRetention.AllowInMemory, cancel).ConfigureAwait(false)).FileDescriptor;
        if (descriptor is not { } openedDescriptor)
            return false;

        var completed = false;
        try
        {
            completed = (await host.WriteAsync(openedDescriptor, content, cancel).ConfigureAwait(false)).CanAcceptMore;
        }
        finally
        {
            var error = await host.CloseOwnedDescriptorAsync(openedDescriptor, operationFailed: !completed).ConfigureAwait(false);
            completed &= error == null;
        }
        return completed;
    }

    /// <summary>Opens a file under the creation policy, truncates it, and writes the remaining contents of a stream.</summary>
    /// <remarks>
    /// Uses IBashHost.OpenWritableFileAsync to validate traversal and creation policy before reading input.
    /// Selects FileWriteRetention.ReleaseOnClose so completed outputs do not accumulate in host file buffers.
    /// Borrows the input without closing it, copies with bounded transfer buffers, and awaits output cleanup.
    /// Input may read from the same host. Failure can leave a partial file; input, storage and cancellation
    /// exceptions propagate, and cleanup preserves an earlier failure.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The creation mode is unsupported.</exception>
    /// <returns>
    /// Null on success, or an expected destination-open, cleanup, or publication failure.
    /// Open failures are classified as for IBashHost.OpenWritableFileAsync.
    /// Supply diagnostic detail without command or operand prefixes; callers format the original path.
    /// </returns>
    public static async Task<OperationError?> WriteFileAsync(
        this IBashHost host,
        FilePathLookup path, Stream content, FileCreationMode mode, CancellationToken cancel)
    {
        var opened = await host.OpenWritableFileAsync(path, WritableFileOpenMode.WriteTruncate, mode,
            FileWriteRetention.ReleaseOnClose, cancel).ConfigureAwait(false);
        if (opened.FileDescriptor is not { } descriptor)
            return opened.Error ?? new OperationError("Cannot create\n");

        Exception? failure = null;
        OperationError? closeError = null;
        try
        {
            using var output = host.CreateDescriptorWriteStream(descriptor, cancel);
            await content.CopyToAsync(output, cancel).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            closeError = await host.CloseOwnedDescriptorAsync(descriptor, operationFailed: failure != null).ConfigureAwait(false);
        }
        return closeError;
    }

    /// <summary>
    ///     Creates a write-only stream that pushes bytes into the given host descriptor.
    /// </summary>
    /// <remarks>
    ///     Writes throw <see cref="ClosedPipeException"/> when the downstream reader closes,
    ///     or <see cref="IOException"/> when the descriptor rejects the write.
    /// </remarks>
    public static Stream CreateDescriptorWriteStream(this IBashHost host, int descriptor, CancellationToken cancel) =>
        CreateDescriptorWriteStream(descriptor, (content, token) => host.WriteAsync(descriptor, content, token), cancel);

    public static Stream CreateDescriptorWriteStream(
        FileDescriptor descriptor,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<DescriptorWriteResult>> write,
        CancellationToken cancel) => new HostDescriptorWriteStream(write, descriptor, cancel);

    /// <summary>
    ///     Provides a write-only stream that forwards bytes into an <see cref="IBashHost"/>'s descriptor.
    /// </summary>
    private sealed class HostDescriptorWriteStream : Stream
    {
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<DescriptorWriteResult>> _write;
        private readonly FileDescriptor _descriptor;
        private readonly CancellationToken _cancel;
        private DescriptorWriteResult _result = DescriptorWriteResult.Open;

        public HostDescriptorWriteStream(
            Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<DescriptorWriteResult>> write,
            FileDescriptor descriptor, CancellationToken cancel)
        {
            _write = write;
            _descriptor = descriptor;
            _cancel = cancel;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.Length == 0)
                return;

            using var cancellation = CancellationTokenScope.Combine(_cancel, cancellationToken);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_result.CanAcceptMore)
                _result = await _write(buffer, cancellation.Token).ConfigureAwait(false);
            if (_result.Error is { } error)
                throw new ShellDescriptorWriteException(_descriptor, error);
            if (!_result.CanAcceptMore)
                throw new ClosedPipeException();
        }
    }
}
