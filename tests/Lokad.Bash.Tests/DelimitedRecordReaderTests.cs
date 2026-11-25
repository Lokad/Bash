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
