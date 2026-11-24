using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class EvaluationMapfileOptionTests
{
    private const string UsageArguments = "[-t] [-d delim] [-n count] [-s count] [-O origin] [-u fd] [-C callback] [-c quantum] [--] [array]";

    // Usage intentionally advertises the supported subset; native comparisons retain the full Bash text.
    [Theory]
    [InlineData("arr=(old); mapfile -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("arr=(old); readarray -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "line 1: readarray: -x: invalid option\nreadarray: usage: readarray " + UsageArguments + "\n")]
    [InlineData("arr=(old); mapfile -ttx arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("mapfile -xtz; printf '<%s>' \"$?\"", 0, "<2>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("mapfile --invalid; printf '<%s>' \"$?\"", 0, "<2>", "line 1: mapfile: --: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("arr=(old); arr=temporary mapfile -x arr; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<2,old>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("! mapfile -x arr; printf '<%s>' \"$?\"", 0, "<0>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("readarray -x 2>&1; printf '<%s>' \"$?\"", 0, "line 1: readarray: -x: invalid option\nreadarray: usage: readarray " + UsageArguments + "\n<2>", "")]
    [InlineData("mapfile -x 2>&-; printf '<%s>' \"$?\"", 0, "<2>", "")]
    [InlineData("mapfile -x \"$(printf expanded >&2)\"; printf '<%s>' \"$?\"", 0, "<2>", "expandedline 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("readonly arr; mapfile -x arr; printf '<%s>' \"$?\"", 0, "<2>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("mapfile -x bad-name; printf '<%s>' \"$?\"", 0, "<2>", "line 1: mapfile: -x: invalid option\nmapfile: usage: mapfile " + UsageArguments + "\n")]
    [InlineData("arr=(old); mapfile -n 1 arr </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<0,empty>", "")]
    [InlineData("arr=(old); mapfile -O0 -n 1 arr </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<0,old>", "")]
    [InlineData("mapfile -n -1; printf '<%s>' \"$?\"", 0, "<1>", "line 1: mapfile: -1: invalid line count\n")]
    [InlineData("readarray -tO0x2; printf '<%s>' \"$?\"", 0, "<1>", "line 1: readarray: 0x2: invalid array origin\n")]
    [InlineData("mapfile -n $'2\\r'; printf '<%s>' \"$?\"", 0, "<1>", "line 1: mapfile: 2\r: invalid line count\n")]
    [InlineData("mapfile -n $'\\n+2' <<< line; printf '<%s,%s>' \"$?\" \"${#MAPFILE[@]}\"", 0, "<0,1>", "")]
    [InlineData("a=scalar; mapfile -tn1 -O3 a <<< value; printf '<%s><%s>' \"${a[0]}\" \"${a[3]}\"", 0, "<scalar><value>", "")]
    [InlineData("declare -a a; mapfile -tO4294967294 -n4 a <<< $'first\\nsecond\\nthird\\nfourth\\nignored'; printf '<%s>/<%s>' \"${!a[*]}\" \"${a[*]}\"", 0, "<0 1 4294967294 4294967295>/<third fourth first second>", "")]
    [InlineData("a=([1]=keep); readarray -td: -O4294967295 -n3 a <<< 'a:b:c:d'; printf '<%s><%s><%s>' \"${a[4294967295]}\" \"${a[0]}\" \"${a[1]}\"", 0, "<a><b><c>", "")]
    [InlineData("a=([0]=keep [4]=old [8]=last); { mapfile -tn2 -O4 a; IFS= read -r next; } <<< $'first\\nsecond\\nthird'; printf '<%s><%s><%s><%s><%s>' \"${a[0]}\" \"${a[4]}\" \"${a[5]}\" \"${a[8]}\" \"$next\"", 0, "<keep><first><second><last><third>", "")]
    [InlineData("a=([0]=keep [4]=old); { a=temp mapfile -tn1 -O4 a; } <<< changed; printf '<%s><%s>' \"${a[0]}\" \"${a[4]}\"", 0, "<keep><old>", "")]
    [InlineData("arr=(old); mapfile arr extra </dev/null; printf '<%s,%s>' \"$?\" \"${arr[0]-empty}\"", 0, "<0,empty>", "")]
    [InlineData("mapfile -- -x; printf '<%s>' \"$?\"", 0, "<1>", "line 1: mapfile: `-x': not a valid identifier\n")]
    [InlineData("mapfile -tt -t -- arr <<< line; printf '<%s,%s>' \"$?\" \"${arr[0]}\"", 0, "<0,line>", "")]
    [InlineData("readarray -c ' +2 ' -c4294967295 -tn2 arr <<< $'a\\nb\\nc'; printf '<%s><%s><%s>' \"$?\" \"${arr[0]}\" \"${arr[1]}\"", 0, "<0><a><b>", "")]
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
            .. Encoding.UTF8.GetBytes("line 1: " + builtin + ": -"), 0xff,
            .. Encoding.UTF8.GetBytes(": invalid option\n" + builtin + ": usage: " + builtin + " " + UsageArguments + "\n")
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
        Assert.Equal("line 1: " + builtin + ": -x: invalid option\n" + builtin + ": usage: " + builtin + " " + UsageArguments + "\n",
            Encoding.UTF8.GetString(diagnostic.Content.Span));
        diagnostic.SetResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n")));

        // Completion without another operation proves borrowed input was neither read nor closed.
        Assert.Null(await state.GetNextStep());

        Assert.Equal(negated ? 0 : 1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("old"), Assert.Single(state.IndexedArrays["arr"]));
    }
}
