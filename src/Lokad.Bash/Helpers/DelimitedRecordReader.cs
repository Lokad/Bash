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
    /// <remarks>Stop leaves the fragment that exceeded the limit unread and returns a marker with
    /// no terminator. The reader keeps any previously admitted prefix for a later read.</remarks>
    public ValueTask<DelimitedRecord?> ReadNextBoundedAsync(int maximumContentBytes, DelimitedRecordOverflow overflow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumContentBytes);
        return ReadNextCoreAsync(maximumContentBytes, overflow);

        async ValueTask<DelimitedRecord?> ReadNextCoreAsync(int maximumContentBytes, DelimitedRecordOverflow overflow)
        {
            try
            {
                while (!_reachedEnd)
                {
                    if (!_hasBorrow)
                    {
                        var available = _bufferWriteOffset == RawReadBufferSize ? RawReadBufferSize : RawReadBufferSize - _bufferWriteOffset;
                        _borrow = await _host.ReadAsync(_inputDescriptor, DescriptorReadRequest.UpTo(available), _cancellationToken)
                            .ConfigureAwait(false);
                        _hasBorrow = true;
                    }
                    var bytes = _borrow.Content[_borrowOffset..];
                    if (_borrow.Result.IsError && !_readFailure.IsError)
                        _readFailure = _borrow.Result;
                    var source = bytes.Span;
                    var terminatorOffset = source.IndexOf(_terminator);
                    var hasTerminator = terminatorOffset >= 0;
                    var contentLength = hasTerminator ? terminatorOffset : source.Length;
                    var consumed = hasTerminator ? contentLength + 1 : contentLength;
                    var completes = hasTerminator || _borrow.Result.IsError || _borrow.Result.ReachedEoF;
                    ReadOnlyMemory<byte> content = default;
                    if (_oversized || contentLength > maximumContentBytes - (_splitRecord?.Length ?? 0))
                    {
                        if (overflow == DelimitedRecordOverflow.Stop)
                            return OversizedRecord(hasTerminator: false);
                        _oversized = true;
                        _splitRecord = null;
                    }
                    else if (completes && _splitRecord == null)
                    {
                        if (consumed != 0)
                            content = CopyRecord(source[..consumed])[..contentLength];
                    }
                    else
                    {
                        AppendSplitRecord(source[..contentLength]);
                        if (completes && _splitRecord is { } splitRecord)
                        {
                            content = splitRecord.ToMemory();
                            _splitRecord = null;
                        }
                    }

                    Advance(consumed);
                    if (!completes)
                        continue;
                    if (_oversized)
                    {
                        _oversized = false;
                        return OversizedRecord(hasTerminator);
                    }
                    return hasTerminator || !content.IsEmpty ? new DelimitedRecord(content, hasTerminator) : null;
                }
                return null;
            }
            finally
            {
                if (!_keepBorrow)
                    ReleaseBorrow();
            }

            ReadOnlyMemory<byte> CopyRecord(ReadOnlySpan<byte> content)
            {
                if (_readBuffer == null || _bufferWriteOffset == RawReadBufferSize)
                {
                    _reserveMemory(RawReadBufferMemoryBytes);
                    _readBuffer = new byte[RawReadBufferSize];
                    _bufferWriteOffset = 0;
                }
                var destination = _readBuffer.AsMemory(_bufferWriteOffset, content.Length);
                content.CopyTo(destination.Span);
                if (_storage == DelimitedRecordStorage.Retained)
                    _bufferWriteOffset += content.Length;
                return destination;
            }

            void AppendSplitRecord(ReadOnlySpan<byte> content)
            {
                if (content.IsEmpty)
                    return;
                // Geometric growth allocates less than four times the appended payload cumulatively.
                // Include array/object overhead and reserve before the constructor or any resize.
                _reserveMemory(4L * content.Length + 64);
                _splitRecord ??= ByteMemoryBuilder.CreateUnaccounted(content.Length);
                _splitRecord.Append(content);
            }

            static DelimitedRecord OversizedRecord(bool hasTerminator) =>
                new(ReadOnlyMemory<byte>.Empty, hasTerminator) { ExceedsLimit = true };
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

    private void Advance(int count)
    {
        _borrowOffset += count;
        if (_borrowOffset == _borrow.Content.Length)
        {
            _reachedEnd = _borrow.Result.IsError || _borrow.Result.ReachedEoF;
            ReleaseBorrow();
        }
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
    /// <summary>The record exceeded the caller's byte limit.</summary>
    public bool ExceedsLimit { get; init; }
}
