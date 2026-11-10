using System;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Shared copying and LF framing over bounded descriptor borrows.</summary>
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

    /// <summary>Retains a complete LF batch, consuming nothing until the batch is ready.</summary>
    /// <param name="destinationBudget">Owns copied bytes and line offsets when supplied;
    /// otherwise the descriptor's owner retains the batch.</param>
    /// <param name="cancellationToken">Cancels computation and pending reads.</param>
    /// <param name="limit">The positive maximum number of records to return.</param>
    /// <param name="read">Acquires the next borrowed descriptor buffer.</param>
    /// <param name="source">The source object passed to the read callback.</param>
    public static ValueTask<ReadResult> ReadLinesAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        int limit, IMemoryBudget? destinationBudget, CancellationToken cancellationToken)
        => ReadLinesCoreAsync(source, read, limit, destinationBudget, reusableLine: null, cancellationToken);

    /// <summary>Copies one LF record into caller-owned storage, valid until the next read.</summary>
    public static ValueTask<ReadResult> ReadReusableLineAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        ByteMemoryBuilder output, CancellationToken cancellationToken) =>
        ReadLinesCoreAsync(source, read, 1, destinationBudget: null, output, cancellationToken);

    private static async ValueTask<ReadResult> ReadLinesCoreAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        int limit, IMemoryBudget? destinationBudget, ByteMemoryBuilder? reusableLine, CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var minimum = 1;
        var examined = 0;
        (DescriptorReadBufferOwner? Owner, long Offset) position = default;
        while (true)
        {
            // One extra byte distinguishes a final record at the limit from an oversized one.
            // A copied batch should not materialize an entire contiguous source. Long records
            // can still grow through lookahead; BufferSize is a transfer window, not a line cap.
            // Keep that window when continuing a short prefix left by BOM detection or a byte read.
            var maximum = ByteMemoryBuilder.MaxMemoryBuilderBytes + 1;
            if ((destinationBudget != null || reusableLine != null) && minimum <= BufferSize)
                maximum = BufferSize;
            else if (minimum == 1)
                maximum = int.MaxValue;
            using var buffer = await read(source, new DescriptorReadRequest(minimum, maximum, position), cancellationToken).ConfigureAwait(false);
            var result = buffer.Result;
            if (result.Error is { } error)
                return ReadResult.Failure(error);
            var bytes = buffer.Content;
            if (bytes.IsEmpty)
                return ReadResult.EndOfFile;
            // Lookahead preserves the prefix. Scan it once unless another alias advanced the cursor.
            if (position != buffer.Position)
                examined = 0;
            var found = ByteLines.FindCompleteLines(bytes.Span[examined..], examined == 0 ? limit : 1,
                destinationBudget ?? buffer.MemoryBudget, out var offsets, out var offsetsMemory);
            var length = found == 0 ? 0 : examined + found;
            if (examined > 0 && (length == 0 ? bytes.Length : length) > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            if (length != 0 || result.ReachedEoF)
            {
                if (length == 0)
                    length = bytes.Length;
                ReadOnlyMemory<byte> content;
                try
                {
                    if (reusableLine != null)
                    {
                        reusableLine.Truncate(0);
                        reusableLine.Append(bytes[..length]);
                        content = reusableLine.ToMemory();
                    }
                    else
                        content = destinationBudget == null
                            ? buffer.Retain(length)
                            : ScopedMemory.Copy(bytes[..length], destinationBudget);
                }
                catch { offsetsMemory?.Dispose(); throw; }
                buffer.Consume(length);
                return ReadResult.FromValidatedLines(content, offsets, result.ReachedEoF && length == bytes.Length);
            }
            if (bytes.Length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                throw new ByteMemoryBuilder.LimitExceededException();
            minimum = bytes.Length + 1;
            examined = bytes.Length;
            position = buffer.Position;
        }
    }
}
