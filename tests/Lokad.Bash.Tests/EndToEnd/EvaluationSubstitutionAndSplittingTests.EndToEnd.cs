using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationSubstitutionAndSplittingTestsEndToEnd
{
    [Theory]
    [InlineData("printf '%s' \"${missing:-$(cat <<'EOF'\na\\\nb\nEOF\n)}\"", "a\\\nb")]
    [InlineData("printf '%s' \"$\\\n((1+\\\n1))\"", "2")]
    [InlineData("value=2; printf '%s' \"$(( $\\\n{value} + 1 ))\"", "3")]
    [InlineData("printf '<%s>' a\r\nprintf end", "<a\r>end")]
    public async Task Evaluation_LogicalContinuationsPreserveNestedAndCarriageReturnData(
        string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }


    [Fact]
    public async Task Evaluation_CommandSubstitution_AcceptsContinuedOpener()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '%s' $\\\n(printf ok)");

        Assert.Equal("ok", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterExpansion_AcceptsContinuedOpener()
    {
        var result = await EvaluationTestHelper.RunAsync("value=ok; printf '%s' $\\\n{value}");

        Assert.Equal("ok", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("printf '%s' $\\\n((1+1))")]
    [InlineData("printf '%s' $(\\\n(1+1))")]
    [InlineData("printf '%s' $((1+\\\n1))")]
    [InlineData("printf '%s' $((1+1)\\\n)")]
    public async Task Evaluation_ArithmeticExpansion_AcceptsLogicalContinuations(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnquotedCarriageReturn_RemainsWordContent()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' a\rb");

        Assert.Equal("<a\rb>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_UsesCapturedOutput()
    {
        // This test verifies that command substitution captures STDOUT and inserts it into the command line.
        // The script "echo $(echo hi)" must output "hi\n".
        // If substitution is skipped, STDOUT would be empty.
        var script = @"echo $(echo hi)";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_StripsTrailingNewlines()
    {
        // This test verifies that command substitution strips trailing newlines.
        // The script echoes the result of printf with two trailing newlines; STDOUT must be "hi\n".
        // If newlines are not stripped, the output would contain extra blank lines.
        var script = @"echo $(printf 'hi\n\n')";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SplitsUnquotedOutput()
    {
        // This test verifies that unquoted command substitution is subject to word splitting.
        // The script prints "a b" via substitution and passes it to printf, which must output "a|b".
        // If splitting does not occur, the output would be "a b|".
        var script = @"printf '%s|%s' $(printf 'a b')";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_SkipsSplitInDoubleQuotes()
    {
        // This test verifies that quoted command substitution suppresses word splitting.
        // The script passes "$(printf 'a b')" as a single argument to printf and "tail" as the second.
        // The output must be "a b|tail".
        // If splitting occurs, the first two words would be "a" and "b".
        var script = @"printf '%s|%s' ""$(printf 'a b')"" tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b|tail", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("empty=; for x in $empty y; do printf '[%s]\\n' \"$x\"; done", "[y]\n")]
    [InlineData("for x in $(true) y; do printf '[%s]\\n' \"$x\"; done", "[y]\n")]
    [InlineData("empty=; for x in \"$empty\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("for x in \"\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("for x in '' y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    [InlineData("empty=; for x in $empty\"\" y; do printf '[%s]\\n' \"$x\"; done", "[]\n[y]\n")]
    public async Task Evaluation_WordSplitting_DistinguishesImplicitAndExplicitEmptyFields(
        string script,
        string expectedStdOut)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedStdOut, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset missing; sh -c 'echo \"$#\"' runner $missing", "0\n")]
    [InlineData("unset missing; sh -c 'echo \"$#\"' runner \"$missing\"", "1\n")]
    [InlineData("sh -c 'echo \"$#\"' runner \"\"", "1\n")]
    public async Task Evaluation_CommandArguments_DistinguishImplicitAndExplicitEmptyFields(
        string script,
        string expectedStdOut)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedStdOut, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsFunctionDefinitionParens()
    {
        var script = @"echo ""$(f() { echo hi; }; f)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsNestedSubshellParens()
    {
        var script = @"echo ""$( (echo hi) )""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsNestedSubstitutionWithSubshellParens()
    {
        var script = @"echo ""$(echo $( (echo hi) ))""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsLeadingTerminatorBeforeNestedSubshellParens()
    {
        var script = "echo \"$(\n(echo hi)\n)\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_AllowsCasePatternParens()
    {
        var script = @"echo ""$(case x in x) echo hi ;; esac)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitution_DoesNotLeakAssignments()
    {
        var script = @"x=outer; echo ""$(x=inner)""; echo ""$x""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\nouter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_UsesCapturedOutput()
    {
        var script = @"echo `echo hi`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_SplitsUnquotedOutput()
    {
        var script = @"printf '%s|%s' `printf 'a b'`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_SkipsSplitInDoubleQuotes()
    {
        var script = @"printf '%s|%s' ""`printf 'a b'`"" tail";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b|tail", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EscapedLegacyBackticks_AreLiteral()
    {
        var script = @"echo \`echo hi\`";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("`echo hi`\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitution_ExpandsInsideBracedParameterDefault()
    {
        var script = @"echo ${missing:-`printf hi`}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideBracedParameterDefault_AllowsClosingBraceInPayload()
    {
        var script = @"echo ${missing:-$(printf '}')}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitutionInsideBracedParameterDefault_AllowsClosingBraceInPayload()
    {
        var script = @"echo ${missing:-`printf '}'`}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideSingleQuotedBracedParameterDefault_IsLiteral()
    {
        var script = @"echo ${missing:-'$(printf hi)'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("$(printf hi)\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LegacyBacktickCommandSubstitutionInsideSingleQuotedBracedParameterDefault_IsLiteral()
    {
        var script = @"echo ${missing:-'`printf hi`'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("`printf hi`\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionInsideDoubleQuotedBracedParameterDefault_ExpandsWithoutQuoteLiterals()
    {
        var script = @"echo ${missing:-""$(printf hi)""}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SingleQuotedBracedParameterDefault_AllowsClosingBraceLiteral()
    {
        var script = @"echo ${missing:-'}'}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_DoubleQuotedBracedParameterDefault_AllowsClosingBraceLiteral()
    {
        var script = @"echo ${missing:-""}""}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesDefaultIfs()
    {
        // This test verifies that default IFS splits on whitespace.
        // The script expands "a b" into two arguments and prints them as "a|b".
        // If default splitting is disabled, the output would be "a b|".
        var script = @"var=""a b""; printf '%s|%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_UsesCustomIfs()
    {
        // This test verifies that IFS controls splitting characters.
        // The script sets IFS=, and splits "a,b,c" into three arguments, printing "a|b|c".
        // If IFS is ignored, the output would be "a,b,c||".
        var script = @"IFS=,; var=""a,b,c""; printf '%s|%s|%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a|b|c", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_PreservesEmptyFieldsForNonWhitespaceIfs()
    {
        // Count arguments: printf's missing-argument padding hides a spurious trailing field.
        var script = @"IFS=,; var=""a,,b,""; set -- $var; printf 'count=%s' ""$#""; for item in ""$@""; do printf ':<%s>' ""$item""; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("count=3:<a>:<>:<b>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_WordSplitting_RespectsEmptyIfs()
    {
        // This test verifies that empty IFS disables splitting.
        // The script sets IFS= and expands "a b" into a single argument, printing "a b".
        // If splitting still happens, the output would be "a|b".
        var script = @"IFS=; var=""a b""; printf '%s' $var";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
