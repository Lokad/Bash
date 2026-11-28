using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class DelimitedRecordReaderTests
{
    [Fact]
    public async Task RetainedBorrowReusesCompleteRecordsAndKeepsSplitBytes()
    {
        var governor = new MemoryGovernor(2 * DelimitedRecordReader.RawReadBufferSize);
        using var memory = new MemoryCapacity(governor, 0);
        await using var host = new MockFileSystem { MaximumReadWindowBytes = 8 };
        host.SetStandardInputBytes([(byte)'a', (byte)'|', (byte)'|', 0xff, 13, (byte)'b', (byte)'c', (byte)'d',
            (byte)'e', (byte)'|', (byte)'t']);
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Retained, memory.Grow, keepBorrow: true, CancellationToken.None);

        var first = Assert.IsType<DelimitedRecord>(await reader.ReadNextAsync());
        var buffered = reader.ReadNextAsync();
        Assert.True(buffered.IsCompletedSuccessfully);
        AssertRecord(await buffered, [], hasTerminator: true);
        Assert.Equal(1, host.ReadCallCount);

        var split = Assert.IsType<DelimitedRecord>(await reader.ReadNextBoundedAsync(6));
        reader.ReleaseBorrow();
        AssertRecord(await reader.ReadNextAsync(), [(byte)'t'], hasTerminator: false);
        Assert.Null(await reader.ReadNextAsync());
        Assert.Equal(3, host.ReadCallCount);
        AssertRecord(first, [(byte)'a'], hasTerminator: true);
        AssertRecord(split, [0xff, 13, (byte)'b', (byte)'c', (byte)'d', (byte)'e'], hasTerminator: true);
        Assert.False(reader.ReadFailure.IsError);

        reader.Dispose();
        memory.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardDistinguishesTerminatedAndUnterminatedOversizedRecords(bool keepBorrow)
    {
        await using var host = new MockFileSystem { MaximumReadWindowBytes = 3 };
        host.SetStandardInput("abcd|ok|tail");
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Reusable, static _ => { }, keepBorrow, CancellationToken.None);

        Assert.Equal(new DelimitedRecord(default, true) { ExceedsLimit = true }, await reader.ReadNextBoundedAsync(2));
        AssertRecord(await reader.ReadNextBoundedAsync(2), "ok"u8.ToArray(), hasTerminator: true);
        Assert.Equal(new DelimitedRecord(default, false) { ExceedsLimit = true }, await reader.ReadNextBoundedAsync(2));
        Assert.Null(await reader.ReadNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopLeavesTheOversizedFragmentAvailableAfterReleasingTheBorrow(bool keepBorrow)
    {
        await using var host = new MockFileSystem { MaximumReadWindowBytes = 4 };
        host.SetStandardInput("abcd|end");
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Reusable, static _ => { }, keepBorrow, CancellationToken.None);

        Assert.Equal(new DelimitedRecord(default, false) { ExceedsLimit = true },
            await reader.ReadNextBoundedAsync(2, DelimitedRecordOverflow.Stop));
        reader.ReleaseBorrow();
        using var remaining = await host.ReadAsync(FileDescriptor.StdIn, DescriptorReadRequest.UpTo(8), CancellationToken.None);
        Assert.Equal("abcd"u8.ToArray(), remaining.Content.ToArray());
    }

    [Theory]
    [InlineData(DelimitedRecordStorage.Reusable, false)]
    [InlineData(DelimitedRecordStorage.Reusable, true)]
    [InlineData(DelimitedRecordStorage.Retained, false)]
    [InlineData(DelimitedRecordStorage.Retained, true)]
    public async Task StopLeavesATerminatedRecordAvailableForRetry(DelimitedRecordStorage storage, bool keepBorrow)
    {
        await using var host = new MockFileSystem();
        host.SetStandardInput("ok|abcd|next");
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            storage, static _ => { }, keepBorrow, CancellationToken.None);

        AssertRecord(await reader.ReadNextAsync(), "ok"u8.ToArray(), hasTerminator: true);
        Assert.Equal(new DelimitedRecord(default, false) { ExceedsLimit = true },
            await reader.ReadNextBoundedAsync(2, DelimitedRecordOverflow.Stop));
        reader.ReleaseBorrow();
        using (var unread = await host.ReadAsync(FileDescriptor.StdIn, DescriptorReadRequest.UpTo(16), CancellationToken.None))
            Assert.Equal("abcd|next"u8.ToArray(), unread.Content.ToArray());
        AssertRecord(await reader.ReadNextBoundedAsync(4, DelimitedRecordOverflow.Stop), "abcd"u8.ToArray(), hasTerminator: true);
        AssertRecord(await reader.ReadNextAsync(), "next"u8.ToArray(), hasTerminator: false);
        Assert.Null(await reader.ReadNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAtEofPreservesAnAdmittedPrefixAndTheUnreadSuffix(bool keepBorrow)
    {
        await using var host = new MockFileSystem { MaximumReadWindowBytes = 3 };
        host.SetStandardInput("abcde");
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Reusable, static _ => { }, keepBorrow, CancellationToken.None);

        Assert.Equal(new DelimitedRecord(default, false) { ExceedsLimit = true },
            await reader.ReadNextBoundedAsync(4, DelimitedRecordOverflow.Stop));
        reader.ReleaseBorrow();
        using (var unread = await host.ReadAsync(FileDescriptor.StdIn, DescriptorReadRequest.UpTo(3), CancellationToken.None))
            Assert.Equal("de"u8.ToArray(), unread.Content.ToArray());
        AssertRecord(await reader.ReadNextBoundedAsync(5, DelimitedRecordOverflow.Stop), "abcde"u8.ToArray(), hasTerminator: false);
        Assert.Null(await reader.ReadNextAsync());
    }

    [Theory]
    [InlineData(DelimitedRecordOverflow.Stop, false)]
    [InlineData(DelimitedRecordOverflow.Discard, true)]
    public async Task OversizedInputNeedsNoRecordBacking(DelimitedRecordOverflow overflow, bool hasTerminator)
    {
        await using var host = new MockFileSystem { MaximumReadWindowBytes = 3 };
        host.SetStandardInput("abcdef|");
        var reservations = new List<long>();
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Retained, reservations.Add, keepBorrow: false, CancellationToken.None);

        Assert.Equal(new DelimitedRecord(default, hasTerminator) { ExceedsLimit = true },
            await reader.ReadNextBoundedAsync(2, overflow));
        if (hasTerminator)
            Assert.Null(await reader.ReadNextAsync());
        Assert.Empty(reservations);
    }

    [Fact]
    public async Task EmptyInputNeedsNoRecordBacking()
    {
        await using var host = new MockFileSystem();
        host.SetStandardInput("");
        var reservations = new List<long>();
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Retained, reservations.Add, keepBorrow: true, CancellationToken.None);

        Assert.Null(await reader.ReadNextAsync());
        Assert.Null(await reader.ReadNextAsync());
        Assert.Empty(reservations);
        Assert.Equal(1, host.ReadCallCount);
    }

    [Fact]
    public async Task RefusedRecordBackingPreservesThePendingBytes()
    {
        await using var host = new MockFileSystem();
        host.SetStandardInput("ok|tail");
        var refuse = true;
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Retained, _ =>
            {
                if (refuse)
                    throw new MemoryBudgetExceededException();
            }, keepBorrow: false, CancellationToken.None);

        await Assert.ThrowsAsync<MemoryBudgetExceededException>(async () => await reader.ReadNextAsync());
        using (var unread = await host.ReadAsync(FileDescriptor.StdIn, DescriptorReadRequest.UpTo(8), CancellationToken.None))
            Assert.Equal("ok|tail"u8.ToArray(), unread.Content.ToArray());
        refuse = false;
        AssertRecord(await reader.ReadNextAsync(), "ok"u8.ToArray(), hasTerminator: true);
        AssertRecord(await reader.ReadNextAsync(), "tail"u8.ToArray(), hasTerminator: false);
    }

    [Theory]
    [InlineData(DelimitedRecordStorage.Reusable, false)]
    [InlineData(DelimitedRecordStorage.Reusable, true)]
    [InlineData(DelimitedRecordStorage.Retained, false)]
    [InlineData(DelimitedRecordStorage.Retained, true)]
    public async Task CancellationBetweenRecordsPreservesTheNextRecord(DelimitedRecordStorage storage, bool keepBorrow)
    {
        using var cancellation = new CancellationTokenSource();
        await using var host = new MockFileSystem();
        host.SetStandardInput("one|two|tail");
        var reservations = new List<long>();
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            storage, reservations.Add, keepBorrow, cancellation.Token);
        var first = Assert.IsType<DelimitedRecord>(await reader.ReadNextAsync());

        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadNextBoundedAsync(1, DelimitedRecordOverflow.Discard).AsTask());

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, host.ReadCallCount);
        Assert.Equal([DelimitedRecordReader.RawReadBufferMemoryBytes], reservations);
        AssertRecord(first, "one"u8.ToArray(), hasTerminator: true);
        using var remaining = await host.ReadAsync(FileDescriptor.StdIn, DescriptorReadRequest.UpTo(16), CancellationToken.None);
        Assert.Equal("two|tail"u8.ToArray(), remaining.Content.ToArray());
        Assert.Equal(4, remaining.Position.Offset);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationBeforeProcessingARecordBorrowReleasesIt(bool keepBorrow, bool suspended)
    {
        using var cancellation = new CancellationTokenSource();
        var committed = new List<int>();
        var owner = new DescriptorReadBufferOwner(committed.Add, new MemoryGovernor(0));
        var reply = new TaskCompletionSource<DescriptorReadBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations = new List<long>();
        var releases = 0;
        var operationError = new OperationError("late input error\n");
        await using var host = new CallbackReadHost(Read);
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Reusable, reservations.Add, keepBorrow, cancellation.Token);

        var read = reader.ReadNextAsync().AsTask();
        var waitedForBorrow = true;
        if (suspended)
        {
            cancellation.Cancel();
            waitedForBorrow = !read.IsCompleted;
            reply.SetResult(Borrow());
        }
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

        Assert.True(waitedForBorrow);
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(1, releases);
        Assert.Equal([0], committed);
        Assert.Equal(0, owner.Position);
        Assert.Empty(reservations);
        Assert.False(reader.ReadFailure.IsError);
        reader.ReleaseBorrow();
        Assert.Equal(1, releases);

        ValueTask<DescriptorReadBuffer> Read(DescriptorReadRequest _, CancellationToken token)
        {
            Assert.Equal(cancellation.Token, token);
            if (suspended)
                return new ValueTask<DescriptorReadBuffer>(reply.Task);
            var borrow = Borrow();
            cancellation.Cancel();
            return ValueTask.FromResult(borrow);
        }

        DescriptorReadBuffer Borrow()
        {
            var buffer = owner.Borrow("one|tail"u8.ToArray(), ByteReadResult.Failure(8, operationError, reachedEoF: false),
                canRetain: false);
            buffer.ReleaseWith(() => releases++);
            return buffer;
        }
    }

    [Fact]
    public async Task CancellationBetweenRecordFragmentsPreservesTheCommittedPrefix()
    {
        using var cancellation = new CancellationTokenSource();
        var committed = new List<int>();
        var owner = new DescriptorReadBufferOwner(committed.Add, new MemoryGovernor(0));
        var reply = new TaskCompletionSource<DescriptorReadBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reservations = new List<long>();
        var reads = 0;
        var releases = 0;
        await using var host = new CallbackReadHost(Read);
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Retained, reservations.Add, keepBorrow: true, cancellation.Token);

        var reading = reader.ReadNextBoundedAsync(8, DelimitedRecordOverflow.Stop).AsTask();
        Assert.Equal(2, reads);
        Assert.Equal([3], committed);
        var prefixReservation = Assert.Single(reservations);
        cancellation.Cancel();
        var suffix = owner.Borrow("t|next"u8.ToArray(), ByteReadResult.Success(6, reachedEoF: true), canRetain: false);
        suffix.ReleaseWith(() => releases++);
        reply.SetResult(suffix);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal([3, 0], committed);
        Assert.Equal(3, owner.Position);
        Assert.Equal(1, releases);
        Assert.Equal([prefixReservation], reservations);

        ValueTask<DescriptorReadBuffer> Read(DescriptorReadRequest _, CancellationToken __) =>
            reads++ == 0
                ? ValueTask.FromResult(owner.Borrow("par"u8.ToArray(), ByteReadResult.Success(3, reachedEoF: false), canRetain: false))
                : new ValueTask<DescriptorReadBuffer>(reply.Task);
    }

    [Fact]
    public async Task TerminalFailureIsPreservedWhileDrainingBorrowedBytes()
    {
        var error = new OperationError("synthetic read failure\n");
        await using var host = new TerminalReadHost(error);
        using var reader = new DelimitedRecordReader(host, FileDescriptor.StdIn, (byte)'|',
            DelimitedRecordStorage.Reusable, static _ => { }, keepBorrow: true, CancellationToken.None);

        AssertRecord(await reader.ReadNextAsync(), "a"u8.ToArray(), hasTerminator: true);
        Assert.Equal(error, reader.ReadFailure.Error);
        AssertRecord(await reader.ReadNextAsync(), "rest"u8.ToArray(), hasTerminator: false);
        Assert.Null(await reader.ReadNextAsync());
        Assert.Equal(error, reader.ReadFailure.Error);
        Assert.Equal(6, host.Consumed);
    }

    private static void AssertRecord(DelimitedRecord? result, byte[] expected, bool hasTerminator)
    {
        var record = Assert.IsType<DelimitedRecord>(result);
        Assert.Equal(expected, record.Content.ToArray());
        Assert.Equal(hasTerminator, record.HasTerminator);
        Assert.False(record.ExceedsLimit);
    }

    private sealed class CallbackReadHost(Func<DescriptorReadRequest, CancellationToken, ValueTask<DescriptorReadBuffer>> read)
        : MockFileSystem
    {
        public override ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(FileDescriptor.StdIn, descriptor);
            return read(request, cancellationToken);
        }
    }

    private sealed class TerminalReadHost(OperationError error) : MockFileSystem
    {
        private readonly byte[] _content = "a|rest"u8.ToArray();
        public int Consumed { get; private set; }

        public override ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, Consumed);
            var owner = new DescriptorReadBufferOwner(count => Consumed += count, MemoryBudget);
            return ValueTask.FromResult(owner.Borrow(_content, ByteReadResult.Failure(_content.Length, error, reachedEoF: false),
                canRetain: false));
        }
    }
}
