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
/// </remarks>
public sealed class AsyncLineReader : IDisposable
{
    /// <summary>Largest whole-input payload supported by a CLR byte array.</summary>
    public const int MaximumReadToEndBytes = ByteMemoryBuilder.MaxMemoryBuilderBytes - 1;

    /// <summary>The host or file-descriptor operation used to refill batches.</summary>
    private readonly Func<int, IMemoryBudget, CancellationToken, ValueTask<ReadResult>> _readLines;

    /// <summary>The cancellation token used for refill operations.</summary>
    private readonly CancellationToken _cancellationToken;

    /// <summary>The maximum number of lines requested on each refill.</summary>
    private readonly int _batchSize;
    private readonly IMemoryBudget _memoryBudget;
    private bool _ownsBatches;
    private bool _disposed;
    private IMemoryScope? _batchMemory;
    private IMemoryScope? _previousBatchMemory;

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
        _readLines = Read;

        ValueTask<ReadResult> Read(int limit, IMemoryBudget budget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (content.IsEmpty)
                return ValueTask.FromResult(ReadResult.EndOfFile);
            var length = ByteLines.FindCompleteLines(content.Span, limit, budget, out var offsets, out _);
            if (length == 0)
                length = content.Length;
            var batch = content[..length];
            content = content[length..];
            return ValueTask.FromResult(ReadResult.FromValidatedLines(batch, offsets, content.IsEmpty));
        }
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

        if (_readFailure.IsError)
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
                result = await reader._readLines(reader._batchSize,
                    batchMemory ?? reader._memoryBudget, token).ConfigureAwait(false);
            }
            catch { batchMemory?.Dispose(); throw; }
            if (result.IsError)
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
    /// <remarks>Consumes remaining buffered input first. Rejection does not rewind previously consumed batches.</remarks>
    public async ValueTask<BoundedReadResult> TryReadToEndAsync(int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        using var output = new ByteMemoryBuilder(0, _memoryBudget);
        while (true)
        {
            ReleasePreviousBatch();
            _cancellationToken.ThrowIfCancellationRequested();
            var (reachedEof, failure) = await TryReachedEofCore(_cancellationToken).ConfigureAwait(false);
            _cancellationToken.ThrowIfCancellationRequested();
            if (failure.Error is { } error)
                return new BoundedReadResult.Failed(ByteReadResult.Failure(
                    0, error, failure.ReachedEoF));
            if (reachedEof)
                return new BoundedReadResult.Complete(output.DetachSnapshot());

            // Consume the remaining batch directly; line boundaries were already scanned by its producer.
            if (_content.Length - _lineStart > maximumBytes - output.Length)
                return new BoundedReadResult.TooLarge();
            output.Append(_content.Span[_lineStart..]);
            _lineStart = _content.Length;
            _lineIndex = BufferedLineCount;
        }
    }

    /// <summary>The number of logical lines currently buffered in <see cref="_content"/>.</summary>
    private int BufferedLineCount => _content.IsEmpty ? 0 : _lineStartOffsets.Count + 1;

    private void ReleasePreviousBatch()
    {
        _previousBatchMemory?.Dispose();
        _previousBatchMemory = null;
    }

    /// <summary>Discards owned read batches; whole-input results retain their caller's budget.</summary>
    public void Dispose()
    {
        _disposed = true;
        _content = default;
        _lineStartOffsets = Array.Empty<int>();
        ReleasePreviousBatch();
        _batchMemory?.Dispose();
        _batchMemory = null;
    }
}
