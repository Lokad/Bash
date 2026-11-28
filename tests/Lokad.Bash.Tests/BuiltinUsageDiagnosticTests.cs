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
    [InlineData("unset -z sample")]
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
        Assert.Equal("line 1: printf: -v: option requires an argument\nprintf: usage: printf [-v var] format [arguments]\n",
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

    [Fact]
    public async Task PrintfConsumesTheHelpSpellingAsAnOptionArgument()
    {
        var result = await EvaluationTestHelper.RunAsync("printf -v --help '%s' text");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: printf: `--help': not a valid identifier\n", result.StdErr);
    }

    [Fact]
    public async Task PrintfPositionsTheOptionErrorThroughTheSourcedRedirection()
    {
        const string source = "source ./loaded\nprintf '<%s>' \"$?\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            host.AddFile("/loaded", ":\nprintf -q \\\n2>log\n");
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
                : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("<2>", host.StandardOutput.TextContent);
            Assert.Equal(string.Empty, host.StandardError.TextContent);
            Assert.Equal("./loaded: line 2: printf: -q: invalid option\nprintf: usage: printf [-v var] format [arguments]\n",
                host.ReadFile("/log"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("eval \\\n-xyz", 3, "eval: -x: invalid option", "eval: usage: eval [arg ...]")]
    [InlineData("source --", 2, "source: filename argument required", "source: usage: source filename [arguments]")]
    [InlineData(". -xyz", 2, ".: -x: invalid option", ".: usage: . filename [arguments]")]
    [InlineData("caller 0xno", 2, "caller: 0xno: invalid hex number", "caller: usage: caller [expr]")]
    [InlineData("getopts -xyz spec flag", 2, "getopts: -x: invalid option", "getopts: usage: getopts optstring name [arg ...]")]
    [InlineData("unset -vxyz sample", 2, "unset: -x: invalid option", "unset: usage: unset [-f] [-v] [-n] [name ...]")]
    public async Task BuiltinUsagePrefixesOnlyTheErrorInTheReachedFile(
        string invocation, int line, string diagnostic, string usage)
    {
        await using var host = EndToEndHarness.BuildTree([("/loaded", ":\n" + invocation + " 2>log\n")]);
        var result = await EndToEndHarness.RunScriptAsync("source ./loaded", host);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdErr);
        Assert.Equal($"./loaded: line {line}: {diagnostic}\n{usage}\n", host.ReadFile("/log"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("eval \\\n-xyz 2>log", 3, "eval: -x: invalid option", "eval: usage: eval [arg ...]\n", 2)]
    [InlineData("command eval \\\n-xyz 2>log", 2, "eval: -x: invalid option", "eval: usage: eval [arg ...]\n", 2)]
    [InlineData("caller nope \\\n2>log", 2, "caller: nope: invalid number", "caller: usage: caller [expr]\n", 2)]
    [InlineData("builtin source -- 2>log", 2, "source: filename argument required", "source: usage: source filename [arguments]\n", 2)]
    [InlineData(". \\\n./absent 2>log", 3, "./absent: No such file or directory", "", 1)]
    public async Task ScriptingReportsTheReachedLineInAnUnnamedScript(
        string invocation, int line, string diagnostic, string usage, int failureStatus)
    {
        var script = ":\n" + invocation + "\nprintf '<%s>' \"$?\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal($"<{failureStatus}>", host.StandardOutput.TextContent);
            Assert.True(host.StandardError.Content.IsEmpty);
            Assert.Equal($"line {line}: {diagnostic}\n{usage}", host.ReadFile("/log"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("help \\\n-Z", 2, "line 3: help: -Z: invalid option\nhelp: usage: help [-dms] [pattern ...]\n")]
    [InlineData("help -s : 1>&-", 1, "line 2: help: write error: Bad file descriptor\n")]
    [InlineData("let \\\n--", 1, "line 3: let: expression expected\n")]
    [InlineData("let ''", 1, "")]
    [InlineData("command set \\\n-Z", 2, "line 2: set: -Z: invalid option\n"
        + "set: usage: set [-efB|+efB] [-o name|+o name] [-|+|--] [arg ...]\n")]
    [InlineData("set -o \\\nsynthetic_missing", 2, "line 2: set: synthetic_missing: invalid option name\n")]
    public async Task BuiltinDiagnosticsUseTheReachedLineInAnUnnamedScript(string invocation, int failureStatus, string diagnostic)
    {
        var script = "set -- kept\n" + invocation + " 2>log\nprintf '<%s><%s>' \"$?\" \"$1\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal($"<{failureStatus}><kept>", host.StandardOutput.TextContent);
            Assert.True(host.StandardError.Content.IsEmpty);
            Assert.Equal(diagnostic, host.ReadFile("/log"));
            Assert.Equal(0, host.OpenFileCount);
        }
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
    [InlineData("eval", "-", "[arg ...]", false)]
    [InlineData("source", "-", "filename [arguments]", false)]
    [InlineData(".", "-", "filename [arguments]", false)]
    [InlineData("type", "-t", "[-afptP] name [name ...]", true)]
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
        var prefix = System.Text.Encoding.ASCII.GetBytes($"line 1: {name}: -");
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
