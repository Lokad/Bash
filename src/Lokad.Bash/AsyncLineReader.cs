using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lokad.Bash.Memory;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>
/// A line read from a byte stream, with its content separated from its LF record terminator.
/// </summary>
/// <remarks>Storage is retained from its source batch and must not be mutated.</remarks>
public readonly struct ByteLine
{
    public ByteLine(ReadOnlyMemory<byte> raw)
    {
        Raw = raw;
        HasTerminatingLf = !raw.IsEmpty && raw.Span[^1] == (byte)'\n';
        Content = HasTerminatingLf ? raw[..^1] : raw;
    }

    /// <summary>The complete bytes returned by the stream, including a terminating LF when present.</summary>
    public ReadOnlyMemory<byte> Raw { get; }

    /// <summary>The line bytes excluding a terminating LF, if present.</summary>
    public ReadOnlyMemory<byte> Content { get; }

    /// <summary>Whether the input record ended with LF.</summary>
    public bool HasTerminatingLf { get; }
}

/// <summary>
/// Reads individual lines or remaining raw content from batched descriptor results.
/// </summary>
/// <remarks>
/// Use one consumer without overlapping calls. The caller retains descriptor ownership; this reader never closes it.
/// Reported read failures remain terminal. Constructed readers retain immutable source batches across refills;
/// CreateStreaming uses the shorter batch lifetimes documented on its factories.
/// Cancellation tokens reach refills; reading already-buffered lines does not perform another cancellation check.
/// Whole-input reads charge their returned buffer to the budget supplied when creating this reader.
/// Host constructors without a budget use <see cref="IBashHost.MemoryBudget"/>. Use a caller-owned
/// <see cref="IMemoryScope"/> for whole-input reads and keep it alive through result consumption,
/// including when consumption outlives the reader.
/// </remarks>
public sealed class AsyncLineReader : IDisposable
{
    /// <summary>Largest whole-input payload supported by a CLR byte array.</summary>
    public const int MaximumReadToEndBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;

    /// <summary>The refill callback. Borrowed-memory readers use their input directly.</summary>
    private readonly Func<int, IMemoryBudget, CancellationToken, ValueTask<ReadResult>>? _readLines;

    /// <summary>The cancellation token used for refill operations.</summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>The maximum number of lines requested on each refill.</summary>
    private readonly int _batchSize;
    private readonly IMemoryBudget _memoryBudget;
    private bool _ownsBatches;
    private bool _disposed;
    private IMemoryScope? _batchMemory;
    private IMemoryScope? _previousBatchMemory;

    /// <summary>Caller-owned input beyond the buffered batch, for readers without a refill callback.</summary>
    private ReadOnlyMemory<byte> _unbufferedContent;

    /// <summary>
    /// The buffered content returned by the most recent refill.
    /// The next unread line starts at <c>_content[_lineStart..]</c>.
    /// </summary>
    private ReadOnlyMemory<byte> _content;

    /// <summary>
    /// The start offsets of buffered lines after the first one within <see cref="_content"/>.
    /// When <c>_lineIndex</c> is less than this collection's count, the next line ends at
    /// <c>_content[_lineStart.._lineStartOffsets[_lineIndex]]</c>.
    /// </summary>
    private IReadOnlyList<int> _lineStartOffsets;

    /// <summary>
    /// The zero-based index of the next buffered line to return.
    /// The buffered line count is <c>_content.IsEmpty ? 0 : _lineStartOffsets.Count + 1</c>.
    /// </summary>
    private int _lineIndex;

    /// <summary>
    /// The byte offset of the next buffered line within <see cref="_content"/>.
    /// The current line content is <c>_content[_lineStart..nextStart]</c>.
    /// </summary>
    private int _lineStart;

    /// <summary>True once the underlying descriptor can no longer yield more lines.</summary>
    private bool _reachedEof;
    /// <summary>Remembers a terminal failure so later calls cannot retry it or report EOF.</summary>
    private ReadResult _readFailure;

    /// <summary>Reads borrowed host input using the host's largest line batches.</summary>
    public AsyncLineReader(
        IBashHost host,
        FileDescriptor descriptor,
        CancellationToken cancellationToken)
        : this(host, descriptor, cancellationToken, int.MaxValue)
    {
    }

