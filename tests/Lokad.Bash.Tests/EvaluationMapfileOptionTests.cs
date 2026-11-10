using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class EvaluationMapfileOptionTests
{
    // Usage intentionally advertises the supported subset; native comparisons retain the full Bash text.
    [Theory]
    [InlineData("arr=(old); mapfile -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("arr=(old); readarray -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "readarray: -x: invalid option\nreadarray: usage: readarray [-t] [--] [array]\n")]
    [InlineData("arr=(old); mapfile -ttx arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("mapfile -xtz; printf '<%s>' \"$?\"", 0, "<2>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("mapfile --invalid; printf '<%s>' \"$?\"", 0, "<2>", "mapfile: --: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("arr=(old); arr=temporary mapfile -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("! mapfile -x arr; printf '<%s>' \"$?\"", 0, "<0>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("readarray -x 2>&1; printf '<%s>' \"$?\"", 0, "readarray: -x: invalid option\nreadarray: usage: readarray [-t] [--] [array]\n<2>", "")]
    [InlineData("mapfile -x 2>&-; printf '<%s>' \"$?\"", 0, "<1>", "")]
    [InlineData("mapfile -x \"$(printf expanded >&2)\"; printf '<%s>' \"$?\"", 0, "<2>", "expandedmapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("readonly arr; mapfile -x arr; printf '<%s>' \"$?\"", 0, "<2>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("mapfile -x bad-name; printf '<%s>' \"$?\"", 0, "<2>", "mapfile: -x: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("arr=(old); mapfile -n 1 arr </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<2,old>", "mapfile: -n: invalid option\nmapfile: usage: mapfile [-t] [--] [array]\n")]
    [InlineData("arr=(old); mapfile arr extra </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<0,empty>", "")]
    [InlineData("mapfile -- -x; printf '<%s>' \"$?\"", 0, "<1>", "mapfile: `-x': not a valid identifier\n")]
    [InlineData("mapfile -tt -t -- arr <<< line; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<0,line>", "")]
    [InlineData("arr=(old); mapfile arr -x </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<0,empty>", "")]
    [InlineData("mapfile -x \"${missing:?boom}\"; printf after", 1, "", "missing: boom\n")]
    public async Task OptionsPreserveDiagnosticAndEffectOrder(
        string script, int exitCode, string output, string error)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(output), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(error), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("mapfile $'-\\377'; printf '<%s>' \"$?\"", "mapfile")]
    [InlineData("readarray $'-t\\377'; printf '<%s>' \"$?\"", "readarray")]
    public async Task InvalidOptionDiagnosticRetainsTheOriginalByte(string script, string builtin)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        byte[] expected =
        [
            .. Encoding.UTF8.GetBytes(builtin + ": -"), 0xff,
            .. Encoding.UTF8.GetBytes(": invalid option\n" + builtin + ": usage: " + builtin + " [-t] [--] [array]\n")
        ];

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2>", result.StdOut);
        Assert.Equal(expected, result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("mapfile", false)]
    [InlineData("mapfile", true)]
    [InlineData("readarray", false)]
    [InlineData("readarray", true)]
    public async Task FailedDiagnosticWritePropagatesStatusWithoutRetainingPrefix(string builtin, bool negated)
    {
        var script = "arr=(old); " + (negated ? "! " : "") + "arr=temporary " + builtin + " -x arr";
        var state = new Eval(Parser.Parse(script));
        var diagnostic = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal(builtin + ": -x: invalid option\n" + builtin + ": usage: " + builtin + " [-t] [--] [array]\n",
            Encoding.UTF8.GetString(diagnostic.Content.Span));
        diagnostic.SetResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        // Completion without another operation proves borrowed input was neither read nor closed.
        Assert.Null(await state.GetNextStep());

        Assert.Equal(negated ? 0 : 1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }
}
