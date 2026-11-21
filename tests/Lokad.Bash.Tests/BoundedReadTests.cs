using System.Runtime.InteropServices;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class BoundedReadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WholeLineReadReleasesConsumedBatchesBeforeRefill(bool readFirstLine, bool finalDataIsEof)
    {
        var governor = new MemoryGovernor(16 * 1024);
        using var owner = governor.CreateScope();
        var first = new byte[4096];
        first[0] = 0xff;
        first[^2] = 13;
        first[^1] = 10;
        var second = new byte[4096];
        second[^1] = (byte)'a';
        IMemoryReservation? lastBatch = null;
        var next = 0;
        using var reader = AsyncLineReader.CreateStreaming((_, budget, _) =>
        {
            if (lastBatch is { } consumed)
                Assert.Equal(0, consumed.Bytes);
            if (++next == 3)
                return ValueTask.FromResult(ReadResult.EndOfFile);
            var content = ScopedMemory.Copy(next == 1 ? first : second, budget, out lastBatch);
            return ValueTask.FromResult(ReadResult.Success(content, [], reachedEoF: next == 2 && finalDataIsEof));
        }, CancellationToken.None, 1, owner);

        if (readFirstLine)
        {
            Assert.False(await reader.ReachedEof());
            Assert.Equal(first, reader.ReadLine().Raw.ToArray());
            Assert.Equal(4096, Assert.IsAssignableFrom<IMemoryReservation>(lastBatch).Bytes);
        }

        var result = await reader.TryReadToEndAsync(8192);

        byte[] expected = readFirstLine ? second : [.. first, .. second];
        Assert.Equal(expected, Assert.IsType<BoundedReadResult.Complete>(result).Content.ToArray());
        Assert.Equal(finalDataIsEof ? 2 : 3, next);
        Assert.Equal(0, Assert.IsAssignableFrom<IMemoryReservation>(lastBatch).Bytes);
        Assert.True(await reader.ReachedEof());
        Assert.Equal(expected.Length, governor.ReservedBytes);
        reader.Dispose();
        Assert.Equal(expected.Length, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    public async Task SmallResultsRetainOnlyTheirBytes(int byteCount, bool finalRead)
    {
        const int maximumBytes = 8192;
        var memory = new MemoryGovernor(maximumBytes + byteCount);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        byte[] expected = byteCount == 0 ? [] : [0xff];
        if (byteCount > 0)
            host.EnqueueByteReadResult(expected, ByteReadResult.Success(byteCount, finalRead));
        if (byteCount == 0 || !finalRead)
            host.EnqueueByteReadResult(ReadOnlyMemory<byte>.Empty, ByteReadResult.EndOfFile);
        var charges = new List<(int Bytes, long Reserved)>();

        using (var destination = memory.CreateScope())
        {
            var result = await host.TryReadAllBytesAsync(new(71), maximumBytes,
                count => charges.Add((count, memory.ReservedBytes)), destination, CancellationToken.None);
            var content = Assert.IsType<BoundedReadResult.Complete>(result).Content;
            Assert.Equal(expected, content.ToArray());
            Assert.True(MemoryMarshal.TryGetArray(content, out var segment));
            Assert.Equal(byteCount, Assert.IsType<byte[]>(segment.Array).Length);
            Assert.Equal(byteCount, memory.ReservedBytes);
        }

        Assert.Equal(finalRead ? [(byteCount, (long)maximumBytes)] : [(1, maximumBytes), (0, maximumBytes)], charges);
        Assert.Equal(maximumBytes + byteCount, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task FragmentedInputFitsTheSameAllowanceAsATerminalRead()
    {
        const int maximumBytes = 8192;
        const int resultBytes = 4096;
        const int chunkBytes = 64;
        var memory = new MemoryGovernor(maximumBytes + resultBytes);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        var input = new byte[resultBytes];
        for (var offset = 0; offset < input.Length; offset++)
            input[offset] = (byte)offset;
        for (var offset = 0; offset < input.Length; offset += chunkBytes)
            host.EnqueueByteReadResult(input.AsMemory(offset, chunkBytes),
                ByteReadResult.Success(chunkBytes, offset + chunkBytes == input.Length));

        var charges = new List<int>();
        using (var destination = memory.CreateScope())
        {
            var result = await host.TryReadAllBytesAsync(new(71), maximumBytes, charges.Add, destination, CancellationToken.None);
            Assert.Equal(input, Assert.IsType<BoundedReadResult.Complete>(result).Content.ToArray());
            Assert.Equal(resultBytes, memory.ReservedBytes);
        }
        Assert.Equal(Enumerable.Repeat(chunkBytes, resultBytes / chunkBytes), charges);
        Assert.Equal(maximumBytes + resultBytes, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task OutputGrowthRefusalReleasesBothReadBuffers()
    {
        const int readBufferBytes = 8192;
        const int maximumBytes = 2 * readBufferBytes;
        var memory = new MemoryGovernor(4 * readBufferBytes - 1);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        host.EnqueueByteReadResult(new byte[readBufferBytes], ByteReadResult.Success(readBufferBytes, reachedEoF: false));
        host.EnqueueByteReadResult("x"u8.ToArray(), ByteReadResult.Success(1, reachedEoF: true));
        var charges = new List<int>();
        using var destination = memory.CreateScope();

        // Growth needs the scratch, the existing result and the doubled result at the same time.
        await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => host.TryReadAllBytesAsync(
            new(71), maximumBytes, charges.Add, destination, CancellationToken.None).AsTask());

        Assert.Equal([readBufferBytes, 1], charges);
        Assert.Equal(2 * readBufferBytes, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Fact]
    public async Task CancellationDuringResultAdmissionPreventsPublication()
    {
        const int maximumBytes = 8192;
        var memory = new MemoryGovernor(16 * 1024);
        var budget = new CountingMemoryBudget(memory);
        using var cancellation = new CancellationTokenSource();
        var host = new TestOperationVisitor([]) { MemoryBudget = budget };
        host.EnqueueByteReadResult("x"u8.ToArray(), ByteReadResult.Success(1, reachedEoF: true));
        var readAccounted = false;
        budget.AdmissionSucceeded += () =>
        {
            if (readAccounted)
                cancellation.Cancel();
        };
        using var destination = budget.CreateScope();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.TryReadAllBytesAsync(
            new(71), maximumBytes, _ => readAccounted = true, destination, cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.True(readAccounted);
        Assert.Equal(maximumBytes + 1, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);
        Assert.Empty(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteInputPreservesBytesAndFinalData(bool finalDataIsEof)
    {
        var memory = new MemoryGovernor(64);
        var host = new TestOperationVisitor([]) { MemoryBudget = memory };
        host.EnqueueByteReadResult(new byte[] { 0xff, 13, 10 }, ByteReadResult.Success(3, reachedEoF: false));
        host.EnqueueByteReadResult(new byte[] { 0, 0xc3, 0xa9 }, ByteReadResult.Success(3, finalDataIsEof));
        if (!finalDataIsEof)
            host.EnqueueByteReadResult(ReadOnlyMemory<byte>.Empty, ByteReadResult.EndOfFile);
        var charged = new List<int>();

        using (var destination = memory.CreateScope())
        {
            var result = await host.TryReadAllBytesAsync(new(71), 6, charged.Add, destination, CancellationToken.None);
            Assert.Equal(new byte[] { 0xff, 13, 10, 0, 0xc3, 0xa9 }, Assert.IsType<BoundedReadResult.Complete>(result).Content.ToArray());
            Assert.Equal(6, memory.ReservedBytes);
        }
        Assert.Equal(0, memory.ReservedBytes);
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
