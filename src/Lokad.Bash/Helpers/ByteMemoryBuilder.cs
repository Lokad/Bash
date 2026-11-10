using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary> Builds a contiguous byte buffer that grows as data is appended. </summary>
/// <remarks>Governed owners reserve capacity and replacement overlap before allocation.</remarks>
public sealed class ByteMemoryBuilder : IDisposable, IByteMemoryWriter
{
    public const int MaxMemoryBuilderBytes = 0x7FFFFFC7; // CLR byte-array representation limit.
    public const int DefaultCapacity = 256;

    private IMemoryReservation? _reservation;
    private readonly IMemoryBudget? _memoryBudget;
    private readonly MemoryCapacity? _prepaidMemory;

    /// <summary> Stores the backing buffer for appended bytes. </summary>
    private byte[] _buffer;

    /// <summary> Tracks the number of bytes written into <see cref="_buffer"/>. </summary>
    private int _length;

    /// <summary>
    /// Creates a buffer whose allocations are accounted by its caller or await migration.
    /// </summary>
    /// <param name="capacity">Initial capacity for the backing buffer.</param>
    // TODO: Give parser and word-expansion callers a memory budget for these buffers.
    // DelimitedRecordReader already prepays its buffer; do not charge it twice.
    // New execution buffers should use a budgeted constructor.
    public static ByteMemoryBuilder CreateUnaccounted(int capacity) => new(capacity);

    private ByteMemoryBuilder(int capacity)
    {
        _buffer = capacity > 0 ? new byte[capacity] : [];
    }

    /// <summary>Reserves backing capacity from the shared allowance before allocation.</summary>
    public ByteMemoryBuilder(int capacity, IMemoryBudget memoryBudget)
    {
        _memoryBudget = memoryBudget;
        _reservation = memoryBudget.Reserve((long)Math.Max(0, capacity));
        try { _buffer = capacity > 0 ? new byte[capacity] : []; }
        catch { _reservation?.Dispose(); throw; }
    }

    /// <summary>Spends a caller-owned allowance; snapshots remain charged until that owner resets it.</summary>
    public ByteMemoryBuilder(int capacity, MemoryCapacity prepaidMemory)
    {
        _prepaidMemory = prepaidMemory;
        prepaidMemory.Grow(Math.Max(0, capacity));
        _buffer = capacity > 0 ? new byte[capacity] : [];
    }

    /// <summary>Releases accounting only when the buffer and its unpublished content are discarded.</summary>
    public void Dispose()
    {
        _buffer = [];
        _length = 0;
        _reservation?.Dispose();
        _reservation = null;
    }

    /// <summary>
    /// Gets the number of bytes written into the buffer.
    /// </summary>
    public int Length => _length;

    /// <summary>Gets retained backing capacity, including unused space after truncation.</summary>
    public int Capacity => _buffer.Length;

    /// <summary> Appends a span of bytes to the buffer. </summary>
    /// <param name="bytes">The bytes to append.</param>
    public ByteMemoryBuilder Append(ReadOnlySpan<byte> bytes)
    {
        if (!TryAppend(bytes, int.MaxValue))
            throw new LimitExceededException();
        return this;
    }

    /// <summary> Appends a memory block of bytes to the buffer. </summary>
    /// <param name="bytes">The bytes to append.</param>
    public ByteMemoryBuilder Append(ReadOnlyMemory<byte> bytes) => Append(bytes.Span);

    /// <summary> Appends a single byte to the buffer. </summary>
    /// <param name="value">The byte to append.</param>
    public ByteMemoryBuilder Append(byte value)
    {
        if (!TryAppend(value, int.MaxValue))
            throw new LimitExceededException();
        return this;
    }

    // Display keeps its previous payload limit; ordinary appends may fill existing
    // capacity. Parser/export writers apply their own smaller bounds.
    public bool TryAppend(ReadOnlySpan<byte> bytes) => TryAppend(bytes, MaxMemoryBuilderBytes - 1);

    public bool TryAppend(byte value) => TryAppend(value, MaxMemoryBuilderBytes - 1);

    /// <summary>Preflights display bytes before allocating encoded names or diagnostics.</summary>
    public bool CanAppend(int count) => CanAppend(count, MaxMemoryBuilderBytes - 1);

