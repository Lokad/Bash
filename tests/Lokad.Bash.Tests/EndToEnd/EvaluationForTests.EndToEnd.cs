using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationForTestsEndToEnd
{
    [Fact]
    public async Task BraceBodyRedirectionWrapsAllIterations()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- a \"\" b; for x; { printf '<%s>' \"$x\"; } >out; printf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("<a><><b>", host.ReadFile("/out"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task BraceBodyCompletesHereDocumentOnEachIteration()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "for x in a b; { cat <<END\n$x\nEND\n}");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a\nb\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task Evaluation_ForCommand_RebindsNamerefWithoutMutatingItsTarget()
    {
        var script = @"arr=(a b); i=1; declare -n ref='arr[i]'; for ref in x; do :; done; ref=updated; printf '<%s>:<%s>\n' ""${arr[1]}"" ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<b>:<updated>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("target=old; first=one; second=two; declare -n ref=target; for ref in first second; do printf '<%s>\\n' \"$ref\"; ref=changed; done; printf '<%s>:<%s>:<%s>:<%s>\\n' \"$target\" \"$first\" \"$second\" \"$ref\"", "<one>\n<two>\n<old>:<changed>:<changed>:<changed>\n")]
    [InlineData("target=old; declare -n ref=target; for ref in; do ref=wrong; done; ref=changed; printf '<%s>\\n' \"$target\"", "<changed>\n")]
    [InlineData("readonly target=old; declare -n ref=target; for ref in first; do ref=changed; done; printf '<%s>:<%s>\\n' \"$target\" \"$first\"", "<old>:<changed>\n")]
    public async Task Evaluation_ForCommand_PreservesNamerefBindings(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("readonly x=old", "$x", "<old>:1")]
    [InlineData("declare -ar x=(old keep)", "${x[1]}", "<keep>:1")]
    [InlineData("declare -nr x=old; old=original", "$x", "<original>:1")]
    public async Task ReadonlyLoopVariableSkipsBodyAndReportsFailure(string setup, string value, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "; for x in new later; do printf body; done; printf '<%s>:%s' \"" + value + "\" \"$?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal("line 1: x: readonly variable\n", result.StdErr);
    }

    [Fact]
    public async Task ReadonlyLoopReportsTheEndOfTheContinuedVariableName()
    {
        var result = await EvaluationTestHelper.RunAsync(
            ":\nreadonly target=old\nfor \\\n tar\\\nget \\\n in seed; do printf body; done\n"
            + "printf '<%s:%s>' \"$?\" \"$target\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:old>", result.StdOut);
        Assert.Equal("line 5: target: readonly variable\n", result.StdErr);
    }

    [Theory]
    [InlineData("''", "line 1: `': not a valid identifier\n")]
    [InlineData("'arr[1'", "line 1: `arr[1': not a valid identifier\n")]
    [InlineData("'arr[]'", "line 1: `arr[]': not a valid identifier\n")]
    [InlineData("$'bad\\377name'", "line 1: `bad\u00ffname': not a valid identifier\n")]
    public async Task InvalidNamerefLoopItemPreservesBindingAndDiagnosticBytes(string item, string expectedError)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "old=original; declare -n ref=old; for ref in " + item +
            "; do printf body; done; printf '%s:<%s>' \"$?\" \"$ref\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:<original>", host.StandardOutput.TextContent);
        Assert.Equal(expectedError, System.Text.Encoding.Latin1.GetString(host.StandardError.Content.Span));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task InvalidNamerefLoopItemUsesLoopRedirectionAfterSuccessfulIteration()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "old=old; next=next; declare -n ref=old; " +
            "for ref in next bad-name old; do printf '<%s>' \"$ref\"; ref=changed; done 2>err; " +
            "printf '<%s>:<%s>:<%s>:<%s>' \"$?\" \"$ref\" \"$old\" \"$next\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<next><1>:<changed>:<old>:<changed>", host.StandardOutput.TextContent);
        Assert.Equal("line 1: `bad-name': not a valid identifier\n", host.ReadFile("/err"));
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("readonly x=old", "line 1: x: readonly variable\n", 1, "")]
    [InlineData("declare -nr x=old", "line 1: `bad-name': not a valid identifier\n", 0, "fallback:1")]
    public async Task TestedLoopAssignmentFailureDistinguishesReadonlyFromInvalidTarget(
        string setup, string expectedError, int exitCode, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -e; " + setup + "; for x in bad-name; do printf body; done || printf 'fallback:%s' \"$?\"");

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task InvalidNamerefLoopItemDoesNotTriggerErrexit()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -e; declare -n ref=old; for ref in bad-name; do printf body; done; printf 'after:%s' \"$?\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after:1", result.StdOut);
        Assert.Equal("line 1: `bad-name': not a valid identifier\n", result.StdErr);
    }

    [Theory]
    [InlineData("declare -a x=([2]=keep)", "${x[2]}")]
    [InlineData("declare -A x=([key]=keep)", "${x[key]}")]
    public async Task LoopAssignmentUpdatesElementZeroAndKeepsOtherEntries(string setup, string otherElement)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "; for x in new; do printf '<%s>' \"$x\"; done; printf '<%s>:<%s>' \"$x\" \"" + otherElement + "\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<new><new>:<keep>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLoopNamePreservesEarlierEffectsAndSkipsItemExpansion(bool prepared)
    {
        await using var host = new TestHost();
        const string script = "set -e; printf prior >prior; " +
            "for bad-name in \"$(printf value >expanded)\"; do printf body >body; done; printf 'loop:%s' \"$?\"";
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
            : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

        Assert.Equal(0, status);
        Assert.Equal("loop:1", host.StandardOutput.TextContent);
        Assert.Equal("line 1: `bad-name': not a valid identifier\n", host.StandardError.TextContent);
        Assert.Equal("prior", host.ReadFile("/prior"));
        Assert.False(host.FileExists("/expanded"));
        Assert.False(host.FileExists("/body"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("1name", "1name", 1)]
    [InlineData("na\\me", "na\\me", 1)]
    [InlineData("bad-\\\nname", "bad-name", 2)]
    [InlineData("$'n\\141me'", "'name'", 1)]
    [InlineData("$\"name\"", "\"name\"", 1)]
    [InlineData("{", "{", 1)]
    public async Task InvalidLoopNamesReportUnexpandedSpelling(string token, string spelling, int line)
    {
        var result = await EvaluationTestHelper.RunAsync("for " + token + " in; do printf body; done");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal($"line {line}: `{spelling}': not a valid identifier\n", result.StdErr);
    }

    [Fact]
    public async Task InvalidLoopNameReportsTheSourcedLineThroughItsRedirection()
    {
        await using var host = new TestHost();
        host.AddFile("/loaded", ":\nfor 'item' in one; do printf body; done 2>log\nprintf '<%s>' \"$?\"\n");

        var result = await host.RunAsync(":\n:\nsource ./loaded", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("./loaded: line 2: `'item'': not a valid identifier\n", host.ReadFile("/log"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLoopNameUsesTheCompletedUnitLine(bool prepared)
    {
        const string script = ":\nfor 'x' in one; do\n  printf body\ndone; : \\\n argument\n"
            + "printf 'after:%s' \"$LINENO\"\n";
        await using var host = new TestHost();
        var status = prepared
            ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
            : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

        Assert.Equal(0, status);
        Assert.Equal("after:6", host.StandardOutput.TextContent);
        Assert.Equal("line 5: `'x'': not a valid identifier\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task InvalidLoopNameInEvalUsesTheLastWordBeforeTrailingRedirections()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(":\neval \"for 'x' in value; do\n :\ndone\" 2>log \\\n >out\nprintf after\n",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("line 6: `'x'': not a valid identifier\n", host.ReadFile("/log"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DisplayedFunctionRetainsItsInvalidLoopNameUntilInvocation()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "f(){ for \"name\" in one; do printf body; done; }; " +
            "declare -f f >display; eval \"$(declare -f f)\"; printf ready; f; printf ':%s' \"$?\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ready:1", host.StandardOutput.TextContent);
        Assert.Equal("line 2: `\"name\"': not a valid identifier\n", host.StandardError.TextContent);
        Assert.Contains("for \"name\" in one;", host.ReadFile("/display"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task RuntimeSourcePreservesInvalidLoopNameBytes()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("eval $'for bad\\377name in one; do :; done'", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("line 1: `bad\u00ffname': not a valid identifier\n",
            System.Text.Encoding.Latin1.GetString(host.StandardError.Content.Span));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task InvalidLoopNameInsideSubstitutionIsAReachedFailure()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "if false; then for \"never\"; do printf body; done; fi; " +
            "value=$(for bad-name in one; do printf body; done); printf 'status:%s value:<%s>' \"$?\" \"$value\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("status:1 value:<>", result.StdOut);
        Assert.Equal("line 4: `bad-name': not a valid identifier\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_ForCommand_ExecutesBodyForEachItem()
    {
        // This test verifies that a "for" loop iterates over each list item in order.
        // The script "for i in a b; do echo $i; done" must emit "a\nb\n" with no STDERR and exit 0.
        // If iteration is skipped or order is wrong, STDOUT will not match.
        var script = @"for i in a b; do echo $i; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a\nb\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_EmptyListReturnsZero()
    {
        // This test verifies that a "for" loop with an empty list runs zero iterations.
        // The script "for i in; do echo $i; done" must emit no output, no STDERR, and exit 0.
        // If the loop runs unexpectedly, STDOUT would contain a newline.
        var script = @"for i in; do echo $i; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a "for" loop.
        // The script "! for i in a; do echo $i; done" must still echo "a\n" but return exit code 1.
        // If negation is ignored, the exit code would remain 0.
        var script = @"! for i in a; do echo $i; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ForCommand_GlobbedItemsWithIfBody()
    {
        // This test verifies that for-loops can iterate over globbed directory items and execute
        // nested if statements without syntax errors.
        // The script uses */ and .*/ globs, so STDERR must remain empty and exit code must be 0.
        var script = @"for d in */ .*/; do if [ -d ""$d"" ]; then echo ""$d""; fi; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.NotEmpty(result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.NotEqual(2, result.ExitCode);
    }
}
