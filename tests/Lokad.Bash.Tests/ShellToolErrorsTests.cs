using System.Runtime.InteropServices;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Tools;

namespace Lokad.Bash.Tests;

public sealed class ShellToolErrorsTests
{
    private static readonly OperationError Detail = new("sample ☃ detail\r\n");

    public static TheoryData<DescriptorWriteResult, int> WriteOutcomes => new()
    {
        { DescriptorWriteResult.Open, 0 },
        { DescriptorWriteResult.Closed, 0 },
        { DescriptorWriteResult.WriteFailure, 1 },
    };

    [Theory]
    [MemberData(nameof(WriteOutcomes))]
    public async Task PathDiagnosticOwnsExactBufferUntilWriteSettles(DescriptorWriteResult result, int expectedStatus)
    {
        var path = SamplePath();
        byte[] expected = [.. "tøol: "u8, .. path, .. ": sample ☃ detail\r\n"u8];
        var budget = new MemoryGovernor(expected.Length);
        var host = new TestOperationVisitor([]) { MemoryBudget = budget };
        var started = new TaskCompletionSource<(FileDescriptor Descriptor, ReadOnlyMemory<byte> Content)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAppend = (descriptor, content) => started.SetResult((descriptor, content));
        host.EnqueueAppendTask(settle.Task);
        var descriptor = new FileDescriptor(42);
        var write = ShellToolErrors.WritePathErrorAsync(host, descriptor, "tøol", path, Detail, CancellationToken.None);

        int status;
        try
        {
            var append = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(descriptor, append.Descriptor);
            Assert.Equal(expected.Length, budget.ReservedBytes);
            Assert.False(write.IsCompleted);
            Assert.True(append.Content.Span.SequenceEqual(expected));
            Assert.True(MemoryMarshal.TryGetArray(append.Content, out var backing));
            Assert.Equal(expected.Length, Assert.IsType<byte[]>(backing.Array).Length);
        }
        finally
        {
            settle.SetResult(result);
            status = await write;
        }

        Assert.Equal(expectedStatus, status);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task PathDiagnosticRefusesBeforeHostCallAndLeavesSmallWriteAvailable()
    {
        var budget = new MemoryGovernor(0);
        var host = new TestOperationVisitor([]) { MemoryBudget = budget, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        await Assert.ThrowsAsync<MemoryBudgetExceededException>(() =>
            ShellToolErrors.WritePathErrorAsync(host, FileDescriptor.StdErr, "tool", SamplePath(), Detail, CancellationToken.None));
        Assert.Empty(host.Appends);

        var status = await ShellToolErrors.WritePathErrorAsync(host, FileDescriptor.StdErr, "tool", "x"u8.ToArray(),
            error: null, CancellationToken.None);
        Assert.Equal(0, status);
        Assert.Single(host.Appends);
        Assert.Equal(0, budget.PeakBytes);
    }

    [Fact]
    public async Task PathDiagnosticKeepsReservationUntilCanceledHostWriteSettles()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = new MemoryGovernor(8192);
        var host = new TestOperationVisitor([]) { MemoryBudget = budget };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settle = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeAppend = (_, _) => started.SetResult();
        host.EnqueueAppendTask(settle.Task);
        var write = ShellToolErrors.WritePathErrorAsync(host, FileDescriptor.StdErr, "tool", SamplePath(), Detail,
            cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.False(write.IsCompleted);
            Assert.True(budget.ReservedBytes > 4096);
        }
        finally
        {
            settle.SetCanceled(cancellation.Token);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void DiagnosticKeepsAnonymousReadAndEmptyOperandDistinct()
    {
        var empty = ReadOnlyMemory<byte>.Empty;
        Assert.True(ShellToolErrors.FormatReadError("read", empty, Detail).Span.SequenceEqual("read: sample ☃ detail\r\n"u8));
        Assert.True(ShellToolErrors.FormatPathError("read", empty, Detail).Span.SequenceEqual("read: : sample ☃ detail\r\n"u8));
        byte[] expected = [0xFF, .. ": No such file or directory\n"u8];
        Assert.True(ShellToolErrors.FormatPathError(new byte[] { 0xFF }, error: null).Span.SequenceEqual(expected));
    }

    [Theory]
    [InlineData(FileTransferOperand.Source)]
    [InlineData(FileTransferOperand.Destination)]
    [InlineData(FileTransferOperand.Operation)]
    public void TransferDiagnosticIncludesOnlyAttributedOperands(FileTransferOperand attribution)
    {
        byte[] source = [0xFF, (byte)'a'];
        byte[] destination = [0xFE, (byte)'b'];
        byte[] operand = attribution switch
        {
            FileTransferOperand.Source => source,
            FileTransferOperand.Destination => destination,
            _ => [.. source, .. " -> "u8, .. destination],
        };
        byte[] expected = [.. "copy: "u8, .. operand, .. ": sample ☃ detail\r\n"u8];
        var diagnostic = ShellToolErrors.FormatTransferError("copy", source, destination, new FileTransferError(Detail, attribution));
        Assert.True(diagnostic.Span.SequenceEqual(expected));
    }

    private static byte[] SamplePath()
    {
        var path = new byte[4096];
        path.AsSpan().Fill((byte)'x');
        path[0] = 0xFF;
        return path;
    }
}
