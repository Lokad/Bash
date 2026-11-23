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
    [InlineData("printf -v out -v")]
    [InlineData("mapfile -z")]
    [InlineData("readarray -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    [InlineData("trap -pz")]
    [InlineData("trap unknown_name")]
    public async Task ShellClosedStderrPreservesUsageStatus(string command)
    {
        var result = await EndToEndHarness.RunScriptAsync(command + " 2>&-; printf '<%s>' \"$?\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("printf -v")]
    [InlineData("mapfile -z")]
    [InlineData("read -z")]
    [InlineData("getopts -z option")]
    [InlineData("trap -pz")]
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
    [InlineData("printf -v")]
    [InlineData("printf -v 'a[i++]' -v")]
    [InlineData("printf -v $missing")]
    public async Task PrintfMissingDestinationReportsTheOptionThroughReachedStderr(string command)
    {
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync("a=(old); i=0; unset missing; " + command
            + " 2>diagnostic; printf '<%s><%s><%s>' \"$?\" \"$i\" \"${a[0]}\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2><0><old>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal("printf: -v: option requires an argument\nprintf: usage: printf [-v var] format [arguments]\n",
            await EndToEndHarness.ReadFileAsync(host, "/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("printf -v out")]
    [InlineData("printf -vout")]
    public async Task PrintfMissingFormatReportsUsageAfterSelectingTheDestination(string command)
    {
        await using var host = new TestHost();
        var result = await EndToEndHarness.RunScriptAsync("out=old; " + command
            + "; printf '<%s><%s>' \"$?\" \"$out\"", host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2><old>", result.StdOut);
        Assert.Equal("printf: usage: printf [-v var] format [arguments]\n", result.StdErr);
    }

    [Theory]
    [InlineData("dirs -c stray")]
    [InlineData("pushd -x")]
    [InlineData("popd stray")]
    [InlineData("eval -q")]
    [InlineData("source -q")]
    [InlineData(". -q")]
    [InlineData("source")]
    public async Task StackAndScriptingUsageSkipShellClosedStderr(string command)
    {
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);
        host.EnqueueAppendResult(DescriptorWriteResult.WriteFailure);

        var result = await BashScript.RunAsync(host, command + " 2>&-", EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.Appends);
    }

    [Theory]
    [InlineData("eval", "-", "[--] [arg ...]", false)]
    [InlineData("source", "-", "[--] filename [arguments]", false)]
    [InlineData(".", "-", "[--] filename [arguments]", false)]
    [InlineData("type", "-t", "[-aftpP] [--] [name ...]", true)]
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
