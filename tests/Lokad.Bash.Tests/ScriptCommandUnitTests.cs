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
