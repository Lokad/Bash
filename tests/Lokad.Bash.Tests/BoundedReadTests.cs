using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class BoundedReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteInputPreservesBytesAndFinalData(bool finalDataIsEof)
    {
        var host = new TestOperationVisitor([]);
        host.EnqueueByteReadResult(new byte[] { 0xff, 13, 10 }, ByteReadResult.Success(3, reachedEoF: false));
        host.EnqueueByteReadResult(new byte[] { 0, 0xc3, 0xa9 }, ByteReadResult.Success(3, finalDataIsEof));
        if (!finalDataIsEof)
            host.EnqueueByteReadResult(ReadOnlyMemory<byte>.Empty, ByteReadResult.EndOfFile);
        var charged = new List<int>();

        var result = await host.TryReadAllBytesAsync(new(71), 6, charged.Add, host.MemoryBudget, CancellationToken.None);

        Assert.Equal(new byte[] { 0xff, 13, 10, 0, 0xc3, 0xa9 }, Assert.IsType<BoundedReadResult.Complete>(result).Content.ToArray());
        Assert.Equal(finalDataIsEof ? new[] { 3, 3 } : [3, 3, 0], charged);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDiscardsPartialInputAndRetainsReadOutcome(bool includesBytes)
    {
        var host = new TestOperationVisitor([]);
        host.EnqueueByteReadResult("prefix"u8.ToArray(), ByteReadResult.Success(6, reachedEoF: false));
        var failure = ByteReadResult.Failure(includesBytes ? 1 : 0, error: new OperationError("read error\n"), reachedEoF: false);
        host.EnqueueByteReadResult(includesBytes ? "x"u8.ToArray() : [], failure);
        var charged = new List<int>();

        var result = await host.TryReadAllBytesAsync(new(71), 6, charged.Add, host.MemoryBudget, CancellationToken.None);

        Assert.Equal(failure, Assert.IsType<BoundedReadResult.Failed>(result).Failure);
        Assert.Equal([6], charged);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task ExcessInputUsesBoundedRequestsAndOneExcessByte()
    {
        var host = new MockFileSystem();
        host.SetStandardInputBytes(new byte[9000]);
        var requests = new List<int>();
        host.BeforeReadBuffer = buffer => requests.Add(buffer);

        var result = await host.TryReadAllBytesAsync(FileDescriptor.StdIn, 8200, static _ => { }, host.MemoryBudget, CancellationToken.None);

        Assert.IsType<BoundedReadResult.TooLarge>(result);
        Assert.Equal([8192, 9], requests);
        Assert.Equal(2, host.ReadCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroLimitDistinguishesEmptyFromNonemptyInput(bool hasInput)
    {
        var host = new MockFileSystem();
        host.SetStandardInput(hasInput ? "x" : "");
        var requests = new List<int>();
        host.BeforeReadBuffer = buffer => requests.Add(buffer);

        var result = await host.TryReadAllBytesAsync(FileDescriptor.StdIn, 0, static _ => { }, host.MemoryBudget, CancellationToken.None);

        if (hasInput)
            Assert.IsType<BoundedReadResult.TooLarge>(result);
        else
            Assert.True(Assert.IsType<BoundedReadResult.Complete>(result).Content.IsEmpty);
        Assert.Equal([1], requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDoesNotPublishOrContinueReading(bool beforeRead)
    {
        using var cancellation = new CancellationTokenSource();
        var host = new MockFileSystem();
        host.SetStandardInput("data");
        if (beforeRead)
            cancellation.Cancel();
        else
            host.BeforeRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.TryReadAllBytesAsync(FileDescriptor.StdIn, 4, static _ => { }, host.MemoryBudget, cancellation.Token).AsTask());

        Assert.Equal(beforeRead ? 0 : 1, host.ReadCallCount);
    }

    [Fact]
    public async Task AccountingCancellationPreventsFinalPublication()
    {
        using var cancellation = new CancellationTokenSource();
        var host = new TestOperationVisitor([]);
        host.EnqueueByteReadResult("data"u8.ToArray(), ByteReadResult.Success(4, reachedEoF: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.TryReadAllBytesAsync(new(71), 4, _ => cancellation.Cancel(), host.MemoryBudget, cancellation.Token).AsTask());

        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task AccountingRejectionStopsBeforeAnotherRead()
    {
        var host = new MockFileSystem();
        host.SetStandardInputBytes(new byte[9000]);
        var rejection = new InvalidOperationException("budget exhausted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.TryReadAllBytesAsync(FileDescriptor.StdIn, 9000, _ => throw rejection, host.MemoryBudget, CancellationToken.None).AsTask());

        Assert.Same(rejection, error);
        Assert.Equal(1, host.ReadCallCount);
    }
}
