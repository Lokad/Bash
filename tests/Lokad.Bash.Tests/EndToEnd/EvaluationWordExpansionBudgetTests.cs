using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationWordExpansionBudgetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BraceCopiesChargeRetainedSubstitutionSource(bool substitutionFirst)
    {
        var substitution = "$(printf '" + new string('x', 128) + "')";
        var operand = substitutionFirst ? substitution + "{a,b}" : "{a,b}" + substitution;
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(": " + operand));
        evaluation.ExpansionBudget.Braces.Charge(0, 16 * 1024 * 1024 - 64);
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.CommandInvocations);
        Assert.Equal(evaluation.ExpansionBudget.Braces.LimitMessage, host.GetOutput(FileDescriptor.StdErr));
    }

    [Theory]
    [InlineData("sh -c 'printf ok'", false)]
    [InlineData("sh -c 'printf ok'", true)]
    [InlineData("eval 'printf ok'", false)]
    [InlineData("eval 'printf ok'", true)]
    public async Task ExecLoginNameChargesItsCopyThroughTheActiveRedirection(string target, bool login)
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(
            "exec " + (login ? "-l " : "") + "-a \"$name\" " + target + " 2>/errors; printf unexpected"));
        evaluation.SetVariable("name", ShellValue.FromText(new string('x', 128 * 1024)));
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 192 * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(login ? 2 : 0, evaluation.LastExitCode);
        Assert.Equal(login ? "" : "ok", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(login ? "shell: word expansion exceeds its word or character limit\n" : "",
            await EndToEndHarness.ReadFileAsync(host, "/errors"));
    }

    [Fact]
    public async Task LiteralCommandQueriesChargeWordEntries()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse("'command' '-v' 'one' 'two'"));
        evaluation.ExpansionBudget.Words.Charge(65535, 0);
        var host = new MockFileSystem();

        await new Engine().RunAsync(evaluation, host);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.CommandInvocations);
        Assert.Equal(evaluation.ExpansionBudget.Words.LimitMessage, host.GetOutput(FileDescriptor.StdErr));
    }

    [Fact]
    public async Task ByteNamePrefixLookupChargesNonmatchingTextNames()
    {
        var source = global::Lokad.Bash.Helpers.ByteMemoryBuilder.CreateUnaccounted(0).Append(": ${!a["u8).Append(0xff)
            .Append("]@}; printf should-not-run"u8).ToMemory();
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(source));
        evaluation.SetVariable(new string('v', 128 * 1024), ShellValue.Empty);
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Equal("shell: word expansion exceeds its word or character limit\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("r=$large", false)]
    [InlineData("r=$large", true)]
    [InlineData("export r=$large", false)]
    [InlineData("export r=$large", true)]
    [InlineData("readonly r=$large", false)]
    [InlineData("readonly r=$large", true)]
    [InlineData(": ${r:=$large}", false)]
    [InlineData(": ${r:=$large}", true)]
    [InlineData("declare -n r=$large", false)]
    [InlineData("declare -n r=$large", true)]
    public async Task UnsetNamerefInitializationChargesCopiesBeforeChangingItsTarget(string assignment, bool element)
    {
        var target = element ? "a[" + new string('0', 100_000) + "]" : new string('v', 100_000);
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(
            "declare -n r; { " + assignment + "; printf should-not-run; } 2>/errors"));
        evaluation.SetExportedVariable("large", ShellValue.FromText(target));
        // Plain-name decoding exceeds 128 KiB; element-reference reparsing exceeds 256 KiB.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - (element ? 256 : 128) * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Equal(ShellValue.FromText(string.Empty), evaluation.Namerefs["r"]);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("shell: word expansion exceeds its word or character limit\n",
            await EndToEndHarness.ReadFileAsync(host, "/errors"));
    }

    [Fact]
    public async Task OrdinaryScalarAssignmentDoesNotReserveNamerefConversions()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse("r=$large; printf %s ${#r}"));
        evaluation.SetExportedVariable("large", ShellValue.FromText(new string('v', 100_000)));
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 128 * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("100000", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("cat /dev/null", false)]
    [InlineData("cat /dev/null", true)]
    [InlineData("command -v cat >/dev/null", false)]
    [InlineData("command -v cat >/dev/null", true)]
    public async Task ExportedNamerefTargetsReuseTheirBytes(string command, bool hidden)
    {
        var loop = "{ for ((i=0;i<100;i++)); do " + command + "; done; printf done; } 2>/errors";
        var script = "declare -nx r='" + new string('v', 2048)
            + "'; " + (hidden ? "f(){ local +x r=local; " + loop + "; }; f" : loop);
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 128 * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("done", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("",
            await EndToEndHarness.ReadFileAsync(host, "/errors"));
    }

    [Theory]
    [InlineData("-n", false, 2)]
    [InlineData("-n", true, 2)]
    [InlineData("-g", true, 0)]
    public async Task NamerefValidationChargesCopiesWhileGlobalLookupReusesTargets(string options, bool element, int exitCode)
    {
        var target = element ? "a[" + new string('0', 2048) + "]" : new string('v', 2048);
        var script = "declare -n r='" + target + "'; { for ((i=0;i<100;i++)); do declare " + options
            + " r; done; printf done; } 2>/errors";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        // Rebinding decodes targets again; global lookup reuses the validated reference.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 128 * 1024);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(exitCode, evaluation.LastExitCode);
        Assert.Equal(exitCode == 0 ? "done" : "", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(exitCode == 0 ? "" : "shell: word expansion exceeds its word or character limit\n",
            await EndToEndHarness.ReadFileAsync(host, "/errors"));
    }

    [Theory]
    [InlineData("declare", "x")]
    [InlineData("export", "x")]
    [InlineData("readonly", "x")]
    [InlineData("local", "x")]
    [InlineData("declare -a", "(x)")]
    public async Task DeclarationNameDecodingUsesTheSharedAllowance(string command, string value)
    {
        var declaration = command + " " + new string('v', 2048) + "=" + value;
        if (command == "local")
            declaration = "f(){ " + declaration + "; }; f";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse("{ " + declaration + "; echo should-not-run; } 2>/errors"));
        // Source bytes can be borrowed, but decoding the binding name still allocates.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 512);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("read -r v", "v+=x")]
    [InlineData("read -r v", "v+=x true")]
    [InlineData("mapfile v", "v[0]+=x")]
    [InlineData("mapfile v", "v+=([0]+=x)")]
    [InlineData("read -r v", "declare v+=x")]
    [InlineData("mapfile v", "declare -a v+=([0]+=x)")]
    [InlineData("read -r v", "declare -n v+=x")]
    [InlineData("read -r v", "declare v=$v")]
    public async Task AssignmentAppendLimitUsesTheActiveErrorRedirectionOnce(string read, string assignment)
    {
        var script = read + " </input; { " + assignment + "; echo should-not-run; } 2>/errors";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        // Reading stored data is not expansion; copying it for += must consume the allowance.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 512);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([("/input", new string('x', 2048) + "\n")]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
    [Fact]
    public async Task ReclassifiedArrayItemChargesItsCopyBeforeReplacingTheArray()
    {
        var script = "{ a=(old); a=(['[']=" + new string('x', 256)
            + "); echo should-not-run; } 2>/errors";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        // The RHS fits, but reconstructing the changed item would exceed the remaining allowance.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 400);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Equal([ShellValue.FromText("old")], evaluation.IndexedArrays["a"]);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("x+=a")]
    [InlineData("x[0]+=a")]
    [InlineData("x+=([0]+=a)")]
    [InlineData("x+=(['[']=a)")]
    [InlineData("x+=a true")]
    public async Task AppendCopyRetainsTheSharedLimit(string assignment)
    {
        var script = "{ x=" + new string('x', 256) + "; " + assignment
            + "; echo should-not-run; } 2>/errors";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        // The seed and RHS fit; the additional copy of the previous value does not.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 400);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("x=a\"$1\"", false)]
    [InlineData("x=a\"$1\"", true)]
    [InlineData("arr=(a\"$1\")", false)]
    [InlineData("arr=(a\"$1\")", true)]
    [InlineData("arr=([0]=a\"$1\")", false)]
    [InlineData("arr=([0]=a\"$1\")", true)]
    [InlineData("declare x=a\"$1\"", false)]
    [InlineData("declare x=a\"$1\"", true)]
    [InlineData("declare arr=(a\"$1\")", false)]
    [InlineData("declare arr=(a\"$1\")", true)]
    [InlineData("declare arr=([0]=a\"$1\")", false)]
    [InlineData("declare arr=([0]=a\"$1\")", true)]
    public async Task AssignmentFlatteningRetainsTheSharedLimit(string assignment, bool alreadyActive)
    {
        // Byte assignment copies twice without text decoding: each copy fits, but both exceed 16 MiB.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var script = alreadyActive
            ? "{ " + assignment + "; } 2>/errors; echo should-not-run"
            : assignment + " 2>/errors; echo should-not-run";
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText(script), ShellValue.FromText("sh"), ShellValue.FromText(new string('x', 9 * 1024 * 1024))], currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);
        var errors = alreadyActive
            ? await EndToEndHarness.ReadFileAsync(host, "/errors")
            : host.StandardError.TextContent;

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        if (alreadyActive)
            Assert.Empty(host.StandardError.TextContent);
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/errors"));
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("declare ", false)]
    [InlineData("declare ", true)]
    public async Task CompoundArrayBraceExpansionRetainsTheSharedLimit(string prefix, bool alreadyActive)
    {
        // This finite cross-product stays small even if the budget check regresses.
        var assignment = prefix + "arr=({1..300}\"\"{1..300})";
        var script = alreadyActive
            ? "{ " + assignment + "; } 2>/errors; echo should-not-run"
            : assignment + " 2>/errors; echo should-not-run";
        var result = await EvaluationTestHelper.RunAsync(script);
        var errors = alreadyActive
            ? await EndToEndHarness.ReadFileAsync(result.Host, "/errors")
            : result.StdErr;

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        if (alreadyActive)
            Assert.Empty(result.StdErr);
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/errors"));
        Assert.Contains("brace expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvalSourceCopiesRetainTheSharedLimit(bool outerRedirection)
    {
        // The large field is only a comment: reparsing cannot disguise an uncharged source copy.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var command = outerRedirection ? "{ eval \"$1\"; }" : "eval \"$1\"";
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText(command + " 2>/errors; echo should-not-run"),
                ShellValue.FromText("sh"), ShellValue.FromText("#" + new string('x', 9 * 1024 * 1024))],
            currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);
        var errors = outerRedirection
            ? await EndToEndHarness.ReadFileAsync(host, "/errors")
            : host.StandardError.TextContent;

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        // eval expands its arguments before applying its own redirections.
        if (outerRedirection)
            Assert.Empty(host.StandardError.TextContent);
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/errors"));
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task ArithmeticPreparationLimitUsesTheActiveErrorRedirectionOnce()
    {
        var script = "{ ((" + new string('0', 128) + ")); echo should-not-run; } 2>/errors";
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(script));
        // Leave enough room for the redirection, but not for the reached arithmetic source.
        evaluation.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 64);
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("declare")]
    [InlineData("typeset")]
    [InlineData("declare -f")]
    [InlineData("declare -F")]
    [InlineData("export -f")]
    [InlineData("readonly -f")]
    public async Task InspectionOperandJoinReportsTheSharedLimitThroughItsRedirection(string builtin)
    {
        // Preparing the value fits; joining the literal inspection operand needs another copy.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse(
            builtin + " -p x=\"$large\" 2>/errors; echo should-not-run"));
        evaluation.SetExportedVariable("large", ShellValue.FromText(new string('x', 9 * 1024 * 1024)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, evaluation.LastExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void OperandJoinsChargeIntermediateSegments()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("x"), isGlobQuoted: true)]);
        var budget = new ExpansionBudget().Words;
        budget.Charge(65535, 0);

        Assert.Throws<ExpansionBudget.LimitExceededException>(() => ExpandedWord.JoinWithSpaces([word, word], budget));
    }

    [Fact]
    public void SingleOperandWordRetainsItsSegmentsWithoutCopying()
    {
        var word = new ExpandedWord([new ExpandedSegment(ShellValue.FromText("x"), isGlobQuoted: true)]);
        var budget = new ExpansionBudget().Words;
        budget.Charge(65536, 16 * 1024 * 1024);

        Assert.Same(word, ExpandedWord.JoinWithSpaces([word], budget));
    }

    [Fact]
    public async Task StringDoublingStopsBeforeExecutingTheNextCommand()
    {
        // Even without the limit this stops at 8 Mi characters; never run an unbounded OOM probe.
        var result = await EvaluationTestHelper.RunAsync(
            "x=a; for ((i=0;i<23;i++)); do x=\"$x$x\"; done; echo should-not-run");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("word expansion exceeds", result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CharacterBudgetAccumulatesAcrossArgumentsAndCommands(bool separateCommands)
    {
        var commands = separateCommands
            ? "for ((i=0;i<1200;i++)); do echo \"$x\" >/dev/null; done; "
            : "echo " + string.Concat(Enumerable.Repeat("\"$x\" ", 1200)) + ">/dev/null; ";
        var script = "x='" + new string('x', 8192) + "'; " + commands + "echo should-not-run";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("word expansion exceeds", result.StdErr);
    }

    [Fact]
    public async Task EmptySplitFieldsConsumeTheWordAllowance()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "IFS=:; x='" + new string(':', 70000) + "'; echo $x; echo should-not-run");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("word expansion exceeds", result.StdErr);
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

    [Fact]
    public async Task PresentAggregateFieldsConsumeTheWordAllowance()
    {
        var arguments = new List<string> { "-c", "IFS=; printf '<%s>' \"$@\"; echo should-not-run", "sh" };
        arguments.AddRange(Enumerable.Repeat(string.Empty, 70000));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(new CommandInvocation("sh", arguments.Select(ShellValue.FromText).ToArray(), currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null), CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Contains("word expansion exceeds", host.StandardError.TextContent);
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

    [Theory]
    [InlineData("")]
    [InlineData(":")]
    public async Task JoinedPositionalParametersRespectTheCharacterAllowance(string separators)
    {
        // Many references to one string make a join much larger than its input storage.
        var arguments = new List<string> { "-c", $"IFS='{separators}'; x=\"$*\"; echo should-not-run", "sh" };
        arguments.AddRange(Enumerable.Repeat(new string('x', 131072), 129));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(new CommandInvocation("sh", arguments.Select(ShellValue.FromText).ToArray(), currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null), CommandLaunchFailureReporting.Host, cancellation.Token);

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Contains("word expansion exceeds", host.StandardError.TextContent);
    }

    [Fact]
    public async Task SinglePositionalParameterDoesNotAllocateAJoin()
    {
        // Decoding argv and one byte-field copy fit; an unnecessary join would exceed the allowance.
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
    [InlineData("echo a\"$1\"", 6 * 1024 * 1024, 1)]
    [InlineData("echo ${MISSING:?$@}", 3 * 1024 * 1024, 2)]
    [InlineData("x=${MISSING:-\"$@\"}", 3 * 1024 * 1024, 2)]
    public async Task FlatteningLimitUsesTheEnclosingErrorRedirection(string command, int length, int count)
    {
        // The input fields fit; combining their expanded values exceeds the allowance.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var arguments = new List<string> { "-c", "{ " + command + "; } 2>/errors; echo should-not-run", "sh" };
        arguments.AddRange(Enumerable.Repeat(new string('x', length), count));
        var invocation = new CommandInvocation("sh",
            arguments.Select(ShellValue.FromText).ToArray(), currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);
        var errors = await EndToEndHarness.ReadFileAsync(host, "/errors");

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("\"$1${MISSING:?first}\"", "MISSING: first\n")]
    [InlineData("~/$1${MISSING:?first}", "MISSING: first\n")]
    [InlineData("\"$1${OUTER:?${INNER:?first}}\"", "INNER: first\n")]
    public async Task FatalExpansion_DiscardsUnfinishedWordsBeforeMaterialization(string word, string diagnostic)
    {
        // Decoding and buffering 6 Mi characters fit; encoding after the fatal error would exceed the allowance.
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
        // Decoding argv and buffering 6 Mi characters fit; copying V after the fatal error would exceed the allowance.
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
        // Decoding argv, expansion and one join fit; another wrapping copy would exceed 16 Mi units.
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
        // Decoding argv and the completed items fit; joining after the fatal item would exceed the allowance.
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
        // Expanding this byte value fits. An unused name=value copy would exceed the allowance.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(global::Lokad.Bash.Syntax.Parser.Parse("declare x=\"$large\"; echo ${#x}"));
        evaluation.SetExportedVariable("large", ShellValue.FromText(new string('x', 9 * 1024 * 1024)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, cancellation.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("9437184\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("command ", false)]
    [InlineData("command ", true)]
    [InlineData("builtin ", false)]
    [InlineData("builtin ", true)]
    [InlineData("exec ", false)]
    [InlineData("exec ", true)]
    public async Task CommandNameDecodingRetainsTheSharedLimit(string prefix, bool alreadyActive)
    {
        // Capturing the selected byte field fits; decoding its command name must charge another copy.
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var command = prefix + "\"$1\"";
        var script = alreadyActive
            ? "{ " + command + "; } 2>/errors; echo should-not-run"
            : command + " 2>/errors; echo should-not-run";
        var invocation = new CommandInvocation("sh",
            [ShellValue.FromText("-c"), ShellValue.FromText(script), ShellValue.FromText("sh"), ShellValue.FromText(new string('x', 9 * 1024 * 1024))],
            currentDirectory: ShellPath.Root, [], stdIn: null, stdOut: null, stdErr: null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await host.RunCommandAsync(invocation, CommandLaunchFailureReporting.Host, cancellation.Token);
        var errors = alreadyActive
            ? await EndToEndHarness.ReadFileAsync(host, "/errors")
            : host.StandardError.TextContent;

        Assert.Equal(2, result.ShellExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        if (alreadyActive)
            Assert.Empty(host.StandardError.TextContent);
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(host, "/errors"));
        Assert.Contains("word expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task IndependentTopLevelCallsHaveSeparateAllowances()
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
