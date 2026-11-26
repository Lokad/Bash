using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Shared copying and record framing over bounded descriptor borrows.</summary>
public static class DescriptorReader
{
    public const int BufferSize = 64 * 1024;

    // A retained line batch may use a whole existing contiguous source. Streaming providers
    // choose bounded windows and reserve lookahead growth against the shared allowance.

    public static ValueTask<ByteReadResult> CopyAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (destination.IsEmpty)
            throw new ArgumentException("The read buffer must not be empty.", nameof(destination));
        var pending = read(source, DescriptorReadRequest.UpTo(destination.Length), cancellationToken);
        return pending.IsCompletedSuccessfully
            ? ValueTask.FromResult(Copy(pending.Result, destination))
            : Await(pending, destination);

        static ByteReadResult Copy(DescriptorReadBuffer buffer, Memory<byte> destination)
        {
            using (buffer)
            {
                buffer.Content.CopyTo(destination);
                buffer.Consume(buffer.Content.Length);
                return buffer.Result;
            }
        }

        static async ValueTask<ByteReadResult> Await(ValueTask<DescriptorReadBuffer> pending, Memory<byte> destination) =>
            Copy(await pending.ConfigureAwait(false), destination);
    }

    /// <summary>Retains up to the line limit and consumes nothing until the batch is ready.
    /// A final unterminated record counts toward the limit when EOF is known.</summary>
    /// <param name="destinationBudget">Owns copied bytes and line offsets when supplied;
    /// otherwise the descriptor's owner retains the batch.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    /// <param name="limit">The positive maximum number of records to return.</param>
    /// <param name="read">Acquires the next borrowed descriptor buffer.</param>
    /// <param name="source">The source object passed to the read callback.</param>
    public static ValueTask<ReadResult> ReadLinesAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        int limit, IMemoryBudget? destinationBudget, CancellationToken cancellationToken)
        => ReadLinesAsync(source, read, limit, destinationBudget, sourceDestination: null, cancellationToken);

    internal static ValueTask<ReadResult> ReadLinesAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        int limit, IMemoryBudget? destinationBudget, SourceLineDestination? sourceDestination, CancellationToken cancellationToken)
        => ReadRecordsCoreAsync(source, read, (byte)'\n', limit, destinationBudget, reusableLine: null, sourceDestination,
            static batch => batch.AsLines(), cancellationToken);

    /// <summary>Copies one LF record into caller-owned storage, valid until the next read.</summary>
    public static ValueTask<ReadResult> ReadReusableLineAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        ByteMemoryBuilder output, CancellationToken cancellationToken) =>
        ReadRecordsCoreAsync(source, read, (byte)'\n', 1, destinationBudget: null, output, sourceDestination: null,
            static batch => batch.AsLines(), cancellationToken);

    /// <summary>Retains records and offsets in the supplied destination budget.</summary>
    internal static ValueTask<DescriptorRecordBatch> ReadRecordBatchAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        byte delimiter, int limit, IMemoryBudget destinationBudget, CancellationToken cancellationToken) =>
        ReadRecordsCoreAsync(source, read, delimiter, limit, destinationBudget, reusableLine: null, sourceDestination: null,
            static batch => batch, cancellationToken);

    private static async ValueTask<TBatch> ReadRecordsCoreAsync<T, TBatch>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        byte delimiter, int limit, IMemoryBudget? destinationBudget, ByteMemoryBuilder? reusableLine,
        SourceLineDestination? sourceDestination,
        Func<DescriptorRecordBatch, TBatch> project, CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var maximumBatchBytes = sourceDestination is { } sourceOutput
            ? sourceOutput.MaximumBytes - sourceOutput.Buffer.Length : int.MaxValue;
        var minimum = 1;
        var examined = 0;
        (DescriptorReadBufferOwner? Owner, long Offset) position = default;
        while (true)
        {
            // One extra byte distinguishes a final record at the limit from an oversized one.
            // A copied batch should not materialize an entire contiguous source. Long records
            // can still grow through lookahead; BufferSize is a transfer window, not a record cap.
            // Keep that window when continuing a short prefix left by BOM detection or a byte read.
            var maximum = ByteMemoryBuilder.MaxMemoryBuilderBytes + 1;
            if ((destinationBudget != null || reusableLine != null) && minimum <= BufferSize)
                maximum = BufferSize;
            else if (minimum == 1)
                maximum = int.MaxValue;
            using var buffer = await read(source, new DescriptorReadRequest(minimum, maximum, position), cancellationToken).ConfigureAwait(false);
            var result = buffer.Result;
            if (result.Error is { } error)
                return project(new DescriptorRecordBatch(default, Array.Empty<int>(),
                    ByteReadResult.Failure(0, error, reachedEoF: false)));
            var bytes = buffer.Content;
            if (bytes.IsEmpty)
                return project(new DescriptorRecordBatch(default, Array.Empty<int>(), ByteReadResult.EndOfFile));
            // Lookahead preserves the prefix. Scan it once unless another alias advanced the cursor.
            if (position != buffer.Position)
                examined = 0;
            // A resumed batch may include following records from one transfer window of new bytes.
            var batchLimit = examined > 0 && (bytes.Length - examined > BufferSize || bytes.Length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                ? 1 : limit;
            var length = ByteLines.FindCompleteRecords(bytes.Span, delimiter, batchLimit, examined, result.ReachedEoF,
                maximumBatchBytes, destinationBudget ?? buffer.MemoryBudget, out var offsets, out var offsetsMemory);
            if (length > maximumBatchBytes)
            {
                buffer.Consume(length);
                return project(new DescriptorRecordBatch(default, Array.Empty<int>(), ByteReadResult.TooLarge));
            }
            if (examined > 0 && (length == 0 ? bytes.Length : length) > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            if (length != 0 || result.ReachedEoF)
            {
                if (length == 0)
                    length = bytes.Length;
                var reachedEof = result.ReachedEoF && length == bytes.Length;
                ReadOnlyMemory<byte> content;
                try
                {
                    if (reusableLine != null)
                    {
                        reusableLine.Truncate(0);
                        reusableLine.Append(bytes[..length]);
                        content = reusableLine.ToMemory();
                    }
                    else if (sourceDestination is { } destination
                        && (length <= destination.Buffer.Capacity - destination.Buffer.Length
                            || destination.Buffer.Length == 0 && reachedEof))
                    {
                        // Use admitted capacity, or allocate the final exact buffer for a complete first batch.
                        var start = destination.Buffer.Length;
                        destination.Buffer.Append(bytes.Span[..length], exactCapacity: reachedEof);
                        content = destination.Buffer.ToMemory()[start..];
                    }
                    else if (destinationBudget == null || (sourceDestination != null && buffer.CanRetain))
                        content = buffer.Retain(length);
                    else
                        content = ScopedMemory.Copy(bytes[..length], destinationBudget);
                }
                catch { offsetsMemory?.Dispose(); throw; }
                buffer.Consume(length);
                return project(new DescriptorRecordBatch(content, offsets,
                    ByteReadResult.Success(length, reachedEof)));
            }
            if (bytes.Length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            minimum = bytes.Length + 1;
            examined = bytes.Length;
            position = buffer.Position;
        }
    }
}

/// <summary>Accepts line batches directly into the parser's bounded source buffer.</summary>
internal readonly record struct SourceLineDestination(ByteMemoryBuilder Buffer, int MaximumBytes);

/// <summary>A framed byte batch with starts after the first record, or a terminal read outcome.</summary>
internal readonly record struct DescriptorRecordBatch(ReadOnlyMemory<byte> Content, int[] RecordStartOffsets, ByteReadResult Result)
{
    /// <summary>Projects a batch scanned with LF to the public line result.</summary>
    internal ReadResult AsLines() => Result.IsTooLarge ? ReadResult.TooLarge
        : Result.Error is { } error ? ReadResult.Failure(error)
        : Content.IsEmpty ? ReadResult.EndOfFile
        : ReadResult.FromValidatedLines(Content, RecordStartOffsets, Result.ReachedEoF);
}
