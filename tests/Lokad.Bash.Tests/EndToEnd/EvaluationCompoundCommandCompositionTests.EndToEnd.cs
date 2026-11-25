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

    [Fact]
    public async Task MissingPipelineCommandInSkippedSubstitutionStopsItsCommandUnit()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf earlier\ntrue || value=$(time)\nprintf later\n", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("earlier", host.StandardOutput.TextContent);
        Assert.Equal("syntax error near unexpected token `)'\n`true || value=$(time)'\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task ContinuedPipelineReportsTheReachedCloserInASourcedCapture()
    {
        await using var host = new TestHost();
        host.AddFile("/fixture", "printf entry\nvalue=$(printf hidden |&\n)\nprintf inner\n");

        var result = await host.RunAsync("source ./fixture\nprintf outer\n", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("entry", host.StandardOutput.TextContent);
        Assert.Equal("./fixture: line 3: syntax error near unexpected token `)'\n./fixture: line 3: `)'\n",
            host.StandardError.TextContent);
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

    [Theory]
    [InlineData("if (true)then (printf hit)fi", "hit", 0)]
    [InlineData("if [[ x ]] then ((0)) fi", "", 1)]
    [InlineData("if true | (true) then printf hit; fi", "hit", 0)]
    [InlineData("if false || { true; } then { printf hit; } fi", "hit", 0)]
    [InlineData("TIMEFORMAT=; if time { true; } then printf hit; fi", "hit", 0)]
    [InlineData("if f() { (printf hit) } then f; fi", "hit", 0)]
    [InlineData("i=0; while ((i<1)) do { printf '%s' \"$i\"; i=1; } done", "0", 0)]
    [InlineData("i=0; until ((i)) do { printf '%s' \"$i\"; i=1; } done", "0", 0)]
    [InlineData("for i in a b; do (printf '%s' \"$i\") done", "ab", 0)]
    [InlineData("for ((i=0;i<2;++i)) do (printf '%s' \"$i\") done", "01", 0)]
    [InlineData("printf '<%s>' \"$(if (false) then (printf no) elif (false) then (printf no) else (printf hit) fi)\"", "<hit>", 0)]
    public async Task CompoundCloserProvidesTheFollowingKeywordBoundary(string script, string expected, int exitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("", 2, "before\n")]
    [InlineData(";", 0, "before\nhit")]
    [InlineData(";;", 0, "before\nhit")]
    public async Task FinalCaseArmChecksTheBoundaryAfterARedirection(string separator, int exitCode, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf 'before\\n'\ncase x in x) (printf hit) >out"
            + separator + " esac; cat out", CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        var createdFile = await EndToEndHarness.FileExistsAsync(host, "/out");
        if (exitCode == 0)
        {
            Assert.True(createdFile);
            Assert.Equal("hit", await EndToEndHarness.ReadFileAsync(host, "/out"));
            Assert.Empty(host.StandardError.TextContent);
        }
        else
        {
            Assert.False(createdFile);
            Assert.Equal("syntax error near unexpected token `esac'\n`case x in x) (printf hit) >out esac; cat out'\n",
                host.StandardError.TextContent);
        }
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
