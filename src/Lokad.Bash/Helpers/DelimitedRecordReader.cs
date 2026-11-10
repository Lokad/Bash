using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Files;

namespace Lokad.Bash.Helpers;

/// <summary>Controls how long raw record contents remain valid.</summary>
public enum DelimitedRecordStorage
{
    /// <summary>Record contents remain valid after subsequent reads.</summary>
    Retained,

    /// <summary>Record contents may be overwritten by the next read.</summary>
    Reusable
}

/// <summary>Whether an oversized record is discarded or stops the read immediately.</summary>
public enum DelimitedRecordOverflow { Discard, Stop }

/// <summary>Reads byte records separated by a caller-selected terminator.</summary>
public sealed class DelimitedRecordReader : IDisposable
{
    public const int RawReadBufferSize = 64 * 1024;
    private const int ArrayHeaderBytes = 32;
    /// <summary>Storage still owned after a reusable reader's current record is consumed.</summary>
    public const int RawReadBufferMemoryBytes = RawReadBufferSize + ArrayHeaderBytes;

    /// <summary>Estimates backing capacity and overhead for a retained record from this reader.</summary>
    public static long GetRetainedMemoryBytes(ReadOnlyMemory<byte> content)
    {
        const long SplitRecordCapacityFactor = 2;
        const long RecordBufferOverheadBytes = 64;
        return Math.Max(RawReadBufferSize, SplitRecordCapacityFactor * content.Length) + RecordBufferOverheadBytes;
    }

    private readonly IBashHost _host;
    private readonly FileDescriptor _inputDescriptor;
    private readonly byte _terminator;
    private readonly DelimitedRecordStorage _storage;
    private readonly Action<long> _reserveMemory;
    private readonly bool _keepBorrow;
    private readonly CancellationToken _cancellationToken;
    private byte[]? _readBuffer;
    private int _bufferWriteOffset;
    private ByteMemoryBuilder? _splitRecord;
    private bool _oversized;
    private bool _reachedEnd;
    private ByteReadResult _readFailure;
    private bool _disposed;
    private DescriptorReadBuffer _borrow;
    private bool _hasBorrow;
    private int _borrowOffset;

    /// <param name="reserveMemory">
    /// Reserves a conservative allocation allowance before allocating buffers, throwing when
    /// exhausted. Charges include growth and headers and are never refunded by the reader.
    /// </param>
    /// <param name="keepBorrow">
    /// Amortizes host reads across records. ReleaseBorrow or Dispose must precede any other
    /// operation on the input descriptor, including output or diagnostics aliased to that input.
    /// </param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    /// <param name="host">The application-owned I/O boundary.</param>
    /// <param name="inputDescriptor">The descriptor containing input records.</param>
    /// <param name="storage">Controls whether returned record bytes remain valid after later reads.</param>
    /// <param name="terminator">The byte separating input records.</param>
    public DelimitedRecordReader(
        IBashHost host,
        FileDescriptor inputDescriptor,
        byte terminator,
        DelimitedRecordStorage storage,
        Action<long> reserveMemory,
        bool keepBorrow,
        CancellationToken cancellationToken)
    {
        _host = host;
        _inputDescriptor = inputDescriptor;
        _terminator = terminator;
        _storage = storage;
        _reserveMemory = reserveMemory;
        _keepBorrow = keepBorrow;
        _cancellationToken = cancellationToken;
    }

    public ValueTask<DelimitedRecord?> ReadNextAsync() => ReadNextBoundedAsync(int.MaxValue);

    /// <summary>Discards records exceeding the byte limit, returning one oversized marker per record.</summary>
    public ValueTask<DelimitedRecord?> ReadNextBoundedAsync(int maximumContentBytes) =>
        ReadNextBoundedAsync(maximumContentBytes, DelimitedRecordOverflow.Discard);

    /// <summary>Returns an oversized marker, either after discarding the record or immediately at the limit.</summary>
    public ValueTask<DelimitedRecord?> ReadNextBoundedAsync(int maximumContentBytes, DelimitedRecordOverflow overflow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumContentBytes);
        if (_keepBorrow && TryReadBufferedRecord(maximumContentBytes, out var record))
            return ValueTask.FromResult<DelimitedRecord?>(record);
        return ReadNextCoreAsync(maximumContentBytes);