    private bool TryAppend(ReadOnlySpan<byte> bytes, int maximumLength)
    {
        if (!CanAppend(bytes.Length, maximumLength))
            return false;
        if (bytes.IsEmpty)
            return true;

        EnsureCapacity(_length + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        return true;
    }

    private bool TryAppend(byte value, int maximumLength)
    {
        if (!CanAppend(1, maximumLength))
            return false;

        EnsureCapacity(_length + 1);
        _buffer[_length++] = value;
        return true;
    }

    public bool TryAppend(int count, byte value)
    {
        if (!CanAppend(count))
            return false;

        AppendSpan(count).Fill(value);
        return true;
    }

    private bool CanAppend(int count, int maximumLength) => count >= 0 && count <= maximumLength - _length;

    bool IByteMemoryWriter.TryAppend(ReadOnlySpan<byte> bytes) => TryAppend(bytes);
    bool IByteMemoryWriter.TryAppend(byte value) => TryAppend(value);
    bool IByteMemoryWriter.TryAppend(int count, byte value) => TryAppend(count, value);

    /// <summary>
    /// Reserves appended bytes and returns the writable slice to fill.
    /// </summary>
    /// <remarks>The returned span is already part of the written payload.</remarks>
    /// <param name="length">The number of bytes to append.</param>
    public Span<byte> AppendSpan(int length)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        EnsureCapacity(checked(_length + length));
        var span = _buffer.AsSpan(_length, length);
        _length += length;
        return span;
    }

    /// <summary>
    /// Truncates the written payload to an earlier length.
    /// </summary>
    /// <param name="length">The new written length.</param>
    public void Truncate(int length)
    {
        if (length < 0 || length > _length)
            throw new ArgumentOutOfRangeException(nameof(length));

        _length = length;
    }

    /// <summary>
    /// Returns the written portion of the backing buffer.
    /// </summary>
    public ReadOnlyMemory<byte> ToMemory() => _length == 0
        ? ReadOnlyMemory<byte>.Empty
        : new ReadOnlyMemory<byte>(_buffer, 0, _length);

    /// <summary>
    /// Ensures the backing buffer can hold the requested capacity.
    /// </summary>
    private void EnsureCapacity(int capacity)
    {
        if (!TryEnsureCapacity(capacity))
            throw new MemoryBudgetExceededException();
    }

    /// <summary>Admits replacement overlap before growing, leaving content unchanged on refusal.</summary>
    public bool TryEnsureCapacity(int capacity)
    {
        if (capacity <= _buffer.Length) return true;
        if (capacity >= MaxMemoryBuilderBytes)
            throw new LimitExceededException();

        var newSize = _buffer.Length == 0 ? 16 : _buffer.Length;
        while (newSize < capacity) newSize = (int)Math.Min(MaxMemoryBuilderBytes, 2L * newSize);

        if (newSize > MaxMemoryBuilderBytes)
            newSize = MaxMemoryBuilderBytes;

        // Reserve the replacement before allocating: the old array still exists during growth.
        // Prepaid owners conservatively retain earlier arrays until their result batch ends.
        _reservation ??= _memoryBudget?.Reserve(0);
        var previousBytes = _reservation?.Bytes ?? 0;
        if (_prepaidMemory is { } prepaid && !prepaid.TryResize(checked(prepaid.Bytes + newSize)))
            return false;
        if (_reservation is { } reservation && !reservation.TryResize(previousBytes + newSize))
            return false;
        try { Array.Resize(ref _buffer, newSize); }
        catch { _reservation?.Resize(previousBytes); throw; }
        _reservation?.Resize((long)newSize);
        return true;
    }

    /// <summary>Leaves the backing array charged to the enclosing scope until that scope is disposed.</summary>
    public ReadOnlyMemory<byte> DetachSnapshot()
    {
        var bytes = _buffer;
        var length = _length;
        _buffer = [];
        _length = 0;
        _reservation = null;
        return bytes.AsMemory(0, length);
    }

    /// <summary>Identifies buffer exhaustion without masking unrelated unsupported operations.</summary>
    public sealed class LimitExceededException() : NotSupportedException("Byte buffer exceeds the CLR array length limit");
}
