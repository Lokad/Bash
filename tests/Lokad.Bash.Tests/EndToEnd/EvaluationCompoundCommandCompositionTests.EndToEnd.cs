using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationCompoundCommandCompositionTestsEndToEnd
{
    [Theory]
    [InlineData("!")]
    [InlineData("!\nfalse")]
    [InlineData("! # independent command follows\nfalse")]
    [InlineData("!; false")]
    public async Task Evaluation_StandaloneNegation_DoesNotNegateNextCommand(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Theory]
    [InlineData("for ((i=0;i<1;i++)) do printf ok; done")]
    [InlineData("for ((i=0;i<1;i++)); do printf ok; done")]
    [InlineData("for ((i=0;i<1;i++))\n\ndo printf ok; done")]
    public async Task Evaluation_ArithmeticFor_AllowsOptionalSeparatorBeforeDo(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_WhileRead_WithNestedIfAndInputRedirection()
    {
        var tree = EndToEndHarness.BuildTree([("/input", "alpha\nskip\nomega\n")]);
        var script = """
            while IFS= read -r value; do
              if [ "$value" = skip ]; then
                continue
              fi
              printf '<%s>\n' "$value"
            done < /input
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<omega>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_GroupContainingLoop_AcceptsTrailingRedirection()
    {
        var script = """
            {
              for value in alpha beta; do
                printf '<%s>\n' "$value"
              done
            } > /out
            cat /out
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<beta>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_ArrayAssignment_FeedsQuotedForLoopExpansion()
    {
        var script = """
            files=(alpha 'two words')
            for file in "${files[@]}"; do
              printf '<%s>\n' "$file"
            done
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<two words>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_ForLoop_AcceptsTrailingRedirection()
    {
        var script = """
            for value in alpha beta; do
              printf '<%s>\n' "$value"
            done > /out
            cat /out
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<beta>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_WhilePipelineStage_AcceptsTrailingRedirection()
    {
        var script = """
            printf 'alpha\nbeta\n' |
              while IFS= read -r value; do
                printf '<%s>\n' "$value"
              done > /out
            cat /out
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<beta>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }


    [Fact]
    public async Task Evaluation_PipelineWhileRead_PreservesCompletePathnameLines()
    {
        var script = """
            printf '%s\n' \
              'examples/summary report.sh' \
              'dir/[one] (two) & été.sh' |
              while IFS= read -r path; do
                printf '<%s>\n' "$path"
              done
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "<examples/summary report.sh>\n<dir/[one] (two) & été.sh>\n",
            result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_BackslashContinuedArgumentsAndForWordList()
    {
        var script = """
            for value in \
              alpha \
              'two words'
            do
              printf '<%s>\n' \
                "$value"
            done
            """.Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<alpha>\n<two words>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

    [Theory]
    [InlineData("[[\\\n value = value ]] && echo matched")]
    [InlineData("[\\\n[ value = value ]] && echo matched")]
    [InlineData("[[ value = value ]\\\n] && echo matched")]
    [InlineData("[\\\n[ value = value ]\\\n] && echo matched")]
    [InlineData("[[ value = value \\\n ]] && echo matched")]
    public async Task Evaluation_BackslashContinuedConditionalDelimiter(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("matched\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }

}
