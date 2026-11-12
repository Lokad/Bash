using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationCommandQueryDescriptionTestsEndToEnd
{
    [Theory]
    [InlineData("command -V echo if", "echo is a shell builtin\nif is a shell keyword\n")]
    [InlineData("option=-V; command \"$option\" -- echo", "echo is a shell builtin\n")]
    [InlineData("builtin command -V echo > /out; cat /out", "echo is a shell builtin\n")]
    [InlineData("command -V", "")]
    [InlineData("command -V --", "")]
    [InlineData("command -v echo if", "echo\nif\n")]
    [InlineData("command -V -v printf", "printf\n")]
    [InlineData("command -v -V printf", "printf is a shell builtin\n")]
    [InlineData("command -VV printf", "printf is a shell builtin\n")]
    [InlineData("command -V -V printf", "printf is a shell builtin\n")]
    [InlineData("command -Vv printf", "printf\n")]
    [InlineData("command -vV printf", "printf is a shell builtin\n")]
    [InlineData("command -VvV -- printf", "printf is a shell builtin\n")]
    [InlineData("option='-V -v printf'; command $option", "printf\n")]
    [InlineData("command -V $(printf -- '-v printf')", "printf\n")]
    [InlineData("command -V -v", "")]
    [InlineData("command -vV --", "")]
    [InlineData("query='-V printf'; command $query", "printf is a shell builtin\n")]
    [InlineData("query='command -V printf'; $query", "printf is a shell builtin\n")]
    [InlineData("command -V : > /out; printf 'outside\\n'; cat /out", "outside\n: is a shell builtin\n")]
    [InlineData("x=:; x=printf command -V \"$x\"; printf '<%s>' \"$x\"", ": is a shell builtin\n<:>")]
    [InlineData("x=:; x=printf type \"$x\"; printf '<%s>' \"$x\"", ": is a shell builtin\n<:>")]
    [InlineData("IFS=:; names=printf:true; command -V $names", "printf is a shell builtin\ntrue is a shell builtin\n")]
    public async Task Evaluation_CommandQueryDescription_UsesExistingLookupAndOutput(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("command -v")]
    [InlineData("command -V")]
    public async Task RepeatedLiteralQueriesCompleteWithoutLifetimeCredits(string command)
    {
        // Single-quoted literals retain their source bytes; query decoding still allocates each time.
        var script = "for ((i=0;i<2200;i++)); do " + command + " '" + new string('x', 8192)
            + "' >/dev/null 2>&1; done; printf done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", result.StdOut);
        Assert.Empty(result.StdErr);
    }
}
