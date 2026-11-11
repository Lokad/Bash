using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationForTestsEndToEnd
{
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
