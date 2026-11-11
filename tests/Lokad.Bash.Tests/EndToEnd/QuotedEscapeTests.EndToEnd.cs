using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class QuotedEscapeTestsEndToEnd
{
    [Theory]
    [InlineData("value=outer; printf '<%s>\\n' \"\\$value\"", "<$value>\n", "", 0)]
    [InlineData("value=outer; printf '<%s>\\n' \"\\${value}\"", "<${value}>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"\\$(printf effect >&2)\"", "<$(printf effect >&2)>\n", "", 0)]
    [InlineData("value=0; printf '<%s>\\n' \"\\$((value++))\" \"$value\"", "<$((value++))>\n<0>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"\\`printf effect\\`\"", "<`printf effect`>\n", "", 0)]
    [InlineData("value=outer; printf '<%s>\\n' \\$value", "<$value>\n", "", 0)]
    [InlineData("value=outer; printf '<%s>\\n' \"\\\\$value\"", "<\\outer>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"\\q\"", "<\\q>\n", "", 0)]
    [InlineData("value=outer; printf '<%s>\\n' '$value'", "<$value>\n", "", 0)]
    [InlineData("value=outer; printf '<%s>\\n' 'literal'\"\\$value\"", "<literal$value>\n", "", 0)]
    [InlineData("MARKER=outer; eval \"sh -c 'printf \\\"<%s>\\\\n\\\" \\\"\\${MARKER-unset}\\\"'\"", "<unset>\n", "", 0)]
    [InlineData("value=ok; printf '<%s>\\n' \"\\\"$value\"", "<\"ok>\n", "", 0)]
    [InlineData("unset missing; printf '<%s>\\n' \"\" \"\\$\" \"$missing\" \"\\$missing$missing\"", "<>\n<$>\n<>\n<$missing>\n", "", 0)]
    [InlineData("value=bad; printf '<%s>\\n' \"\\$va\\\nlue\"", "<$value>\n", "", 0)]
    [InlineData("value=ok; printf '<%s>\\n' \"\\\\\\$value\" \"\\\\\\\\$value\"", "<\\$value>\n<\\\\ok>\n", "", 0)]
    [InlineData("value=0; printf '<%s>\\n' \"\\$((value++)):$((value++)):\\$(printf skipped >&2):$(printf done; printf once >&2)\" \"$value\"", "<$((value++)):0:$(printf skipped >&2):done>\n<1>\n", "once", 0)]
    [InlineData("unset missing; value=bad; printf '<%s>\\n' \"${missing:-\\$value}\" \"${missing:-\\$(printf effect >&2)}\"", "<$value>\n<$(printf effect >&2)>\n", "", 0)]
    [InlineData("value=bad; values=(\"\\$value\" \"$value\" \"\\$(printf effect >&2)\" \"\"); printf '<%s>\\n' \"${values[@]}\"", "<$value>\n<bad>\n<$(printf effect >&2)>\n<>\n", "", 0)]
    [InlineData("command=printf; value=bad; \"$command\" '<%s>\\n' \"\\$value\"", "<$value>\n", "", 0)]
    [InlineData("printf x > star; printf '<%s>\\n' \"\\$*\" \"\\$value*\"", "<$*>\n<$value*>\n", "", 0)]
    // LF is the only line terminator; CR remains literal input data.
    [InlineData("value=bad; printf '<%s>\\n' \"\\$value\r\\q\"", "<$value\r\\q>\n", "", 0)]
    public async Task QuotedEscapes_PreserveLiteralsAndIntentionalExpansion(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }
}
