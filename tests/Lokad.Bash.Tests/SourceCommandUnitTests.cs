using Lokad.Bash.Files;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class SourceCommandUnitTests
{
    [Theory]
    [InlineData("value=$((1/0)); printf wrong\nprintf 'next:%s' \"$?\"\n", "next:1[root:0]", false)]
    [InlineData("f(){ value=$((1/0)); printf wrong; }\nf\nprintf 'next:%s' \"$?\"\n", "next:1[root:0]", false)]
    [InlineData("value=$((1/0))\nreturn\nprintf wrong\n", "[root:1]", false)]
    [InlineData("value=$((1/0))\nreturn 7\nprintf wrong\n", "[root:7]", false)]
    [InlineData("value=$((1/0))\n", "[root:1]", false)]
    [InlineData("value=$((1/0))\n)\n", "[root:2]", true)]
    public async Task RecoverableExpansionFailureEndsOnlyTheCurrentUnit(string body, string expected, bool hasSyntaxError)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "source /source; printf '[root:%s]' \"$?\"", EndToEndHarness.BuildTree([("/source", body)]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.StartsWith("1/0: division by 0\n", result.StdErr);
        Assert.Equal(hasSyntaxError, result.StdErr.Contains("syntax error:"));
    }

    [Theory]
    [InlineData("exit 7\nprintf wrong\n", 7, "")]
    [InlineData("value=${missing?bad}\nprintf wrong\n", 1, "missing: bad\n")]
    public async Task UnrecoverableFlowStillStopsTheSource(string body, int expectedStatus, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "source /source; printf wrong", EndToEndHarness.BuildTree([("/source", body)]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("value=changed\n(\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("value=changed; (\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:outer]", true)]
    [InlineData("value=first\nvalue=second; )\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:first]", true)]
    [InlineData("value=changed;\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("value=changed\n'unterminated",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("value=changed; 'unterminated",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:outer]", true)]
    [InlineData("value=changed\nreturn 7\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[7:changed]", false)]
    [InlineData("value=changed\nreturn 7; )\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("return 7\n'unterminated",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[7:outer]", false)]
    [InlineData("printf A\nexit 7\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        7, "A", false)]
    [InlineData("exit 7; )\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:outer]", true)]
    [InlineData("{\nvalue=changed\nprintf A\n}\nprintf B\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "AB[0:changed]", false)]
    [InlineData("value=first\n{\nvalue=second\n)\n}\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:first]", true)]
    [InlineData("{\nvalue=changed\nprintf A\n}\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "A[2:changed]", true)]
    [InlineData("if true; then\nvalue=changed\nprintf A\nfi\nprintf B\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "AB[0:changed]", false)]
    [InlineData("for i in 1 2; do\nprintf '%s' \"$i\"\ndone\nvalue=changed\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "12[0:changed]", false)]
    [InlineData("f(){\nvalue=changed\nprintf A\n}\nf\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "A[0:changed]", false)]
    [InlineData("f(){ printf A; }\nvalue=changed\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("value=cha\\\nnged\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("false ||\nvalue=changed\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("printf A |\ncat\nvalue=changed\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "A[2:changed]", true)]
    [InlineData("value='first\nsecond'\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:first\nsecond]", true)]
    [InlineData("cat <<EOF\nbody ) '\nEOF\nvalue=changed\n)\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "body ) '\n[2:changed]", true)]
    [InlineData("value=changed; cat <<EOF; )\nbody\nEOF\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:outer]", true)]
    [InlineData("cat <<EOF\nfirst\nEOF\ncat <<EOF\nsecond\nEOF\nvalue=changed\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "first\nsecond\n[0:changed]", false)]
    [InlineData("f(){ cat <<EOF\nbody\nEOF\n}\nf\nvalue=changed\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "body\n[0:changed]", false)]
    [InlineData("# comment\n\n# second",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[0:outer]", false)]
    [InlineData("value=changed\nfalse\n\n# trailing",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[1:changed]", false)]
    [InlineData(";\nvalue=changed\n",
        "value=outer; source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:outer]", true)]
    [InlineData("value=changed\n)\n",
        "value=outer; ! source /source; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[0:changed]", true)]
    [InlineData("local value=changed\n)\n",
        "value=outer; f(){ source /source; printf '[%s:%s]' \"$?\" \"$value\"; }; f; printf '[root:%s]' \"$value\"",
        0, "[2:changed][root:outer]", true)]
    [InlineData("printf A\nvalue=changed\n)\n",
        "value=outer; output=$(source /source); printf '[%s:%s:%s]' \"$?\" \"$value\" \"$output\"",
        0, "[2:outer:A]", true)]
    [InlineData("value=changed\nother=set\n)\n",
        "value=outer; value=prefix source /source; printf '[%s:%s:%s]' \"$?\" \"$value\" \"$other\"",
        0, "[2:outer:set]", true)]
    [InlineData("value=changed\nbreak\n)\n",
        "value=outer; for i in 1 2; do source /source; printf wrong; done; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[2:changed]", true)]
    [InlineData("printf '%s' \"$i\"\ncontinue\n)\n",
        "for i in 1 2; do source /source; printf wrong; done; printf after",
        0, "12after", true)]
    [InlineData("continue\n)\n",
        "for i in 1 2; do source /source; printf wrong; done; printf '[%s]' \"$?\"",
        0, "[2]", true)]
    [InlineData("value=changed\nbreak\nvalue=wrong\n",
        "value=outer; for i in 1 2; do source /source; printf wrong; done; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[0:changed]", false)]
    [InlineData("continue\nvalue=wrong\n",
        "value=outer; for i in 1 2; do source /source; printf wrong; done; printf '[%s:%s]' \"$?\" \"$value\"",
        0, "[0:outer]", false)]
    [InlineData("return 7;\n'unterminated",
        "source /source; printf '[%s]' \"$?\"",
        0, "[7]", false)]
    public async Task SourcePreservesCompletedUnitEffectsAndControlFlow(
        string body, string script, int expectedStatus, string expectedOutput, bool hasDiagnostic)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([("/source", body)]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(hasDiagnostic, result.Host.StandardError.Content.Length > 0);
    }

    [Fact]
    public async Task EarlierUnitPreservesRawBytesBeforeLaterSyntaxFailure()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "printf \"value='\\377\\r'\\n(\\n\" > /source; value=outer; source /source; printf '[%s]' \"$value\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { (byte)'[', 0xff, 13, (byte)']' }, result.Host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task HostSyntaxExceptionDuringExecutionIsNotTreatedAsAParserFailure()
    {
        var state = new Eval(Parser.Parse("source file"));
        var host = new TestOperationVisitor([]);
        host.EnqueueOpenFileDescriptor(10);
        host.EnqueueReadResult(ReadResult.Success("value=changed\nexternal\nvalue=wrong\n"u8.ToArray(), [14, 23], reachedEoF: true));
        host.EnqueueCloseDescriptor();
        var failure = new SyntaxException(new SyntaxError("Host failure.", 0));
        host.EnqueueCommandException(failure);

        var error = await Assert.ThrowsAsync<SyntaxException>(() => new Engine().RunAsync(state, host));

        Assert.Same(failure, error);
        Assert.Equal(ShellValue.FromText("changed"), state.Variables["value"]);
        Assert.Equal(["external"], host.CommandNames);
        Assert.Equal([new FileDescriptor(10)], host.ClosedDescriptors);
    }
}