        async ValueTask<DelimitedRecord?> ReadNextCoreAsync(int maximumContentBytes)
        {
            try
            {
                while (true)
                {
                    if (_reachedEnd)
                    {
                        if (_splitRecord == null && !_oversized)
                            return null;
                        return CompleteRecord(ReadOnlyMemory<byte>.Empty, hasTerminator: false);
                    }
                    if (_readBuffer == null || _bufferWriteOffset == RawReadBufferSize)
                    {
                        _reserveMemory(RawReadBufferMemoryBytes);
                        _readBuffer = new byte[RawReadBufferSize];
                        _bufferWriteOffset = 0;
                    }
                    var destination = _readBuffer.AsMemory(_bufferWriteOffset, RawReadBufferSize - _bufferWriteOffset);
                    if (!_hasBorrow)
                    {
                        _borrow = await _host.ReadAsync(_inputDescriptor, DescriptorReadRequest.UpTo(destination.Length), _cancellationToken)
                            .ConfigureAwait(false);
                        _hasBorrow = true;
                    }
                    var bytes = _borrow.Content[_borrowOffset..];
                    if (_borrow.Result.IsError && !_readFailure.IsError)
                        _readFailure = _borrow.Result;
                    if (TryReadBufferedRecord(maximumContentBytes, out var buffered))
                        return buffered;
                    var terminatorOffset = bytes.Span.IndexOf(_terminator);
                    var consumed = terminatorOffset < 0 ? bytes.Length : terminatorOffset + 1;
                    bytes[..consumed].CopyTo(destination);
                    if (_storage == DelimitedRecordStorage.Retained)
                        _bufferWriteOffset += consumed;
                    if (consumed == bytes.Length)
                    {
                        _reachedEnd = _borrow.Result.IsError || _borrow.Result.ReachedEoF;
                    }
                    var content = destination[..(terminatorOffset < 0 ? consumed : terminatorOffset)];
                    if (terminatorOffset >= 0)
                    {
                        var record = CompleteRecord(content, hasTerminator: true);
                        Advance(consumed);
                        return record;
                    }
                    if (_reachedEnd && _splitRecord == null && !_oversized)
                    {
                        if (content.IsEmpty)
                            return null;
                        var record = CompleteRecord(content, hasTerminator: false);
                        Advance(consumed);
                        return record;
                    }
                    AppendSplitRecord(content);
                    if (_oversized && overflow == DelimitedRecordOverflow.Stop)
                        return CompleteRecord(ReadOnlyMemory<byte>.Empty, hasTerminator: false);
                    Advance(consumed);
                }
            }
            finally
            {
                if (!_keepBorrow)
                    ReleaseBorrow();
            }

            void AppendSplitRecord(ReadOnlyMemory<byte> content)
            {
                if (_oversized)
                    return;
                if (content.Length > maximumContentBytes - (_splitRecord?.Length ?? 0))
                {
                    _splitRecord = null;
                    _oversized = true;
                    return;
                }
                // Geometric growth allocates less than four times the appended payload cumulatively.
                // Include array/object overhead and reserve before the constructor or any resize.
                _reserveMemory(4L * content.Length + 64);
                _splitRecord ??= ByteMemoryBuilder.CreateUnaccounted(content.Length);
                _splitRecord.Append(content);
            }

            DelimitedRecord CompleteRecord(ReadOnlyMemory<byte> content, bool hasTerminator)
            {
                if (_oversized || content.Length > maximumContentBytes - (_splitRecord?.Length ?? 0))
                {
                    _splitRecord = null;
                    _oversized = false;
                    return new DelimitedRecord(ReadOnlyMemory<byte>.Empty, hasTerminator) { ExceedsLimit = true };
                }
                if (_splitRecord != null)
                {
                    AppendSplitRecord(content);
                    content = _splitRecord.ToMemory();
                    _splitRecord = null;
                }
                return new DelimitedRecord(content, hasTerminator);
            }
        }
    }

    /// <summary>Commits processed records; returned contents remain valid under the chosen storage policy.</summary>
    public void ReleaseBorrow()
    {
        if (!_hasBorrow)
            return;
        _borrow.Consume(_borrowOffset);
        _borrow.Dispose();
        _borrow = default;
        _hasBorrow = false;
        _borrowOffset = 0;
    }

    private bool TryReadBufferedRecord(int maximumContentBytes, out DelimitedRecord record)
    {
        record = default;
        if (!_hasBorrow || _splitRecord != null || _oversized || _readBuffer is not { } destination)
            return false;
        var bytes = _borrow.Content[_borrowOffset..];
        var terminatorOffset = bytes.Span.IndexOf(_terminator);
        if (terminatorOffset < 0)
            return false;
        var consumed = terminatorOffset + 1;
        bytes[..consumed].CopyTo(destination.AsMemory(_bufferWriteOffset));
        record = terminatorOffset > maximumContentBytes
            ? new DelimitedRecord(ReadOnlyMemory<byte>.Empty, true) { ExceedsLimit = true }
            : new DelimitedRecord(destination.AsMemory(_bufferWriteOffset, terminatorOffset), true);
        if (_storage == DelimitedRecordStorage.Retained)
            _bufferWriteOffset += consumed;
        if (consumed == bytes.Length)
            _reachedEnd = _borrow.Result.IsError || _borrow.Result.ReachedEoF;
        Advance(consumed);
        return true;
    }

    private void Advance(int count)
    {
        _borrowOffset += count;
        if (_borrowOffset == _borrow.Content.Length)
            ReleaseBorrow();
    }

    /// <summary>The terminal failed read, or default when no failure has occurred.</summary>
    /// <remarks>Check IsError for failure; this is not an end-of-input indicator and buffered records may remain.</remarks>
    public ByteReadResult ReadFailure => _readFailure;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseBorrow();
        _splitRecord = null;
        _readBuffer = null;
    }
}

/// <summary>A delimiter-free record and whether the input contained its terminator.</summary>
public readonly record struct DelimitedRecord(ReadOnlyMemory<byte> Content, bool HasTerminator)
{
    /// <summary>The reader discarded the contents because they exceeded its byte limit.</summary>
    public bool ExceedsLimit { get; init; }
}
