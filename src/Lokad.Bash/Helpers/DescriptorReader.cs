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
        => ReadLinesAsync(source, read, limit, destinationBudget, retainImmutable: false, cancellationToken);

    internal static ValueTask<ReadResult> ReadLinesAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        int limit, IMemoryBudget? destinationBudget, bool retainImmutable, CancellationToken cancellationToken)
        => ReadRecordsCoreAsync(source, read, (byte)'\n', limit, destinationBudget, reusableLine: null,
            retainImmutable, static batch => batch.AsLines(), cancellationToken);

    /// <summary>Copies one LF record into caller-owned storage, valid until the next read.</summary>
    public static ValueTask<ReadResult> ReadReusableLineAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        ByteMemoryBuilder output, CancellationToken cancellationToken) =>
        ReadRecordsCoreAsync(source, read, (byte)'\n', 1, destinationBudget: null, output,
            retainImmutable: false, static batch => batch.AsLines(), cancellationToken);

    /// <summary>Retains offsets and any necessary record copy in the supplied destination budget.</summary>
    internal static ValueTask<DescriptorRecordBatch> ReadRecordBatchAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        byte delimiter, int limit, IMemoryBudget destinationBudget, bool retainImmutable, CancellationToken cancellationToken) =>
        ReadRecordsCoreAsync(source, read, delimiter, limit, destinationBudget, reusableLine: null,
            retainImmutable, static batch => batch, cancellationToken);

    /// <summary>Fills the source destination through EOF. The result contains only the terminal outcome.</summary>
    internal static async ValueTask<ReadResult> ReadSourceAsync<T>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        SourceReadDestination destination, IMemoryBudget stagingBudget, CancellationToken cancellationToken)
    {
        var output = destination.Buffer;
        var minimum = 1;
        var examined = 0;
        (DescriptorReadBufferOwner? Owner, long Offset) position = default;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var buffer = await read(source, WindowRequest(minimum, position, bounded: true), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var result = buffer.Result;
            if (result.Error is { } error)
                return ReadResult.Failure(error);
            var bytes = buffer.Content;
            if (bytes.IsEmpty)
                return ReadResult.EndOfFile;

            if (position != buffer.Position)
                examined = 0;
            var length = SourcePrefixLength(bytes.Span, examined, result.ReachedEoF);
            var remaining = destination.MaximumBytes - output.Length;
            if (length > remaining)
            {
                buffer.Consume(remaining + 1);
                return ReadResult.TooLarge;
            }
            if (length == 0)
            {
                if (bytes.Length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
                    throw new ByteMemoryBuilder.LimitExceededException();
                // Keep an incomplete record with the provider while looking ahead.
                minimum = bytes.Length + 1;
                examined = bytes.Length;
                position = buffer.Position;
                continue;
            }

            var reachedEof = result.ReachedEoF && length == bytes.Length;
            var content = bytes[..length];
            if (length <= output.Capacity - output.Length || (output.Length == 0 && reachedEof))
            {
                output.Append(content.Span, exactCapacity: reachedEof);
                buffer.Consume(length);
            }
            else
            {
                // Let the provider release its window before admitting parser-buffer growth.
                IMemoryReservation? staged = null;
                try
                {
                    content = buffer.CanRetain ? buffer.Retain(length) : ScopedMemory.Copy(content, stagingBudget, out staged);
                    buffer.Consume(length);
                    buffer.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Append(content.Span, exactCapacity: reachedEof);
                }
                finally { staged?.Dispose(); }
            }

            if (reachedEof)
                return ReadResult.EndOfFile;
            minimum = 1;
            examined = 0;
            position = default;
        }

        static int SourcePrefixLength(ReadOnlySpan<byte> bytes, int examined, bool reachedEof)
        {
            var unread = bytes[examined..];
            if (IsWideLookahead(examined, bytes.Length))
            {
                // Extended lookahead may offer much more than the requested record.
                var first = unread.IndexOf((byte)'\n');
                if (first >= 0)
                    return examined + first + 1;
            }
            else if (!reachedEof)
            {
                var last = unread.LastIndexOf((byte)'\n');
                return last < 0 ? 0 : examined + last + 1;
            }
            return reachedEof ? bytes.Length : 0;
        }
    }

    private static async ValueTask<TBatch> ReadRecordsCoreAsync<T, TBatch>(T source,
        Func<T, DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read,
        byte delimiter, int limit, IMemoryBudget? destinationBudget, ByteMemoryBuilder? reusableLine, bool retainImmutable,
        Func<DescriptorRecordBatch, TBatch> project, CancellationToken cancellationToken)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var minimum = 1;
        var examined = 0;
        (DescriptorReadBufferOwner? Owner, long Offset) position = default;
        while (true)
        {
            using var buffer = await read(source, WindowRequest(minimum, position, destinationBudget != null || reusableLine != null),
                cancellationToken).ConfigureAwait(false);
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
            var batchLimit = IsWideLookahead(examined, bytes.Length) ? 1 : limit;
            var length = ByteLines.FindCompleteRecords(bytes.Span, delimiter, batchLimit, examined, result.ReachedEoF,
                destinationBudget ?? buffer.MemoryBudget, out var offsets, out var offsetsMemory);
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
                    else if (destinationBudget == null || (retainImmutable && buffer.CanRetain))
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

    private static DescriptorReadRequest WindowRequest(int minimum,
        (DescriptorReadBufferOwner? Owner, long Offset) position, bool bounded)
    {
        // Preserve bounded transfer windows while allowing lookahead for a long record.
        // The extra byte distinguishes a record at the limit from an oversized one.
        var maximum = bounded && minimum <= BufferSize ? BufferSize
            : minimum == 1 ? int.MaxValue : ByteMemoryBuilder.MaxMemoryBuilderBytes + 1;
        return new DescriptorReadRequest(minimum, maximum, position);
    }

    private static bool IsWideLookahead(int examined, int offered) =>
        examined > 0 && (offered - examined > BufferSize || offered > ByteMemoryBuilder.MaxMemoryBuilderBytes);
}

/// <summary>Accepts descriptor windows into the parser's bounded source buffer.</summary>
internal readonly record struct SourceReadDestination(ByteMemoryBuilder Buffer, int MaximumBytes);

/// <summary>A framed byte batch with starts after the first record, or a terminal read outcome.</summary>
internal readonly record struct DescriptorRecordBatch(ReadOnlyMemory<byte> Content, int[] RecordStartOffsets, ByteReadResult Result)
{
    /// <summary>Projects a batch scanned with LF to the public line result.</summary>
    internal ReadResult AsLines() => Result.Error is { } error ? ReadResult.Failure(error)
        : Content.IsEmpty ? ReadResult.EndOfFile
        : ReadResult.FromValidatedLines(Content, RecordStartOffsets, Result.ReachedEoF);
}
