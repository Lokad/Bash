using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ReturnArgumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosticBudgetFailureIsReportedOnTheActiveErrorDescriptor(bool redirected)
    {
        var redirection = redirected ? " 2>&1" : "";
        var state = new Evaluation.Evaluation(Parser.Parse($"f() {{ return abc{redirection}; }}; f; after=wrong"));
        // The operand fits; copying it into a numeric-argument diagnostic does not.
        state.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 20);
        var host = new TestOperationVisitor([]) { RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);

        await new Engine().RunAsync(state, host);

        Assert.Equal(2, state.LastExitCode);
        Assert.False(state.Variables.ContainsKey("after"));
        var diagnostic = Assert.Single(host.Appends);
        Assert.Equal(redirected ? FileDescriptor.StdOut : FileDescriptor.StdErr, diagnostic.Descriptor);
        Assert.Equal("shell: word expansion exceeds its word or character limit\n"u8.ToArray(), diagnostic.Content);
    }

    [Theory]
    [InlineData("f(){ set -- 5 6; return -1; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:255]", "")]
    [InlineData("f(){ set -- 5 6; return 257; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:1]", "")]
    [InlineData("f(){ set -- 5 6; return -- 5; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:5]", "")]
    [InlineData("f(){ set -- 5 6; return --; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:0]", "")]
    [InlineData("f(){ set -- 5 6; return 1 2; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        1, "", "return: too many arguments\n")]
    [InlineData("f() { return 7 $'\\377'; }; f; printf '%s' \"$?\"",
        1, "", "return: too many arguments\n")]
    [InlineData("f(){ set -- 5 6; return word; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: word: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return ''; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: : numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return -x; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: -x: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return \"$@\"; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        1, "", "return: too many arguments\n")]
    [InlineData("f(){ set -- 5 6; return ' 3 '; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:3]", "")]
    [InlineData("f(){ set -- 5 6; return $'\\t3\\t'; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:3]", "")]
    [InlineData("f(){ set -- 5 6; return $'3\\n'; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: 3\n: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return $'\\r3\\r'; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: \r3\r: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return +7; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:7]", "")]
    [InlineData("f(){ set -- 5 6; return 9223372036854775807; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:255]", "")]
    [InlineData("f(){ set -- 5 6; return 9223372036854775808; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: 9223372036854775808: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return -9223372036854775808; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:0]", "")]
    [InlineData("f(){ set -- 5 6; return 0x10; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: 0x10: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return 3.0; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: 3.0: numeric argument required\n")]
    [InlineData("f(){ set -- 5 6; return -- -- 5; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
        0, "[root:2]", "return: --: numeric argument required\n")]
    [InlineData("set -- 5 6; return ; printf '[continued:%s]' \"$?\"",
        0, "[continued:2]", "return: can only `return' from a function or sourced script\n")]
    [InlineData("set -- 5 6; return word; printf '[continued:%s]' \"$?\"",
        0, "[continued:2]", "return: word: numeric argument required\nreturn: can only `return' from a function or sourced script\n")]
    [InlineData("f(){ return word 2; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "return: word: numeric argument required\n")]
    [InlineData("return word 2; printf '[%s]' \"$?\"",
        0, "[2]", "return: word: numeric argument required\nreturn: can only `return' from a function or sourced script\n")]
    [InlineData("f(){ ! return 7; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[7]", "")]
    [InlineData("f(){ ! { return 7; }; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[7]", "")]
    [InlineData("f(){ ! eval 'return 7'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[7]", "")]
    [InlineData("f(){ ! for i in 1; do return 7; done; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[7]", "")]
    [InlineData("f(){ ! { return 7; }; }; ! f; printf '[%s]' \"$?\"",
        0, "[0]", "")]
    [InlineData("f(){ ! return word; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "return: word: numeric argument required\n")]
    [InlineData("! return 1 2; printf wrong",
        1, "", "return: too many arguments\n")]
    [InlineData("f(){ false; return $(true); printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[0]", "")]
    [InlineData("f(){ set --; false; return \"$@\"; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[1]", "")]
    [InlineData("f(){ value='3 4'; return $value; printf wrong; }; f; printf wrong",
        1, "", "return: too many arguments\n")]
    [InlineData("unset value; f(){ return ${value:=7}; printf wrong; }; f; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[7:7]", "")]
    [InlineData("value=outer; f(){ value=temp return 7; printf wrong; }; f; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[7:outer]", "")]
    [InlineData("f(){ return 1 \"$((x+=1))\" \"$(printf E >&2)\"; printf wrong; }; f; printf wrong",
        1, "", "Ereturn: too many arguments\n")]
    // Keep the platform's current fatal-expansion status (1); Bash -c reports 127 here.
    [InlineData("f(){ value=${missing?bad} return word; printf wrong; }; f; printf wrong",
        1, "", "missing: bad\n")]
    [InlineData("f(){ return word ${missing?bad}; printf wrong; }; f; printf wrong",
        1, "", "missing: bad\n")]
    [InlineData("f(){ return word 2>/dev/null; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "")]
    [InlineData("(return 1 2; printf wrong); printf '[%s]' \"$?\"",
        0, "[1]", "return: too many arguments\n")]
    [InlineData("f(){ value=$(return 1 2; printf wrong); printf '[%s:%s]' \"$value\" \"$?\"; }; f",
        0, "[:1]", "return: too many arguments\n")]
    [InlineData("f(){ return $'3\\0wrong'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[3]", "")]
    [InlineData("f(){ return $'\\n3'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[3]", "")]
    [InlineData("f(){ return $'\\r3'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[3]", "")]
    [InlineData("f(){ return $'\\v3'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[3]", "")]
    [InlineData("f(){ return $'\\f3'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[3]", "")]
    [InlineData("f(){ return $'3\\v'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "return: 3\u000b: numeric argument required\n")]
    [InlineData("f(){ return $'3\\f'; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "return: 3\f: numeric argument required\n")]
    [InlineData("f(){ return -- --help; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[2]", "return: --help: numeric argument required\n")]
    [InlineData("f(){ false; value=$(true) return; printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[0]", "")]
    [InlineData("f(){ false; return 2>$(printf /dev/null); printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[0]", "")]
    [InlineData("f(){ false; return $(false); printf wrong; }; f; printf '[%s]' \"$?\"",
        0, "[1]", "")]
    public async Task ReturnValidatesExpandedOperandsAndControlFlow(
        string script, int expectedStatus, string expectedOutput, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("word", 0, "[root:2]", "return: word: numeric argument required\n")]
    [InlineData("1 2", 1, "", "return: too many arguments\n")]
    public async Task ReturnOperandErrorsRespectTheSourceBoundary(
        string operand, int expectedStatus, string expectedOutput, string expectedError)
    {
        var tree = EndToEndHarness.BuildTree([("/script", $"return {operand}; printf wrong")]);
        var result = await EndToEndHarness.RunScriptAsync("source /script; printf '[root:%s]' \"$?\"", tree);

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task NumericDiagnosticPreservesInvalidUtf8AndReturnsFromTheFunction()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f(){ return $'\\377'; printf wrong; }; f; printf '[%s]' \"$?\"", EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[2]", result.StdOut);
        Assert.Equal(Convert.FromHexString("72657475726E3A20FF3A206E756D6572696320617267756D656E742072657175697265640A"),
            result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task HelpReportsUsageWithoutReturningFromTheFunction()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "f(){ return --help; printf '[continued:%s]' \"$?\"; }; f; printf '[root:%s]' \"$?\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("return: return [n]\n", result.StdOut);
        Assert.EndsWith("[continued:2][root:0]", result.StdOut);
        Assert.Empty(result.StdErr);
    }
}
