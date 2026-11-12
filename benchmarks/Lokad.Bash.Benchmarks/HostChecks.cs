using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Benchmarks;

internal static class HostChecks
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var memory = new MemoryGovernor(4 * 1024 * 1024);
        var data = Enumerable.Range(0, 2 * 64 * 1024 + 257).Select(i => (byte)(i * 7)).ToArray();

        var pipe = new BoundedBytePipe(memory, deadline.Token);
        var input = new PipeInput(pipe, memory);
        var output = new PipeOutput(pipe);
        var writing = output.WriteAsync(data, deadline.Token).AsTask();
        Check(!writing.IsCompleted, "Full transport did not apply backpressure.");
        var buffer = await input.ReadAsync(new DescriptorReadRequest(data.Length, data.Length), deadline.Token);
        Check(buffer.Content.Span.SequenceEqual(data), "Consumer lookahead beyond pipe capacity lost bytes.");
        Check((await writing).CanAcceptMore, "Producer did not resume after consumption.");
        var closing = input.CloseAsync();
        Check(!closing.IsCompleted, "Close retired storage with a pending borrowed read.");
        buffer.Consume(buffer.Content.Length);
        buffer.Dispose();
        await closing;
        await output.CloseAsync();
        Check(memory.ReservedBytes == 0, "Borrow/close control retained pipe storage.");

        using (var retainedOwner = memory.CreateScope())
        {
            pipe = new BoundedBytePipe(retainedOwner, deadline.Token);
            input = new PipeInput(pipe, retainedOwner);
            output = new PipeOutput(pipe);
            await output.WriteAsync(data.AsMemory(0, 4096), deadline.Token);
            ReadOnlyMemory<byte> retained;
            using (var source = await input.ReadAsync(DescriptorReadRequest.UpTo(4096), deadline.Token))
            {
                retained = source.Retain(17);
                source.Consume(source.Content.Length);
            }
            await input.CloseAsync(); await output.CloseAsync();
            Check(retained.Span.SequenceEqual(data.AsSpan(0, 17)) && memory.ReservedBytes >= 17,
                "Closing a descriptor ended the ownership of retained read bytes.");
        }
        Check(memory.ReservedBytes == 0, "Retained bytes were not refunded when their consuming owner ended.");

        pipe = new BoundedBytePipe(memory, deadline.Token);
        input = new PipeInput(pipe, memory);
        output = new PipeOutput(pipe);
        writing = output.WriteAsync(data, deadline.Token).AsTask();
        Check(!writing.IsCompleted, "Early-close control was not blocked on capacity.");
        await input.CloseAsync();
        var write = await writing;
        Check(!write.CanAcceptMore && !write.IsError, "Reader closure was not distinct from a write failure.");
        await output.CloseAsync();
        Check(memory.ReservedBytes == 0, "Early close retained transport storage.");

        using var lifetime = new CancellationTokenSource();
        pipe = new BoundedBytePipe(memory, lifetime.Token);
        input = new PipeInput(pipe, memory);
        output = new PipeOutput(pipe);
        var reading = input.ReadAsync(DescriptorReadRequest.UpTo(4096), CancellationToken.None).AsTask();
        Check(!reading.IsCompleted, "Empty open pipe returned before data or closure.");
        lifetime.Cancel();
        using (var failed = await reading.WaitAsync(deadline.Token)) Check(failed.Result.IsError, "Lifetime cancellation did not abort a read using another token.");
        Check((await output.WriteAsync(data, CancellationToken.None)).IsError, "Lifetime cancellation did not abort a write using another token.");
        await input.CloseAsync(); await output.CloseAsync();
        Check(memory.ReservedBytes == 0, "Aborted pipe was not released after cleanup.");

        using var bufferedLifetime = new CancellationTokenSource();
        pipe = new BoundedBytePipe(memory, bufferedLifetime.Token);
        input = new PipeInput(pipe, memory);
        output = new PipeOutput(pipe);
        await output.WriteAsync(data.AsMemory(0, 4096), deadline.Token);
        using (var prefix = await input.ReadAsync(DescriptorReadRequest.UpTo(1), deadline.Token)) prefix.Consume(1);
        bufferedLifetime.Cancel();
        using (var failed = await input.ReadAsync(DescriptorReadRequest.UpTo(1), CancellationToken.None))
            Check(failed.Result.IsError, "Lifetime cancellation exposed prefetched bytes as a successful read.");
        await input.CloseAsync(); await output.CloseAsync();
        Check(memory.ReservedBytes == 0, "Buffered abort retained storage after cleanup.");

        pipe = new BoundedBytePipe(memory, deadline.Token);
        input = new PipeInput(pipe, memory);
        output = new PipeOutput(pipe);
        await output.WriteAsync(data.AsMemory(0, 4096), deadline.Token);
        using var cancelledRead = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        reading = input.ReadAsync(new DescriptorReadRequest(8192, 8192), cancelledRead.Token).AsTask();
        Check(!reading.IsCompleted, "Read-minimum control did not retain its partial prefix.");
        cancelledRead.Cancel();
        try { using var unexpected = await reading; throw new InvalidOperationException("Read cancellation did not propagate."); }
        catch (OperationCanceledException) when (cancelledRead.IsCancellationRequested) { }
        using (var retained = await input.ReadAsync(DescriptorReadRequest.UpTo(4096), deadline.Token))
        {
            Check(retained.Content.Span.SequenceEqual(data.AsSpan(0, 4096)), "Cancelled lookahead lost unconsumed bytes.");
            retained.Consume(retained.Content.Length);
        }
        await output.CloseAsync();
        using (var eof = await input.ReadAsync(DescriptorReadRequest.UpTo(4096), deadline.Token))
            Check(eof.Content.IsEmpty && eof.Result.ReachedEoF, "Closed writer did not produce EOF after buffered bytes drained.");
        await input.CloseAsync();
        Check(memory.ReservedBytes == 0, "Cancelled read retained storage after cleanup.");

        var refused = new MemoryGovernor(1);
        try { _ = new BoundedBytePipe(refused, deadline.Token); throw new InvalidOperationException("Pipe admission did not fail."); }
        catch (MemoryBudgetExceededException) { }
        Check(refused.ReservedBytes == 0, "Refused acquisition left storage charged.");

        var partial = new MemoryGovernor(16 * 1024 + 96 * 1024 + 1);
        await using (var host = new BenchmarkHost(partial, ReadOnlyMemory<byte>.Empty,
            new Dictionary<string, ReadOnlyMemory<byte>>(), null, null))
        {
            var baseline = partial.ReservedBytes;
            try { await host.CreatePipeAsync(deadline.Token, deadline.Token); throw new InvalidOperationException("Partial acquisition did not refuse."); }
            catch (MemoryBudgetExceededException) { }
            Check(partial.PeakBytes >= baseline + 96 * 1024 && partial.ReservedBytes == baseline,
                "Partial pipe acquisition failed to refund its already acquired transport.");
        }
        Check(partial.ReservedBytes == 0, "Refused host pipe retained storage after host cleanup.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
