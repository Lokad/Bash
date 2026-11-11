using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationBraceExpansionTests
{
    [Theory]
    [InlineData("{a,{b,c}}{1..2}", "a1 a2 b1 b2 c1 c2")]
    [InlineData("{a,b}\"x\"{1..2}", "ax1 ax2 bx1 bx2")]
    [InlineData("'{1..1000000000}'", "{1..1000000000}")]
    [InlineData("{01..03}", "01 02 03")]
    [InlineData("{-02..01}", "-02 -01 00 01")]
    [InlineData("{2147483646..2147483647}", "2147483646 2147483647")]
    [InlineData("{-2147483647..-2147483648}", "-2147483647 -2147483648")]
    [InlineData("{2147483647..2147483647}", "2147483647")]
    [InlineData("{-2147483648..-2147483648}", "-2147483648")]
    [InlineData("{-2147483648..2147483647..2147483647}", "-2147483648 -1 2147483646")]
    [InlineData("{2147483647..-2147483648..-2147483648}", "2147483647 -1")]
    [InlineData("{a..z..2147483647}", "a")]
    [InlineData("{z..a..-2147483648}", "z")]
    public async Task ExpansionPreservesOrderQuotingAndIntegerEndpoints(string word, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Equal(expected + "\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task NumericPaddingCanExceedTheFormerStackBuffer()
    {
        var zeros = new string('0', 40);
        var result = await EvaluationTestHelper.RunAsync("echo {" + zeros + "1..2}");

        Assert.Equal(zeros + "1 " + zeros + "2\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("{1..1000000000}")]
    [InlineData("{-2147483648..2147483647}")]
    [InlineData("{1..1000}{1..1000}")]
    [InlineData("{1..300}\"\"{1..300}")]
    [InlineData("{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}{,}")]
    public async Task ExcessiveExpansionAbortsBeforeExecutingTheCommand(string word)
    {
        var result = await EvaluationTestHelper.RunAsync("echo " + word + "; echo should-not-run");

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData(":")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("hash")]
    [InlineData("mapfile names")]
    [InlineData("readarray names")]
    [InlineData("exit 0")]
    [InlineData("shift 0")]
    public async Task IgnoredOperandsStillRespectExpansionLimits(string command)
    {
        var result = await EvaluationTestHelper.RunAsync(command + " {1..1000000000}; echo should-not-run");

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("{1..2048}TEXT")]
    [InlineData("'TEXT'{1..2048}")]
    [InlineData("{1..2048}'TEXT'")]
    [InlineData("{TEXT1..2048}")]
    public async Task CharacterBudgetIncludesLiteralsQuotedPartsAndNumericPadding(string word)
    {
        // The range counts fit the word budget; repeated literal text must hit the character budget.
        var text = new string('0', 8192);
        var result = await EvaluationTestHelper.RunAsync("echo " + word.Replace("TEXT", text));

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetIsSharedAcrossWordsAndCommands(bool separateCommands)
    {
        // Each range is small enough on its own; together they exceed the evaluator's allowance.
        var script = separateCommands
            ? string.Concat(Enumerable.Repeat("echo {1..1000} >/dev/null; ", 20))
            : "echo " + string.Join(" ", Enumerable.Repeat("{1..1000}", 20));
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("echo {1..1000} | cat >/dev/null")]
    [InlineData("echo $(echo {1..1000}) >/dev/null")]
    [InlineData("echo $(echo {1..1000} | cat) >/dev/null")]
    public async Task NestedEvaluationsCannotResetTheBudget(string command)
    {
        var script = string.Concat(Enumerable.Repeat(command + "; ", 20));
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        // Splitting the substituted output can exhaust the word allowance before the brace allowance.
        Assert.Contains("expansion exceeds", result.StdErr);
    }

    [Fact]
    public async Task ConcurrentPipelineStagesShareTheBudget()
    {
        var script = "{ " + string.Join(" | ", Enumerable.Repeat("echo {1..1000}", 20)) + "; } >/dev/null";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("{ echo {1..1000000000}; } 2>/errors")]
    [InlineData("(echo {1..1000000000}) 2>/errors")]
    [InlineData("{ read {1..1000000000}; } 2>/errors")]
    [InlineData("echo $({ echo {1..1000000000}; } 2>/errors) >/dev/null")]
    [InlineData("{ echo {1..1000000000}; } 2>/errors | cat")]
    public async Task LimitDiagnosticUsesTheEnclosingErrorRedirection(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        var errors = await EndToEndHarness.ReadFileAsync(result.Host, "/errors");

        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Contains("brace expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task LimitDiagnosticHonorsMergedOutput()
    {
        var result = await EvaluationTestHelper.RunAsync("{ echo {1..1000000000}; } 2>&1");

        Assert.Contains("brace expansion exceeds", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("echo {1..1000000000} 2>/errors")]
    [InlineData("read {1..1000000000} 2>/errors")]
    [InlineData("echo $(echo {1..1000000000} 2>/errors) >/dev/null")]
    [InlineData("echo {1..1000000000} 2>/errors | cat")]
    [InlineData("echo {1..1000000000} 2>&1")]
    public async Task OperandLimitDoesNotOpenTheCommandsRedirections(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.Single(result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/errors"));
    }

    [Theory]
    [InlineData("echo 2>/errors >{1..1000000000}")]
    [InlineData("echo 2>/errors <{1..1000000000}")]
    [InlineData("echo 2>/errors <<<{1..1000000000}")]
    [InlineData("echo 2>/errors 1>&{1..1000000000}")]
    public async Task LimitDuringRedirectionSetupUsesEarlierRedirections(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        var errors = await EndToEndHarness.ReadFileAsync(result.Host, "/errors");

        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Contains("brace expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task LimitDuringRedirectionSetupHonorsEarlierDescriptorDuplication()
    {
        var result = await EvaluationTestHelper.RunAsync("echo 2>&1 >{1..1000000000}");

        Assert.Contains("brace expansion exceeds", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task LimitDuringRedirectionSetupDoesNotApplyLaterRedirections()
    {
        var result = await EvaluationTestHelper.RunAsync("echo >{1..1000000000} 2>/errors");

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds", result.StdErr);
        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/errors"));
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task SubstitutionDuringRedirectionSetupInheritsEarlierRedirections()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "echo 2>/errors >$(echo {1..1000000000}); echo after >&2");
        var errors = await EndToEndHarness.ReadFileAsync(result.Host, "/errors");

        Assert.Empty(result.StdOut);
        Assert.Equal("after\n", result.StdErr);
        Assert.Contains("brace expansion exceeds", errors);
        Assert.Single(errors.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RepeatedSingletonExpansionCannotExhaustTheStack()
    {
        var word = string.Concat(Enumerable.Repeat("{1..1}", 300));
        var result = await EvaluationTestHelper.RunAsync("echo " + word);

        Assert.Empty(result.StdOut);
        Assert.Contains("brace expansion exceeds the 256-step nesting limit", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void ExpansionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BraceExpansion.Expand(ShellSource.FromText("{1..1000}"), new ExpansionBudget().Braces, cancellation.Token));
    }
}
