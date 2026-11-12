using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationExpansionTests
{
    [Fact]
    public void SingleOperandWordRetainsItsSegmentsWithoutCopying()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("x"), isGlobQuoted: true)]);
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));

        Assert.Same(word, ExpandedWord.JoinWithSpaces([word], budget));
    }

    [Theory]
    [InlineData("1000000", "${a[@]}")]
    [InlineData("1000000", "${a[*]}")]
    [InlineData("9223372036854775807", "${a[*]}")]
    public async Task SparseArrayExpansionDoesNotMaterializeHoles(string index, string expansion)
    {
        // Charge actual words/copies, not absent entries up to the maximum index.
        var result = await EvaluationTestHelper.RunAsync(
            "a[" + index + "]=x; IFS=; echo \"" + expansion + "\" 2>/errors; echo after");
        var errors = await EndToEndHarness.ReadFileAsync(result.Host, "/errors");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("x\nafter\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("a/", false)]
    [InlineData("[/", false)]
    [InlineData("a/", true)]
    [InlineData("*/", true)]
    public async Task LiteralArgumentsDoNotBuildGlobMetadata(string fragment, bool quoted)
    {
        var text = string.Concat(Enumerable.Repeat(fragment, 65536));
        var argument = quoted ? "'" + text + "'" : text;
        var result = await EvaluationTestHelper.RunAsync("echo " + argument + " >/dev/null; echo ok");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("$x", "0\n")]
    [InlineData("before${x}after", "2\n<before>\n<after>\n")]
    [InlineData("${x}'tail'", "1\n<tail>\n")]
    [InlineData("'head'${x}", "1\n<head>\n")]
    [InlineData("\"\"${x}\"\"", "2\n<>\n<>\n")]
    public async Task WhitespaceRunsDoNotCreateDiscardedFields(string word, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "x='" + new string(' ', 70000) + "'; set -- " + word
            + "; echo \"$#\"; for arg in \"$@\"; do printf '<%s>\\n' \"$arg\"; done");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task SinglePositionalParameterDoesNotAllocateAJoin()
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText("x=\"$*\"; echo ok"), ShellValue.FromText("sh"), ShellValue.FromText(new string('x', 6 * 1024 * 1024))], currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(0, result.ShellExitCode);
        Assert.Equal("ok\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("\"$1${MISSING:?first}\"", "MISSING: first\n")]
    [InlineData("~/$1${MISSING:?first}", "MISSING: first\n")]
    [InlineData("\"$1${OUTER:?${INNER:?first}}\"", "INNER: first\n")]
    public async Task FatalExpansion_DiscardsUnfinishedWordsBeforeMaterialization(string word, string diagnostic)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText("HOME=/home; printf %s " + word + "; printf should-not-run"), ShellValue.FromText("sh"), ShellValue.FromText(new string('x', 6 * 1024 * 1024))],
            currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(1, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("#")]
    [InlineData("##")]
    [InlineData("%")]
    [InlineData("%%")]
    public async Task FatalExpansion_StopsPatternRemovalBeforeCopyingTheValue(string patternOperator)
    {
        var value = new string('x', 6 * 1024 * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText("printf %s \"$1${V" + patternOperator + "${MISSING:?first}}\"; printf should-not-run"), ShellValue.FromText("sh"), ShellValue.FromText(value)],
            currentDirectory: ShellPath.Root, [new EnvironmentVariable("V", ShellValue.FromText(value))], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(1, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("MISSING: first\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("A=(\"$1\" \"$1\") true", 1)]
    [InlineData("A=(\"$@\") true", 2)]
    public async Task CompoundPrefix_UsesOneJoinCopy(string command, int count)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var arguments = new List<string> { "-c", command + "; printf ok", "sh" };
        arguments.AddRange(Enumerable.Repeat(new string('x', (count == 1 ? 11 : 9) * 1024 * 1024 / 4), count));
        var invocation = new CommandInvocation("sh",
            arguments.Select(ShellValue.FromText).ToArray(), currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(0, result.ShellExitCode);
        Assert.Equal("ok", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("A=(\"$1\" \"$1\" \"${MISSING:?first}\") true", 1)]
    [InlineData("A=(\"$@\" \"${MISSING:?first}\") true", 2)]
    public async Task FatalExpansion_DiscardsCompoundPrefixBeforeJoiningCompletedItems(string command, int count)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var arguments = new List<string> { "-c", command + "; printf should-not-run", "sh" };
        arguments.AddRange(Enumerable.Repeat(new string('x', (count == 1 ? 9 : 6) * 1024 * 1024 / 2), count));
        var invocation = new CommandInvocation("sh",
            arguments.Select(ShellValue.FromText).ToArray(), currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(1, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("MISSING: first\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task DeclarationDoesNotCopyAJoinedOperandBeforeDispatch()
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse("declare x=\"$large\"; echo ${#x}"));
        evaluation.SetExportedVariable("large", ShellValue.FromText(new string('x', 9 * 1024 * 1024)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("9437184\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Fact]
    public async Task IndependentTopLevelCallsCompleteUnderSharedMemory()
    {
        var script = "x='" + new string('x', 8192) + "'; "
            + "for ((i=0;i<640;i++)); do echo \"$x\" >/dev/null; done";
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

        for (var index = 0; index < 2; index++)
        {
            var result = await EndToEndHarness.RunScriptAsync(script, host);
            Assert.Equal(0, result.ExitCode);
        }

        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }
}
