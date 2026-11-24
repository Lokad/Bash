using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class QuotedEscapeTestsEndToEnd
{
    [Theory]
    [InlineData("set -- left 'right split'; printf '<%s>' $\"pre$@post\"", "<preleft><right splitpost>")]
    [InlineData("set -- $\"\"; printf '%s:<%s>' \"$#\" \"$1\"", "1:<>")]
    [InlineData("unset missing; v=1; printf '<%s>' \"${missing:-$\"a $v\"}\"", "<a 1>")]
    [InlineData("case '*' in $\"*\") printf literal;; esac; [[ item == $\"*\" ]] || printf ':quoted'", "literal:quoted")]
    [InlineData("printf '<%s>' $\\\n\"a\\\nb\" \\$\"plain\"", "<ab><$plain>")]
    [InlineData("v=expanded; cat <<$\"END\"\n$v\nEND\ncat <<END\n$\"$v\"\nEND\n", "$v\n$\"expanded\"\n")]
    public async Task LocaleQuotedWordsUseDoubleQuotedExpansion(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task LocaleQuotedFieldsPreserveByteValuesAndFileSelection()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("v=$'\\377'; f='text name'; printf '%s' $\"$v\" >$\"$f\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(new byte[] { 0xff }, host.ReadFileBytes("/text name"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("\"${missing:-CAPTURE}\"", "<reached>", true)]
    [InlineData("\"${present:-CAPTURE}\"", "<given>", false)]
    [InlineData("\"${present:+CAPTURE}\"", "<reached>", true)]
    [InlineData("\"${missing:+CAPTURE}\"", "<>", false)]
    [InlineData("${missing:-CAPTURE}", "<$(printf x >>effect; printf reached)>", false)]
    [InlineData("\"${present/given/CAPTURE}\"", "<$(printf x >>effect; printf reached)>", false)]
    public async Task BracedOperatorQuoteContextControlsWhetherDecodedCaptureIsReached(string form, string expected, bool executes)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("present=given; printf '<%s>' "
            + form.Replace("CAPTURE", "$'$(printf x >>effect; printf reached)'", StringComparison.Ordinal), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        if (executes)
            Assert.Equal("x", host.ReadFile("/effect"));
        else
            Assert.False(host.FileExists("/effect"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("\\x27", "'")]
    [InlineData("\\x22", "\"")]
    public async Task DecodedQuoteCanInvalidateAProtectedDefaultBeforeOperandSelection(string escape, string quote)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("value=given\nprintf '%s' \"${value:-$'a" + escape
            + "b'}\"; printf discarded\nprintf tail", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Equal("line 2: bad substitution: no closing `}' in \"${value:-a" + quote + "b}\"\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task ProjectedQuoteDiagnosticUsesTheFirstReachedBraceExpandedWord()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf '<%s>' p{a,b}\"${missing:-$'a\\x27b'}\"; printf discarded\nprintf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Equal("line 1: bad substitution: no closing `}' in pa\"${missing:-a'b}\"\n", host.StandardError.TextContent);
    }

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