    /// <summary>Owns read batches until disposal. Returned lines remain valid through EOF
    /// lookahead, but must be relinquished before the next ReadLine or ReadToEndAsync call.</summary>
    public static AsyncLineReader CreateStreaming(IBashHost host, FileDescriptor descriptor,
        CancellationToken cancellationToken, int batchSize, IMemoryBudget memoryBudget) =>
        CreateStreaming((limit, budget, token) => host.ReadLinesAsync(descriptor, limit, budget, token),
            cancellationToken, batchSize, memoryBudget);

    /// <summary>Owns batches produced by a callback that allocates into the supplied batch budget.
    /// Lines follow the same lifetime as the host-based streaming reader.</summary>
    public static AsyncLineReader CreateStreaming(
        Func<int, IMemoryBudget, CancellationToken, ValueTask<ReadResult>> readLines,
        CancellationToken cancellationToken, int batchSize, IMemoryBudget memoryBudget) =>
        new(readLines, cancellationToken, batchSize, memoryBudget) { _ownsBatches = true };

    /// <summary>Owns line offsets while the caller keeps the underlying content alive.</summary>
    public static AsyncLineReader CreateStreaming(ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken, int batchSize, IMemoryBudget memoryBudget) =>
        new(content, cancellationToken, batchSize, memoryBudget) { _ownsBatches = true };

    /// <summary>Reads borrowed host input, requesting at most batchSize lines per refill.</summary>
    public AsyncLineReader(
        IBashHost host,
        FileDescriptor descriptor,
        CancellationToken cancellationToken,
        int batchSize)
        : this((limit, _, token) => host.ReadLinesAsync(descriptor, limit, token),
            cancellationToken, batchSize, host.MemoryBudget)
    {
    }

