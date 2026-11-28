using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class SubshellLevelTests
{
    [Theory]
    [InlineData("""show(){ printf '<%s>' "$BASH_SUBSHELL"; }; show; (show; (show); show); show""", "<0><1><2><1><0>")]
    [InlineData("""show(){ printf '<%s>' "$BASH_SUBSHELL"; }; printf '%s' "$(show)" "`show`"; show""", "<1><1><0>")]
    [InlineData("""
        f(){ printf '<%s:%s:%s>' "$1" "$mark" "$BASH_SUBSHELL"; }
        mark=$BASH_SUBSHELL f "$BASH_SUBSHELL" | { raw=$(cat); printf '%s' "$raw"; }
        """, "<0:0:1>")]
    [InlineData("""show(){ printf '<%s>' "$BASH_SUBSHELL"; }; : | (show; (show)); show""", "<1><2><0>")]
    [InlineData("""BASH_SUBSHELL=7; BASH_SUBSHELL+=2; printf '<%s>' "$BASH_SUBSHELL"; (printf '<%s>' "$BASH_SUBSHELL")""", "<2><3>")]
    [InlineData("""BASH_SUBSHELL=7; declare -n level=BASH_SUBSHELL; level+=2; printf '<%s>' "$level"; (printf '<%s>' "$level")""", "<72><73>")]
    [InlineData("""BASH_SUBSHELL=7; BASH_SUBSHELL=word; printf '<%s>' "$BASH_SUBSHELL"; (printf '<%s>' "$BASH_SUBSHELL")""", "<0><1>")]
    [InlineData("""export BASH_SUBSHELL=8; bash -c 'declare -p BASH_SUBSHELL' fixture; printf '<%s>' "$BASH_SUBSHELL" """, "declare -x BASH_SUBSHELL=\"0\"\n<8>")]
    [InlineData("""unset BASH_SUBSHELL; BASH_SUBSHELL=ordinary; (printf '<%s>' "$BASH_SUBSHELL")""", "<ordinary>")]
    [InlineData("""BASH_SUBSHELL=4; f(){ local -I BASH_SUBSHELL=8; (printf '<%s>' "$BASH_SUBSHELL"); }; f; printf '<%s>' "$BASH_SUBSHELL" """, "<9><8>")]
    [InlineData("""
        BASH_SUBSHELL=3
        (f(){ local BASH_SUBSHELL=hidden; declare -g BASH_SUBSHELL=8; printf '<%s>' "$BASH_SUBSHELL"; }
        f; printf '<%s>' "$BASH_SUBSHELL")
        printf '<%s>' "$BASH_SUBSHELL"
        """, "<hidden><8><3>")]
    public Task ExpansionAndExecutionSeeTheirOwnSubshellLevels(string script, string expected) =>
        CheckBothEntryPointsAsync(script, expected, trace: null);

    [Theory]
    [InlineData("""show >"trace$((++BASH_SUBSHELL))" | :""", "/trace2", "<2>")]
    [InlineData("""(show) >"trace$BASH_SUBSHELL" | :""", "/trace1", "<1>")]
    [InlineData(""" >"trace$BASH_SUBSHELL" | :""", "/trace0", "")]
    public Task RedirectionsSeeTheReachedStageLevel(string stage, string path, string content) =>
        CheckBothEntryPointsAsync("show(){ printf '<%s>' \"$BASH_SUBSHELL\"; }; " + stage + "; show", "<0>", (path, content));

    private static async Task CheckBothEntryPointsAsync(string script, string expected, (string Path, string Content)? trace)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, CancellationToken.None)
                : await host.RunAsync(script, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            if (trace is { } file)
                Assert.Equal(file.Content, host.ReadFile(file.Path));
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }
}
