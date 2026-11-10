using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class SourceReturnContextTests
{
    [Theory]
    [InlineData("printf before; return 7; printf wrong",
        "source /source-a; printf ':%s:after' \"$?\"", "before:7:after")]
    [InlineData("printf before; return 7; printf wrong",
        ". /source-a; printf ':%s:after' \"$?\"", "before:7:after")]
    [InlineData("x=changed; return 7; printf wrong",
        "f(){ local x=local; source /source-a; printf '[f:%s:%s]' \"$?\" \"$x\"; }; f; printf '[root:%s]' \"${x-unset}\"", "[f:7:changed][root:unset]")]
    [InlineData("local x=inside; return 7",
        "x=outer; f(){ source /source-a; printf '[%s:%s]' \"$?\" \"$x\"; }; f; printf ':%s' \"$x\"", "[7:inside]:outer")]
    [InlineData("local x=wrong 2>/dev/null; printf '%s:%s' \"$?\" \"${x-unset}\"; return 7",
        "source /source-a; printf ':%s' \"$?\"", "1:unset:7")]
    [InlineData("g(){ printf G; return 4; printf wrong; }; g; printf '[s:%s]' \"$?\"; return 7; printf wrong",
        "source /source-a; printf '[root:%s]' \"$?\"", "G[s:4][root:7]")]
    [InlineData("eval 'printf E; return 6; printf wrong'; printf wrong",
        "source /source-a; printf '[root:%s]' \"$?\"", "E[root:6]")]
    [InlineData("x=temp eval 'return 6; printf wrong'; printf wrong",
        "source /source-a; printf '[root:%s]' \"$?\"", "[root:6]")]
    [InlineData("! { return 7; }; printf wrong",
        "source /source-a; printf '[root:%s]' \"$?\"", "[root:7]")]
    [InlineData("printf A; return 7; printf wrong",
        "value=$(source /source-a); status=$?; printf '%s:%s' \"$value\" \"$status\"", "A:7")]
    [InlineData("value=$(printf C; return 4; printf wrong); printf '[%s:%s]' \"$value\" \"$?\"; return 7",
        "source /source-a; printf '[root:%s]' \"$?\"", "[C:4][root:7]")]
    [InlineData("(printf C; return 4; printf wrong); printf '[%s]' \"$?\"; return 7",
        "source /source-a; printf '[root:%s]' \"$?\"", "C[4][root:7]")]
    [InlineData("x=child; printf C; return 7; printf wrong",
        "f(){ local x=outer; value=$(source /source-a); printf '[%s:%s:%s]' \"$value\" \"$?\" \"$x\"; }; f", "[C:7:outer]")]
    [InlineData("printf A; return 7; printf wrong",
        "! source /source-a; printf '[%s]' \"$?\"", "A[0]")]
    [InlineData("printf '%s' \"$i\"; break; printf wrong",
        "for i in 1 2; do source /source-a; printf wrong; done; printf after", "1after")]
    [InlineData("printf '%s' \"$i\"; continue; printf wrong",
        "for i in 1 2; do source /source-a; printf wrong; done; printf after", "12after")]
    [InlineData("printf '%s' \"$i\"; return 7; printf wrong",
        "for i in 1 2; do source /source-a; printf ':%s;' \"$?\"; done", "1:7;2:7;")]
    [InlineData("x=changed; y=set; return 7",
        "x=outer; x=temp source /source-a; printf '[%s:%s:%s]' \"$?\" \"$x\" \"$y\"", "[7:outer:set]")]
    [InlineData("shift; printf '%s' \"$1\"; return 7",
        "set -- outer; source /source-a inner more; printf '[%s:%s]' \"$?\" \"$1\"", "more[7:outer]")]
    [InlineData("set -- changed; return 7",
        "set -- outer; source /source-a inner; printf '[%s:%s]' \"$?\" \"$1\"", "[7:changed]")]
    [InlineData("g(){ printf G; return 4; printf wrong; }; return 7",
        "source /source-a; g; printf '[%s]' \"$?\"", "G[4]")]
    [InlineData("false; return; printf wrong",
        "source /source-a; printf '[%s]' \"$?\"", "[1]")]
    public async Task SourceConsumesOnlyItsReturnAndPreservesCallerState(string body, string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([("/source-a", body)]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task NestedSourceReturnsToItsImmediateCaller()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/source-a", "printf A; source /source-b; printf '[a:%s]' \"$?\"; return 7; printf wrong"),
            ("/source-b", "printf B; return 3; printf wrong")
        ]);
        var result = await EndToEndHarness.RunScriptAsync("source /source-a; printf '[root:%s]' \"$?\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("AB[a:3][root:7]", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task SourceInPipelineKeepsItsReturnBoundaryAndPipelineFailureStatus()
    {
        // Pipeline status follows this engine's existing pipefail policy.
        var tree = EndToEndHarness.BuildTree([("/source-a", "printf A; return 7; printf wrong")]);
        var result = await EndToEndHarness.RunScriptAsync("source /source-a | cat; printf '[%s]' \"$?\"", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("A[7]", result.StdOut);
        Assert.Empty(result.StdErr);
    }
}
