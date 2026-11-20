using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ScriptCommandUnitTests
{
    public static IEnumerable<object[]> CompleteUnits()
    {
        yield return ["printf A\n)\n", "A", 2, true];
        yield return ["printf wrong; )\n", "", 2, true];
        yield return ["printf prefix \"$(for ((i=7)); do :; done)\"\nprintf wrong", "prefix", 0, true];
        yield return ["printf wrong; true && value=(\"$(for ((i=7)); do :; done)\")", "", 1, true];
        yield return ["printf A;\n)\n", "A", 2, true];
        yield return ["{\nprintf A\n}\n)\n", "A", 2, true];
        yield return ["{\nprintf wrong\n)\n}\n", "", 2, true];
        yield return ["f(){\nprintf wrong\n)\n}\n", "", 2, true];
        yield return ["f(){ printf A; }\nf\n)\n", "A", 2, true];
        yield return ["printf A |\ncat\n)\n", "A", 2, true];
        yield return ["false ||\nprintf A\n)\n", "A", 2, true];
        yield return ["printf '%s' 'a\nb'\n)\n", "a\nb", 2, true];
        yield return ["cat <<EOF\nbody ) '\nEOF\n)\n", "body ) '\n", 2, true];
        yield return ["printf wrong; cat <<EOF; )\nbody\nEOF\n", "", 2, true];
        yield return ["f(){ cat <<EOF\nbody\nEOF\n}\nf\n)\n", "body\n", 2, true];
        yield return ["printf A\nexit 7\n)\n", "A", 7, false];
        yield return ["set -e\nfalse\n)\n", "", 1, false];
        yield return ["trap 'printf \"trap:%s\" \"$?\"' EXIT\nprintf A\n)\n", "Atrap:2", 2, true];
        yield return ["x=$((1/0)); printf wrong\nprintf 'next:%s' \"$?\"\n", "next:1", 0, false];
        yield return ["readonly x=old\nx=new; printf wrong\nprintf 'next:%s:%s' \"$?\" \"$x\"\n", "next:1:old", 0, false];
        yield return ["set -e\nx=$((1/0))\nprintf after\n", "after", 0, false];
        yield return ["printf A\nx=${missing?bad}\nprintf wrong\n", "A", 1, false];
        yield return ["# comment\n\n# trailing", "", 0, false];
    }

    [Theory]
    [MemberData(nameof(CompleteUnits))]
    public Task PublicEntryPointRunsOnlyCompletedUnits(string source, string output, int status, bool syntaxError) =>
        AssertUnitsAsync(source, output, status, syntaxError);

    [Theory]
    [MemberData(nameof(CompleteUnits))]
    public Task EvalSharesTheCommandUnitPath(string source, string output, int status, bool syntaxError) =>
        AssertUnitsAsync("eval '" + source.Replace("'", "'\\''", StringComparison.Ordinal) + "'", output, status, syntaxError);

    [Theory]
    [InlineData("printf '<%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"; (exit 7); value=", "<2:2>|7:7", 7)]
    [InlineData("printf discarded; printf unfinished >", "|2:2", 2)]
    public async Task QuotedSubstitutionFailurePublishesParserStatusBeforeAnyCompletePrefix(
        string prefix, string output, int status)
    {
        var script = "trap 'printf \"|%s:%s\" \"$?\" \"${PIPESTATUS[*]}\"' EXIT\n"
            + prefix + "\"$(for ((i=7)); do :; done)\"\nprintf discarded";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task QuotedSubstitutionDiagnosticPrecedesPrefixRedirections()
    {
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        const string Script = """
            exec 2>diag; printf prefix >&2; value="$(for ((i=7)); do :; done)"
            : >discarded
            """;

        Assert.False(Parser.TryParse(Script, out _, out _));
        var result = await host.RunAsync(Script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
        Assert.Equal("prefix", host.ReadFile("/diag"));
        Assert.Equal("diag", Assert.Single(host.CaptureEntries()).Key);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(true, "value=$(for ((i=7)); do : >body; break; done)", 2, "2:inner:source-arg", "Arithmetic for header")]
    [InlineData(false, "value=\"$(printf hidden |)\"", 1, "257:inner:caller-arg", "Expected command after pipeline operator")]
    public async Task FatalSubstitutionInsideScriptBuiltinRunsExitWithActiveScope(
        bool source, string malformed, int status, string trapOutput, string diagnostic)
    {
        var inner = "value=inner\n: >completed\n" + malformed + "\n: >inner-after\n";
        var invoke = source ? "source unit source-arg" : "eval '" + inner + "'";
        var script = """
            set -- caller-arg
            value=outer
            trap 'printf '%s:%s:%s' "$?" "$value" "$1"' EXIT

            """ + invoke + " >out 2>err || : >fallback\n: >caller-after";
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            if (source)
                host.AddFile("/unit", inner);
            var actualStatus = prepared
                ? (await new Engine().RunAsync(new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(script)),
                    host, owner, CancellationToken.None)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(status, actualStatus);
            Assert.Empty(host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(trapOutput, host.ReadFile("/out"));
            Assert.Contains(diagnostic, host.ReadFile("/err"), StringComparison.Ordinal);
            Assert.Equal(source ? new[] { "completed", "err", "out", "unit" } : ["completed", "err", "out"],
                host.CaptureEntries().Keys.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(0, host.OpenFileCount);
            if (!prepared)
                Assert.Equal(0, governor.ReservedBytes);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("for ((i=7)); do :; done", "2")]
    [InlineData("value=\"$(for ((i=7)); do :; done)\"", "2")]
    [InlineData("value=(\"$(for ((i=7)); do :; done)\")", "1")]
    [InlineData("value=$(printf hidden |", "2")]
    public async Task RecoverableSourceSyntaxFailureReturnsToCaller(string malformed, string builtinStatus)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "printf before\n" + malformed);
        var result = await host.RunAsync("source unit\nprintf ':%s:after' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:" + builtinStatus + ":after", host.StandardOutput.TextContent);
        Assert.Contains("syntax error:", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("(", ")", "2:source-arg;2")]
    [InlineData("value=$(", "); printf '%s' \"$value\"", "1:caller-arg;0")]
    public async Task SourceSyntaxExitStaysAtItsChildShellBoundary(string open, string close, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "value=$(for ((i=7)); do :; done)");
        var script = "set -- caller-arg\n" + open
            + " trap 'printf \"%s:%s;\" \"$?\" \"$1\"' EXIT; source unit source-arg; : >child-after " + close
            + "\nprintf '%s' \"$?\"";
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Contains("Arithmetic for header", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.False(host.FileExists("/child-after"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("readonly value=old\nvalue=$(exit 7); printf wrong >skipped\n", "1:old", 0, "readonly variable")]
    [InlineData("set -e\nreadonly value=old\nvalue=new || printf wrong >skipped\n", "", 1, "readonly variable")]
    [InlineData("set -e\nreadonly value=old\neval 'value=new; printf wrong >skipped\n:' || printf wrong >skipped\n", "0:old", 0, "readonly variable")]
    [InlineData("set -e\nvalue=$((1/0)); printf wrong >skipped\n", "1:unset", 0, "division by 0")]
    [InlineData("set -e\nprintf '%s' p{Z..a}s; printf wrong >skipped\n", "", 1, "bad substitution")]
    [InlineData("declare -n ref\n: ${ref:=bad name}; printf wrong >skipped\n", "2:unset", 0, "not a valid identifier")]
    public async Task ExpansionFailuresResumeOnlyAtTheirNextAllowedUnit(
        string prefix, string expectedOutput, int expectedStatus, string diagnostic)
    {
        const string tail = "printf '%s:%s' \"$?\" \"${value-unset}\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(prefix + tail, host)).ExitCode
                : (await host.RunAsync(prefix + tail, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Contains(diagnostic, host.StandardError.TextContent, StringComparison.Ordinal);
            Assert.False(host.FileExists("/skipped"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private static async Task AssertUnitsAsync(string source, string output, int status, bool syntaxError)
    {
        await using var host = new TestHost();
        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(syntaxError, host.StandardError.TextContent.Contains("syntax error:", StringComparison.Ordinal));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task LaterSyntaxFailurePreservesDirectoryEffectsAndClosesPersistentDescriptors()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddDirectory("/work");
        var result = await BashScript.RunAsync(host,
            "cd /work\nexec 3>out\nprintf first >&3\ntrap 'printf trap >&3' EXIT\n)\n",
            EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(new ShellPath("/work"), result.CurrentDirectory);
        Assert.Equal("firsttrap", host.ReadFile("/work/out"));
        Assert.Contains("syntax error:", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void WholeScriptParsingStillValidatesBeforeExecution()
    {
        Assert.False(Parser.TryParse("printf earlier\n)\n", out var script, out var error));
        Assert.Null(script);
        Assert.NotNull(error);
    }
}
