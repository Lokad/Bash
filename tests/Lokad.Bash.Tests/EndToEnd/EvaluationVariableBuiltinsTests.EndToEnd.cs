using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationVariableBuiltinsTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_UnsetBuiltin_RemovesVariable()
    {
        // This test verifies that "unset" removes an assigned variable.
        // With input "value\n", the script reads name, unsets it, and echoes a default.
        // The output must be "unset\n".
        // If unset fails, the output would be "value\n".
        var script = @"read name; unset name; echo ${name:-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnsetBuiltin_MissingVariableReturnsSuccess()
    {
        // This test verifies that unsetting a missing variable succeeds.
        // The script unsets "missing" and echoes a default, which must output "unset\n" with exit code 0.
        // If unset returns failure for missing variables, the exit code would be non-zero.
        var script = @"unset missing; echo ${missing:-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_HashBuiltin_ReturnsSuccess()
    {
        // This test verifies that "hash" with no arguments succeeds.
        // The script "hash" must emit no output and exit with code 0.
        // If hash requires arguments, the exit code would be 1.
        var script = @"hash";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_RemovesFirstParameter()
    {
        // This test verifies that "shift" removes the first positional parameter.
        // The script sets parameters, shifts once, and echoes $1; STDOUT must be "b\n".
        // If shift fails, the output would be "a\n".
        var script = @"set a b; shift; echo $1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_RemovesExplicitCount()
    {
        // This test verifies that "shift N" removes N positional parameters.
        // The script shifts two parameters and echoes $1; STDOUT must be "c\n".
        // If the count is ignored, the output would be "b\n".
        var script = @"set a b c; shift 2; echo $1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("c\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_TooLargeReturnsFailure()
    {
        // This test verifies that shifting more parameters than available fails.
        // The script sets two parameters and shifts three; it must emit no output and exit with code 1.
        // If the error is ignored, the exit code would be 0.
        var script = @"set a b; shift 3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_InvalidCountReturnsFailure()
    {
        var result = await EvaluationTestHelper.RunAsync("shift nope");

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("shift: nope: numeric argument required\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("shift --", "<0:2><b c><>", "")]
    [InlineData("shift -- +1", "<0:2><b c><>", "")]
    [InlineData("shift $'\\t\\r\\n\\v\\f+1 \\t'", "<0:2><b c><>", "")]
    [InlineData("shift -0", "<0:3><a><b c><>", "")]
    [InlineData("shift 4294967296", "<1:3><a><b c><>", "")]
    [InlineData("shift 9223372036854775807", "<1:3><a><b c><>", "")]
    [InlineData("shift 9223372036854775808", "<1:3><a><b c><>", "shift: 9223372036854775808: numeric argument required\n")]
    [InlineData("shift -9223372036854775808", "<1:3><a><b c><>", "shift: -9223372036854775808: shift count out of range\n")]
    [InlineData("shift -- -1", "<1:3><a><b c><>", "shift: --: shift count out of range\n")]
    [InlineData("shift nope extra", "<1:3><a><b c><>", "shift: nope: numeric argument required\n")]
    [InlineData("unset count; shift $count", "<0:2><b c><>", "")]
    [InlineData("count=; shift \"$count\"", "<1:3><a><b c><>", "shift: : numeric argument required\n")]
    public async Task Evaluation_ShiftBuiltin_ValidatesExpandedCountBeforeChangingFrame(
        string command, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync("set -- a 'b c' ''; " + command
            + "; printf '<%s:%s>' \"$?\" \"$#\"; for arg; do printf '<%s>' \"$arg\"; done");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_SurplusFieldsExitThroughActiveRedirection()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- a b; shift 1 \"$(printf once >fact)\" 2>err; printf unexpected", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("once", host.ReadFile("/fact"));
        Assert.Equal("shift: too many arguments\n", host.ReadFile("/err"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_PreservesInvalidOperandBytes()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("shift $'\\377'", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("73686966743A20FF3A206E756D6572696320617267756D656E742072657175697265640A",
            Convert.ToHexString(host.StandardError.Content.Span));
    }

    [Fact]
    public async Task Evaluation_ShiftBuiltin_HelpPreservesPositionals()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -- a b; shift --help; printf '<%s:%s>' \"$?\" \"$#\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.StartsWith("shift: shift [--] [n]\n", result.StdOut);
        Assert.EndsWith("<2:2>", result.StdOut);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_ReplacesPositionalParameters()
    {
        // This test verifies that "set" replaces positional parameters.
        // The script sets parameters and echoes them; STDOUT must be "a b\n".
        // If set fails, the output would be empty.
        var script = @"set a b; echo $1 $2";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashClearsParameters()
    {
        // This test verifies that "set --" clears positional parameters.
        // The script sets parameters, clears them, and echoes $#; STDOUT must be "0\n".
        // If parameters are not cleared, the output would be non-zero.
        var script = @"set a b; set --; echo $#";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashKeepsLeadingDashArgument()
    {
        // This test verifies that "set --" allows a leading dash argument.
        // The script sets "-- -x" and echoes $1; STDOUT must be "-x\n".
        // If "--" is ignored, the parameter would be treated as an option.
        var script = @"set -- -x; echo $1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("-x\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashDropsUnquotedMissingExpansion()
    {
        var script = @"unset missing; set -- $missing; echo $#";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_DoubleDashPreservesQuotedMissingExpansion()
    {
        var script = @"unset missing; set -- ""$missing""; printf '[%s] %s\n' ""$1"" ""$#""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("[] 1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_SingleDashLeavesParametersUnchanged()
    {
        // This test verifies that "set -" does not alter positional parameters.
        // The script sets parameters, runs "set -", and echoes them; STDOUT must be "a b\n".
        // If parameters are cleared, the output would be empty.
        var script = @"set a b; set -; echo $1 $2";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_SetBuiltin_InvalidOptionReturnsFailure()
    {
        var script = @"set -x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("set: -x: unsupported option\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }
}
