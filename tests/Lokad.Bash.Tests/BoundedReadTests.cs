using System.Runtime.InteropServices;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class BoundedReadTests
{
    [Fact]
    public async Task ReusableLineGrowthReturnsOnlyTheNextRecordAndKeepsTheOverlapAdmitted()
    {
        byte[] first = [0xff, 13, 0, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 10];
        byte[] second = [0, 13, 0xfe, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e', 10];
        await using var host = new MockFileSystem();
        host.SetStandardInputBytes([.. first, .. second]);
        var governor = new MemoryGovernor(24);
        using var budget = governor.CreateScope();
        using var output = new ByteMemoryBuilder(8, budget);

        var previous = await DescriptorReader.ReadReusableLineAsync(host, Read, output, CancellationToken.None);
        Assert.False(previous.ReachedEoF);
        Assert.Equal(first, previous.Content.ToArray());
        var current = await DescriptorReader.ReadReusableLineAsync(host, Read, output, CancellationToken.None);

        Assert.True(current.ReachedEoF);
        Assert.Equal(second, current.Content.ToArray());
        Assert.Equal(16, governor.ReservedBytes);
        Assert.Equal(24, governor.PeakBytes);
        output.Dispose();
        Assert.Equal(0, governor.ReservedBytes);

        static ValueTask<DescriptorReadBuffer> Read(MockFileSystem source, DescriptorReadRequest request, CancellationToken token) =>
            source.ReadAsync(FileDescriptor.StdIn, request, token);
    }

    [Fact]
    public void RefusedBufferGrowthPreservesTheWrittenPrefixUntilRetry()
    {
        var governor = new MemoryGovernor(24);
        using var budget = governor.CreateScope();
        using var output = new ByteMemoryBuilder(8, budget);
        output.Append("original"u8);
        var original = output.ToMemory();
        output.Truncate(3);

        using (budget.Reserve(1))
        {
            Assert.False(output.TryEnsureCapacity(9));
            Assert.Equal(8, output.Capacity);
            Assert.Equal("ori"u8.ToArray(), output.ToMemory().ToArray());
            Assert.Equal(9, governor.ReservedBytes);
        }
        Assert.True(output.TryEnsureCapacity(9));
        Assert.Equal("ori"u8.ToArray(), output.ToMemory().ToArray());
        output.Append(original);
        Assert.Equal("orioriginal"u8.ToArray(), output.ToMemory().ToArray());
        Assert.Equal("original"u8.ToArray(), original.ToArray());
        Assert.Equal(16, governor.ReservedBytes);
        Assert.Equal(24, governor.PeakBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SizeRejectedSourceBatchSkipsBothCopiesAndPreservesTheBorrowOutcome(bool readFails)
    {
        var memory = new MemoryGovernor(0);
        using var budget = memory.CreateScope();
        using var output = ByteMemoryBuilder.CreateUnaccounted(4).Append("#\n"u8);
        var destination = new SourceReadDestination(output, 6);
        var consumed = new List<int>();
        var owner = new DescriptorReadBufferOwner(consumed.Add, budget);
        var content = "ab\ncd\nef\n"u8.ToArray();
        var error = new OperationError("read failed\n");
        var result = readFails ? ByteReadResult.Failure(content.Length, error, reachedEoF: false)
            : ByteReadResult.Success(content.Length, reachedEoF: true);

        var batch = await DescriptorReader.ReadSourceAsync(owner, Read, destination, budget, CancellationToken.None);

        Assert.Empty(batch.Content.ToArray());
        Assert.Empty(batch.LineStartOffsets);
        Assert.Equal(!readFails, batch.IsTooLarge);
        Assert.Equal(readFails ? error : (OperationError?)null, batch.Error);
        Assert.Equal([readFails ? 0 : 5], consumed);
        Assert.Equal("#\n"u8.ToArray(), output.ToMemory().ToArray());
        Assert.Equal(0, memory.PeakBytes);

        ValueTask<DescriptorReadBuffer> Read(DescriptorReadBufferOwner source, DescriptorReadRequest _, CancellationToken __) =>
            ValueTask.FromResult(source.Borrow(content, result, canRetain: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceGrowthAfterTheBorrowReusesOnlyImmutableHostBytes(bool canRetain)
    {
        byte[] input = [0xff, 13, 10, (byte)'b', 13];
        byte[] expected = [(byte)'#', 10, .. input];
        var memory = new MemoryGovernor(canRetain ? 0 : input.Length);
        using var budget = memory.CreateScope();
        using var output = ByteMemoryBuilder.CreateUnaccounted(2).Append("#\n"u8);
        var owner = new DescriptorReadBufferOwner(consumed => Assert.Equal(input.Length, consumed), budget);

        var result = await DescriptorReader.ReadSourceAsync(owner, Read, new SourceReadDestination(output, 64), budget,
            CancellationToken.None);

        Assert.Equal(ReadResult.EndOfFile, result);
        Assert.Equal(canRetain, owner.WasRetained);
        Assert.Equal(expected, output.ToMemory().ToArray());
        Assert.Equal(input.Length, owner.Position);
        Assert.Equal(canRetain ? 0 : input.Length, memory.PeakBytes);
        Assert.Equal(0, memory.ReservedBytes);

        ValueTask<DescriptorReadBuffer> Read(DescriptorReadBufferOwner source, DescriptorReadRequest _, CancellationToken __)
        {
            var buffer = source.Borrow(input, ByteReadResult.Success(input.Length, reachedEoF: true), canRetain);
            if (!canRetain)
                buffer.ReleaseWith(() => Array.Clear(input));
            return ValueTask.FromResult(buffer);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublicLineReadOwnsItsBatchWhenGivenAnExplicitDestination(bool canRetain)
    {
        const int OffsetBytes = 32 + sizeof(int);
        ReadOnlyMemory<byte> input = new byte[] { 0xff, 13, 10, (byte)'b', 13 };
        var expectedBytes = OffsetBytes + input.Length;
        var memory = new MemoryGovernor(expectedBytes);
        using var budget = memory.CreateScope();
        var owner = new DescriptorReadBufferOwner(consumed => Assert.Equal(input.Length, consumed), budget);

        var batch = await DescriptorReader.ReadLinesAsync(owner, Read, 2, budget, CancellationToken.None);

        Assert.False(owner.WasRetained);
        Assert.False(batch.Content.Equals(input));
        Assert.Equal(input.ToArray(), batch.Content.ToArray());
        Assert.Equal([3], batch.LineStartOffsets);
        Assert.True(batch.ReachedEoF);
        Assert.Equal(input.Length, owner.Position);
        Assert.Equal(expectedBytes, memory.PeakBytes);
        budget.Dispose();
        Assert.Equal(0, memory.ReservedBytes);

        ValueTask<DescriptorReadBuffer> Read(DescriptorReadBufferOwner source, DescriptorReadRequest _, CancellationToken __) =>
            ValueTask.FromResult(source.Borrow(input, ByteReadResult.Success(input.Length, reachedEoF: true), canRetain));
    }

    [Theory]
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 2, 3)]
    [InlineData(3, DescriptorReader.BufferSize, 1)]
    public async Task ExtendedLineWindowKeepsOffsetsAndTheUnreadSuffix(int limit, int tailLength, int expectedRecords)
    {
        var governor = new MemoryGovernor(8 * DescriptorReader.BufferSize);
        await using var host = new MockFileSystem { MemoryBudget = governor };
        var first = new byte[DescriptorReader.BufferSize + 1];
        Array.Fill(first, (byte)'a');
        first[^2] = 13;
        first[^1] = 10;
        byte[] second = [0xff, 10];
        var tail = new byte[tailLength];
        Array.Fill(tail, (byte)'z');
        byte[] input = [.. first, .. second, .. tail];
        host.SetStandardInputBytes(input);
        using var owner = governor.CreateScope();

        var batch = await host.ReadLinesAsync(FileDescriptor.StdIn, limit, owner, CancellationToken.None);

        var expectedLength = expectedRecords switch
        {
            1 => first.Length,
            2 => first.Length + second.Length,
            _ => input.Length
        };
        Assert.Equal(input[..expectedLength], batch.Content.ToArray());
        Assert.Equal(new[] { first.Length, first.Length + second.Length }.Take(expectedRecords - 1), batch.LineStartOffsets);
        Assert.Equal(expectedRecords == 3, batch.ReachedEoF);
        var offsetBytes = expectedRecords == 1 ? 0 : 32 + sizeof(int) * (expectedRecords - 1);
        Assert.Equal(expectedLength + offsetBytes, governor.ReservedBytes);
        Assert.Equal(2, host.ReadCallCount);
        if (!batch.ReachedEoF)
        {
            var remainder = await host.TryReadAllBytesAsync(FileDescriptor.StdIn, input.Length,
                static _ => { }, owner, CancellationToken.None);
            Assert.Equal(input[expectedLength..], Assert.IsType<BoundedReadResult.Complete>(remainder).Content.ToArray());
        }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task ExtendedLineReadRestartsWhenAnotherAliasConsumesTheExaminedPrefix()
    {
        var governor = new MemoryGovernor(1024);
        using var budget = governor.CreateScope();
        var position = 0;
        var owner = new DescriptorReadBufferOwner(consumed => position += consumed, budget);
        byte[] input = [.. "prefix"u8, (byte)'x', 10, 0xff, 13];
        var reads = 0;

        var batch = await DescriptorReader.ReadLinesAsync(input, Read, 2, budget, CancellationToken.None);

        Assert.Equal(new byte[] { (byte)'x', 10, 0xff, 13 }, batch.Content.ToArray());
        Assert.Equal(new[] { 2 }, batch.LineStartOffsets);
        Assert.True(batch.ReachedEoF);
        Assert.Equal(input.Length, position);
        Assert.Equal(2, reads);
        budget.Dispose();
        Assert.Equal(0, governor.ReservedBytes);

        ValueTask<DescriptorReadBuffer> Read(byte[] data, DescriptorReadRequest request, CancellationToken token)
        {
            var prefix = data.AsMemory(0, "prefix"u8.Length);
            token.ThrowIfCancellationRequested();
            if (reads++ == 0)
                return ValueTask.FromResult(owner.Borrow(prefix, ByteReadResult.Success(prefix.Length, false), canRetain: true));
            using (var alias = owner.Borrow(prefix, ByteReadResult.Success(prefix.Length, false), canRetain: true))
                alias.Consume(prefix.Length);
            Assert.Equal(1, request.MinimumBytes);
            var remaining = data.AsMemory(position);
            return ValueTask.FromResult(owner.Borrow(remaining, ByteReadResult.Success(remaining.Length, true), canRetain: true));
        }
    }

    [Theory]
    [InlineData(2, int.MaxValue, true)]
    [InlineData(1, int.MaxValue, false)]
    [InlineData(2, 4, false)]
    public async Task LineBatchIncludesUnterminatedTailOnlyAtEofWithRoom(int limit, int window, bool includesTail)
    {
        var governor = new MemoryGovernor(1024);
        await using var host = new MockFileSystem { MemoryBudget = governor, MaximumReadWindowBytes = window };
        byte[] first = [(byte)'a', 13, 10];
        byte[] tail = [0xff, 13];
        byte[] input = [.. first, .. tail];
        host.SetStandardInputBytes(input);
        using var owner = governor.CreateScope();

        var batch = await host.ReadLinesAsync(FileDescriptor.StdIn, limit, owner, CancellationToken.None);

        Assert.Equal(includesTail ? input : first, batch.Content.ToArray());
        Assert.Equal(includesTail ? [first.Length] : Array.Empty<int>(), batch.LineStartOffsets);
        Assert.Equal(includesTail, batch.ReachedEoF);
        Assert.Equal(includesTail ? input.Length + 32 + sizeof(int) : first.Length, governor.ReservedBytes);
        Assert.Equal(1, host.ReadCallCount);
        if (!includesTail)
        {
            var remaining = await host.ReadLinesAsync(FileDescriptor.StdIn, limit, owner, CancellationToken.None);
            Assert.Equal(tail, remaining.Content.ToArray());
            Assert.Empty(remaining.LineStartOffsets);
            Assert.True(remaining.ReachedEoF);
            Assert.Equal(2, host.ReadCallCount);
        }
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(4096, false)]
    public async Task TerminalLineBatchGrowsOutputToItsExactLength(int precedingBytes, bool readFirstLine)
    {
        var terminal = new byte[4097];
        terminal[0] = 0xff;
        terminal[2] = 13;
        terminal[3] = 10;
        terminal[^1] = (byte)'z';
        var expected = new byte[precedingBytes + terminal.Length - (readFirstLine ? 4 : 0)];
        terminal.AsSpan(readFirstLine ? 4 : 0).CopyTo(expected.AsSpan(precedingBytes));
        var expectedPeak = precedingBytes + terminal.Length + expected.Length;
        var governor = new MemoryGovernor(expectedPeak);
        using var owner = governor.CreateScope();
        var reads = 0;
        using var reader = AsyncLineReader.CreateStreaming((_, budget, _) =>
        {
            var final = ++reads == (precedingBytes == 0 ? 1 : 2);
            var content = ScopedMemory.Copy(final ? terminal : new byte[precedingBytes], budget);
            return ValueTask.FromResult(ReadResult.Success(content, final ? [4] : [], reachedEoF: final));
        }, CancellationToken.None, 2, owner);

        if (readFirstLine)
        {
            Assert.False(await reader.ReachedEof());
            Assert.Equal(terminal[..4], reader.ReadLine().Raw.ToArray());
        }

        var result = await reader.TryReadToEndAsync(expected.Length);

        var content = Assert.IsType<BoundedReadResult.Complete>(result).Content;
        Assert.Equal(expected, content.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(content, out var segment));
        Assert.Equal(expected.Length, Assert.IsType<byte[]>(segment.Array).Length);
        Assert.Equal(precedingBytes == 0 ? 1 : 2, reads);
        Assert.Equal(expected.Length, governor.ReservedBytes);
        Assert.Equal(expectedPeak, governor.PeakBytes);
        reader.Dispose();
        Assert.Equal(expected.Length, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(int.MaxValue, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public async Task BorrowedWholeInputAdmitsOnlyTheExactResultAndAlreadyBufferedOffsets(int batchSize, bool readFirstLine)
    {
        const int LineLength = 32;
        var input = new byte[257 * LineLength + 3];
        for (var end = LineLength; end < input.Length; end += LineLength)
        {
            input[end - 2] = 13;
            input[end - 1] = 10;
        }
        input[^3] = 0xff;
        input[^2] = 13;
        var start = readFirstLine ? LineLength : 0;
        var expectedLength = input.Length - start;
        var offsets = readFirstLine ? MemoryCapacity.ArrayBackingBytes<int>(batchSize - 1) : 0;
        var governor = new MemoryGovernor(expectedLength + offsets);
        using var owner = governor.CreateScope();
        using var reader = AsyncLineReader.CreateStreaming(input, CancellationToken.None, batchSize, owner);

        if (readFirstLine)
        {
            Assert.False(await reader.ReachedEof());
            Assert.Equal(input[..LineLength], reader.ReadLine().Raw.ToArray());
            Assert.Equal(offsets, governor.ReservedBytes);
        }

        var result = await reader.TryReadToEndAsync(expectedLength);

        var content = Assert.IsType<BoundedReadResult.Complete>(result).Content;
        Assert.Equal(input[start..], content.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(content, out var segment));
        var backing = Assert.IsType<byte[]>(segment.Array);
        Assert.Equal(expectedLength, backing.Length);
        Assert.NotSame(input, backing);
        Assert.True(await reader.ReachedEof());
        Assert.Equal(expectedLength, governor.ReservedBytes);
        Assert.Equal(expectedLength + offsets, governor.PeakBytes);
        reader.Dispose();
        Assert.Equal(expectedLength, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedBorrowedWholeInputRetainsTheFirstRejectedBatch(bool refuseMemory)
    {
        byte[] input = "first\nsecond-continued\nlast\n"u8.ToArray();
        var governor = new MemoryGovernor(refuseMemory ? 20 : 1024);
        using var owner = governor.CreateScope();
        using var reader = AsyncLineReader.CreateStreaming(input, CancellationToken.None, 1, owner);

        if (refuseMemory)
            await Assert.ThrowsAsync<MemoryBudgetExceededException>(() => reader.TryReadToEndAsync(input.Length).AsTask());
        else
            Assert.IsType<BoundedReadResult.TooLarge>(await reader.TryReadToEndAsync(7));

        Assert.Equal(0, governor.ReservedBytes);
        Assert.False(await reader.ReachedEof());
        Assert.Equal("second-continued\n"u8.ToArray(), reader.ReadLine().Raw.ToArray());
        Assert.Equal("last\n"u8.ToArray(), (await reader.ReadToEndAsync()).ToArray());
        Assert.True(await reader.ReachedEof());
    }

    [Fact]
    public async Task CanceledBorrowedWholeInputLeavesBufferedLinesAvailable()
    {
        var governor = new MemoryGovernor(1024);
        using var owner = governor.CreateScope();
        using var cancellation = new CancellationTokenSource();
        using var reader = AsyncLineReader.CreateStreaming("first\nnext\nlast"u8.ToArray(), cancellation.Token, 2, owner);
        Assert.False(await reader.ReachedEof());
        var offsets = governor.ReservedBytes;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.TryReadToEndAsync().AsTask());

        Assert.Equal(offsets, governor.ReservedBytes);
        Assert.Equal("first\n"u8.ToArray(), reader.ReadLine().Raw.ToArray());
        Assert.Equal("next\n"u8.ToArray(), reader.ReadLine().Raw.ToArray());
        reader.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

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