    /// <summary>Reads bytes retained by its caller without another descriptor or buffer copy.</summary>
    /// <remarks>The caller must not mutate the storage while this reader or returned lines retain it.</remarks>
    public AsyncLineReader(ReadOnlyMemory<byte> content, CancellationToken cancellationToken, int batchSize, IMemoryBudget memoryBudget)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        _memoryBudget = memoryBudget;
        _cancellationToken = cancellationToken;
        _batchSize = batchSize;
        _lineStartOffsets = Array.Empty<int>();
        _unbufferedContent = content;
    }

    /// <summary>Reads batches supplied by a host adapter or an evaluator's operation callback.</summary>
    /// <param name="readLines">
    /// Receives the positive line limit, destination budget and effective refill token. Return immutable batches satisfying
    /// ReadResult's contract. Reported failures are terminal; thrown exceptions propagate without being retained.
    /// </param>
    /// <param name="cancellationToken">The reader's lifetime token, passed to refills.</param>
    /// <param name="batchSize">The positive maximum number of lines requested per refill.</param>
    /// <param name="memoryBudget">Owns retained batches and materialized results for their full lifetime.</param>
    public AsyncLineReader(
        Func<int, IMemoryBudget, CancellationToken, ValueTask<ReadResult>> readLines,
        CancellationToken cancellationToken,
        int batchSize,
        IMemoryBudget memoryBudget)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        ArgumentNullException.ThrowIfNull(readLines);

        _readLines = readLines;
        _memoryBudget = memoryBudget;
        _cancellationToken = cancellationToken;
        _batchSize = batchSize;
        _content = ReadOnlyMemory<byte>.Empty;
        _lineStartOffsets = Array.Empty<int>();
        _lineIndex = 0;
        _lineStart = 0;
        _reachedEof = false;
    }

    /// <summary>
    /// Returns true when no more lines can be read from the descriptor.
    /// </summary>
    /// <exception cref="ShellDescriptorReadException">The source reported a terminal read failure.</exception>
    public ValueTask<bool> ReachedEof() => ReachedEofCore(_cancellationToken);

    /// <summary>
    /// Returns true when no more lines can be read, using both lifetime and call tokens for refills.
    /// </summary>
    /// <exception cref="ShellDescriptorReadException">The source reported a terminal read failure.</exception>
    public async ValueTask<bool> ReachedEof(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenScope.Combine(_cancellationToken, cancellationToken);
        return await ReachedEofCore(cancellation.Token).ConfigureAwait(false);
    }

    private ValueTask<bool> ReachedEofCore(CancellationToken cancellationToken)
    {
        var pending = TryReachedEofCore(cancellationToken);
        // Buffered lines must not allocate an async state machine per record.
        return pending.IsCompletedSuccessfully
            ? ValueTask.FromResult(Unwrap(pending.Result))
            : Await(pending);

        static bool Unwrap((bool ReachedEof, ReadResult Failure) result)
        {
            if (result.Failure.IsTooLarge)
                throw new ByteMemoryBuilder.LimitExceededException();
            result.Failure.ThrowIfError();
            return result.ReachedEof;
        }

        static async ValueTask<bool> Await(ValueTask<(bool ReachedEof, ReadResult Failure)> pending) =>
            Unwrap(await pending.ConfigureAwait(false));
    }

    private ValueTask<(bool ReachedEof, ReadResult Failure)> TryReachedEofCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lineIndex < BufferedLineCount)
            return ValueTask.FromResult((false, ReadResult.EndOfFile));

        if (_readFailure.HasFailure)
            return ValueTask.FromResult((false, _readFailure));
        if (_reachedEof)
            return ValueTask.FromResult((true, ReadResult.EndOfFile));

        return RefillAsync(this, cancellationToken);

        static async ValueTask<(bool ReachedEof, ReadResult Failure)> RefillAsync(AsyncLineReader reader, CancellationToken token)
        {
            var batchMemory = reader._ownsBatches ? reader._memoryBudget.CreateScope() : null;
            ReadResult result;
            try
            {
                var budget = batchMemory ?? reader._memoryBudget;
                result = reader._readLines is { } readLines
                    ? await readLines(reader._batchSize, budget, token).ConfigureAwait(false)
                    : ReadBorrowedBatch(reader, budget, token);
            }
            catch { batchMemory?.Dispose(); throw; }
            if (result.HasFailure)
            {
                batchMemory?.Dispose();
                reader._readFailure = result;
                return (false, result);
            }

            // EOF lookahead can overlap the last line returned from the previous batch.
            reader._previousBatchMemory = reader._batchMemory;
            reader._batchMemory = batchMemory;

            reader._content = result.Content;
            reader._lineStartOffsets = result.LineStartOffsets;
            reader._lineIndex = 0;
            reader._lineStart = 0;
            reader._reachedEof = result.ReachedEoF;
            // Success always has bytes; empty non-error results are normal EOF.
            return (result.Content.IsEmpty, ReadResult.EndOfFile);
        }

        static ReadResult ReadBorrowedBatch(AsyncLineReader reader, IMemoryBudget budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var remaining = reader._unbufferedContent;
            if (remaining.IsEmpty)
                return ReadResult.EndOfFile;
            var length = ByteLines.FindCompleteRecords(remaining.Span, (byte)'\n', reader._batchSize, examined: 0,
                reachedEof: true, maximumBatchBytes: int.MaxValue, budget, out var offsets, out _);
            reader._unbufferedContent = remaining[length..];
            return ReadResult.FromValidatedLines(remaining[..length], offsets, reader._unbufferedContent.IsEmpty);
        }
    }

    /// <summary>
    /// Returns the next buffered line. Call only after <see cref="ReachedEof()"/> returned false.
    /// </summary>
    public ByteLine ReadLine()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lineIndex >= BufferedLineCount)
            throw new InvalidOperationException("No line is buffered. Call ReachedEof() before ReadLine().");

        ReleasePreviousBatch();

        var nextStart = _lineIndex < _lineStartOffsets.Count
            ? _lineStartOffsets[_lineIndex]
            : _content.Length;
        var line = _content.Slice(_lineStart, nextStart - _lineStart);
        _lineStart = nextStart;
        _lineIndex++;
        return new ByteLine(line);
    }

    /// <summary>Returns all remaining bytes only after successful EOF, preserving LF, CR and NUL.</summary>
    /// <remarks>
    /// Materialization reserves capacity from the shared governor. Failure, cancellation or
    /// refusal discards partial output; descriptor ownership stays with the caller.
    /// </remarks>
    /// <exception cref="ByteMemoryBuilder.LimitExceededException">Remaining input exceeds the whole-input byte limit.</exception>
    /// <exception cref="ShellDescriptorReadException">The source reported a terminal read failure.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ReadToEndAsync()
    {
        var result = await TryReadToEndAsync().ConfigureAwait(false);
        return result.GetContentOrThrow();
    }

    /// <summary>Reads remaining bytes, returning descriptor failures and size rejection without partial output.</summary>
    /// <remarks>
    /// Materialization uses the shared governor, independent of batch boundaries. Cancellation,
    /// reservation refusal and thrown I/O failures propagate.
    /// </remarks>
    public ValueTask<BoundedReadResult> TryReadToEndAsync() => TryReadToEndAsync(MaximumReadToEndBytes);

    /// <summary>Reads remaining bytes without materializing more than the caller's byte limit.</summary>
    /// <param name="maximumBytes">The largest accepted payload, below the CLR array length limit.</param>
    /// <remarks>
    /// Consumes remaining buffered input first and releases each owned batch after copying its bytes.
    /// Rejection does not rewind previously consumed batches.
    /// </remarks>
    public ValueTask<BoundedReadResult> TryReadToEndAsync(int maximumBytes) => TryReadToEndAsync(maximumBytes, destination: null);

    /// <summary>Reads remaining bytes, allowing the dispatcher to append batches to the supplied source buffer.</summary>
    /// <remarks>Takes ownership of the supplied builder. Completed bytes remain charged to its enclosing scope.</remarks>
    internal async ValueTask<BoundedReadResult> TryReadToEndAsync(int maximumBytes, ByteMemoryBuilder? destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var output = destination ?? new ByteMemoryBuilder(0, _memoryBudget);
        ReleasePreviousBatch();
        if (_lineIndex >= BufferedLineCount)
            ReleaseCurrentBatch();

        if (destination is null && _readLines is null && TryAppendBorrowedRemainder())
            return new BoundedReadResult.Complete(output.DetachSnapshot());

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var earlierBytes = output.Length;
            var (reachedEof, failure) = await TryReachedEofCore(_cancellationToken).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
            if (failure.Error is { } error)
                return new BoundedReadResult.Failed(ByteReadResult.Failure(
                    0, error, failure.ReachedEoF));
            if (failure.IsTooLarge)
                return new BoundedReadResult.TooLarge();
            if (reachedEof)
            {
                ReleaseCurrentBatch();
                return new BoundedReadResult.Complete(output.DetachSnapshot());
            }

            // Consume the remaining batch directly; line boundaries were already scanned by its producer.
            if (_content.Length - _lineStart > maximumBytes - earlierBytes)
                return new BoundedReadResult.TooLarge();
            // The dispatcher may have copied this batch directly into the destination.
            if (output.Length == earlierBytes)
                output.Append(_content.Span[_lineStart..], exactCapacity: _reachedEof);
            ReleaseCurrentBatch();
        }

        bool TryAppendBorrowedRemainder()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var buffered = _content[_lineStart..];
            var length = buffered.Length + _unbufferedContent.Length;
            // A rejected size or reservation keeps the ordinary batch-by-batch consumption boundary.
            if (length > Math.Min(maximumBytes, MaximumReadToEndBytes)
                || !output.TryEnsureCapacity(length, exactCapacity: true))
                return false;

            output.Append(buffered.Span);
            output.Append(_unbufferedContent.Span);
            _cancellationToken.ThrowIfCancellationRequested();
            _unbufferedContent = default;
            _reachedEof = true;
            ReleaseCurrentBatch();
            return true;
        }
    }

    /// <summary>The number of logical lines currently buffered in <see cref="_content"/>.</summary>
    private int BufferedLineCount => _content.IsEmpty ? 0 : _lineStartOffsets.Count + 1;

    private void ReleasePreviousBatch()
    {
        _previousBatchMemory?.Dispose();
        _previousBatchMemory = null;
    }

    private void ReleaseCurrentBatch()
    {
        _content = default;
        _lineStartOffsets = Array.Empty<int>();
        _lineStart = 0;
        _lineIndex = 0;
        _batchMemory?.Dispose();
        _batchMemory = null;
    }

    /// <summary>Discards owned read batches; whole-input results retain their caller's budget.</summary>
    public void Dispose()
    {
        _disposed = true;
        _unbufferedContent = default;
        ReleasePreviousBatch();
        ReleaseCurrentBatch();
    }
}
