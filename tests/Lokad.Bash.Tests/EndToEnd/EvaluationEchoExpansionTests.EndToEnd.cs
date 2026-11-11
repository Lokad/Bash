using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationEchoExpansionTestsEndToEnd
{
    [Theory]
    [InlineData("unset HOME; printf '<%s>\\n' ~", "<~>\n")]
    [InlineData("HOME=; printf '<%s>\\n' ~", "<>\n")]
    [InlineData("unset PWD; printf '<%s>\\n' ~+", "</>\n")]
    [InlineData("PWD=; printf '<%s>\\n' ~+", "<>\n")]
    [InlineData("PWD=/logical; printf '<%s>\\n' ~+", "</logical>\n")]
    [InlineData("unset OLDPWD; printf '<%s>\\n' ~-", "<~->\n")]
    [InlineData("OLDPWD=; printf '<%s>\\n' ~-", "<>\n")]
    [InlineData("OLDPWD=/previous; printf '<%s>\\n' ~-", "</previous>\n")]
    [InlineData("HOME=/home; printf '<%s>\\n' prefix~", "<prefix~>\n")]
    public async Task Evaluation_LeadingTilde_UsesDocumentedVariableAvailability(string script, string expected)
    {
        // The controlled host has no account lookup; missing HOME stays literal.
        // Missing PWD uses the evaluator's directory, not Bash's literal ~+.
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_EmitsWriteOperation()
    {
        // This test verifies that echo writes its arguments to STDOUT with a trailing newline.
        // The script "echo a b" must output "a b\n" with no STDERR and exit code 0.
        // If echo skips arguments or the newline, STDOUT will not match.
        var script = @"echo a b";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AllowsNoArguments()
    {
        // This test verifies that echo with no arguments prints an empty line.
        // The script "echo" must output "\n" with no STDERR and exit code 0.
        // If echo refuses empty arguments, STDOUT would be empty.
        var script = @"echo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsLastExitStatus()
    {
        // This test verifies that "$?" expands to the last exit code.
        // The script "false; echo $?" must output "1\n", with no STDERR and exit code 0.
        // If the status is not preserved, the output would be "0\n" or empty.
        var script = @"false; echo $?";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsPositionalParameters()
    {
        // This test verifies that positional parameters expand correctly.
        // The script sets parameters and echoes "$1 $#" which must output "one 2\n".
        // If parameters are not set, the output would be empty or the count wrong.
        var script = @"set one two; echo $1 $#";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one 2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariables()
    {
        // This test verifies that named variables expand in ${...} form.
        // The script assigns FOO=bar and echoes ${FOO}, which must output "bar\n".
        // If variable expansion fails, STDOUT would be empty.
        var script = @"FOO=bar; echo ${FOO}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_SingleQuotedIsLiteral()
    {
        // This test verifies that single-quoted text is not expanded.
        // The script "echo '$1'" must output "$1\n".
        // If expansion occurs, STDOUT would be empty or contain a parameter value.
        var script = @"echo '$1'";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("$1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_SingleQuotedGlobIsLiteral()
    {
        // This test verifies that a single-quoted glob is treated literally.
        // The script "echo '*.txt'" must output "*.txt\n" regardless of matching files.
        // If glob expansion occurs, the output would list file names instead.
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b")
        ]);
        var script = @"cd /; echo '*.txt'";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("*.txt\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_DoubleQuotedGlobIsLiteral()
    {
        // This test verifies that a double-quoted glob is treated literally.
        // The script "echo \"*.txt\"" must output "*.txt\n" regardless of matching files.
        // If glob expansion occurs, the output would list file names instead.
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b")
        ]);
        var script = @"cd /; echo ""*.txt""";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("*.txt\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_MixedQuotedGlobIsLiteral()
    {
        // This test verifies that partially quoted globs are not expanded.
        // The script "echo a'*'.txt" must output "a*.txt\n".
        // If glob expansion occurs, STDOUT would contain matching file names.
        var script = @"echo a'*'.txt";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a*.txt\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RespectsQuotedBracketSegment()
    {
        // This test verifies that quoted bracket segments suppress glob expansion.
        // With files "ax[bc]", "axb", and "axc", the pattern a*'[bc]' must only match ax[bc].
        // If the bracket segment expands, STDOUT would include "axb" or "axc".
        var tree = EndToEndHarness.BuildTree([
            ("/ax[bc]", "x"),
            ("/axb", "b"),
            ("/axc", "c")
        ]);
        var script = @"cd /; echo a*'[bc]'";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        Assert.Equal("ax[bc]\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsArithmeticLiteral()
    {
        // This test verifies that arithmetic expansion evaluates literals.
        // The script "echo $((42))" must output "42\n".
        // If arithmetic expansion fails, STDOUT would be empty or contain the literal text.
        var script = @"echo $((42))";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("42\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsArithmeticVariable()
    {
        // This test verifies that arithmetic expansion resolves variables.
        // The script sets FOO=7 and echoes $(( ${FOO} )), which must output "7\n".
        // If variable expansion fails, the output would be wrong.
        var script = @"FOO=7; echo $(( ${FOO} ))";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("7\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNestedArithmetic()
    {
        // This test verifies that nested arithmetic expansion is supported.
        // The script "echo $(( $((3)) ))" must output "3\n".
        // If nested expansion fails, STDOUT would be empty or incorrect.
        var script = @"echo $(( $((3)) ))";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("3\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsHomeTilde()
    {
        // This test verifies that "~" expands to HOME.
        // The script sets HOME=/home and echoes "~", which must output "/home\n".
        // If tilde expansion is disabled, the output would be "~\n".
        var script = @"HOME=/home; echo ~";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/home\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsHomeTildeWithPath()
    {
        // This test verifies that "~/" expands with a trailing path segment.
        // The script sets HOME=/home and echoes "~/src", which must output "/home/src\n".
        // If tilde expansion fails, the output would be "~/src\n".
        var script = @"HOME=/home; echo ~/src";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/home/src\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsOldPwdTilde()
    {
        // This test verifies that "~- " expands to OLDPWD.
        // The script sets OLDPWD=/prev and echoes "~-", which must output "/prev\n".
        // If OLDPWD expansion fails, the output would be "~-\\n".
        var script = @"OLDPWD=/prev; echo ~-";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/prev\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_TildeQuotedIsLiteral()
    {
        // This test verifies that quoted "~" is treated literally.
        // The script "echo \"~\"" must output "~\n".
        // If tilde expansion occurs inside quotes, the output would be "/home\n".
        var script = @"echo ""~""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("~\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariableWithoutBraces()
    {
        // This test verifies that $FOO expands without braces.
        // The script assigns FOO=bar and echoes $FOO, which must output "bar\n".
        // If expansion fails, STDOUT would be empty.
        var script = @"FOO=bar; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsNamedVariableInDoubleQuotes()
    {
        // This test verifies that variables expand inside double quotes.
        // The script assigns FOO=bar and echoes "$FOO", which must output "bar\n".
        // If double-quote expansion is disabled, the output would be "$FOO\n".
        var script = @"FOO=bar; echo ""$FOO""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsBraceList()
    {
        // This test verifies that brace list expansion enumerates alternatives.
        // The script "echo a{b,c}d" must output "abd acd\n".
        // If brace expansion is disabled, the output would contain the braces.
        var script = @"echo a{b,c}d";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("abd acd\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsBraceSequence()
    {
        // This test verifies that brace sequences expand to each element in order.
        // The script "echo {1..3}" must output "1 2 3\n".
        // If sequence expansion fails, the output would contain braces.
        var script = @"echo {1..3}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1 2 3\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_LeavesQuotedBracesLiteral()
    {
        // This test verifies that quoted braces are not expanded.
        // The script "echo \"{a,b}\"" must output "{a,b}\n".
        // If brace expansion runs inside quotes, the output would be "a b\n".
        var script = @"echo ""{a,b}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("{a,b}\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
