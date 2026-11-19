using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class BuiltinUsageDiagnosticTests
{
    [Theory]
    [InlineData("printf -v bad-name '%s' sample")]
    [InlineData("printf -z sample")]
    [InlineData("printf -v")]
    [InlineData("mapfile -z")]
    [InlineData("readarray -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    public async Task ShellClosedStderrPreservesUsageStatus(string command)
    {
        var result = await EndToEndHarness.RunScriptAsync(command + " 2>&-; printf '<%s>' \"$?\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("mapfile -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    public async Task HostUsageDiagnosticFailureStillReturnsOne(string command)
    {
        var state = new Eval(Parser.Parse(command));
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Synthetic write failure\n")));

        await new Engine().RunAsync(state, host);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdErr, Assert.Single(host.Appends).Descriptor);
    }

    [Theory]
    [InlineData("eval", "-", "[--] [arg ...]", false)]
    [InlineData("source", "-", "[--] filename [arguments]", false)]
    [InlineData(".", "-", "[--] filename [arguments]", false)]
    [InlineData("type", "-t", "[-ftp] [--] [name ...]", true)]
    public async Task InvalidOptionPreservesRawByteAndUsageWriteBoundaries(
        string name, string optionPrefix, string arguments, bool combined)
    {
        var state = new Eval(Parser.Parse($"{name} \"{optionPrefix}$1\" 2>out"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(new byte[] { 0xff })]);
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Synthetic write failure\n")));
        if (!combined)
            host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        var descriptor = new FileDescriptor(9);
        var prefix = System.Text.Encoding.ASCII.GetBytes($"{name}: -");
        byte[] invalid = [.. prefix, 0xff, .. ": invalid option\n"u8];
        var usage = System.Text.Encoding.ASCII.GetBytes($"{name}: usage: {name} {arguments}\n");
        if (combined)
        {
            byte[] expected = [.. invalid, .. usage];
            Assert.Equal(expected, Assert.Single(host.Appends).Content);
        }
        else
            Assert.Collection(host.Appends, append => Assert.Equal(invalid, append.Content),
                append => Assert.Equal(usage, append.Content));
        Assert.All(host.Appends, append => Assert.Equal(descriptor, append.Descriptor));
        Assert.Equal(descriptor, Assert.Single(host.ClosedDescriptors));
        Assert.Equal(2, state.LastExitCode);
    }
}
