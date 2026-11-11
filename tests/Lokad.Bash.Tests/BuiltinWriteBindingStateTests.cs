using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class BuiltinWriteBindingStateTests
{
    [Fact]
    public async Task CommandSubstitutionKeepsStderrOutsideItsStdoutCapture()
    {
        var result = await EndToEnd.EndToEndHarness.RunScriptAsync(
            "{ value=$(printf diagnostic >&2; printf captured); printf '<%s>' \"$value\"; } 2>&1",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("diagnostic<captured>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=old; printf -v 'a[0]' '%s' new; printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "a", true, false, "<0><new>")]
    [InlineData("a=([3]=old); printf -v a '%s' new; printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "a", true, false, "<0><3><new><old>")]
    [InlineData("a=outer; a=temporary printf -v 'a[0]' '%s' new; printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "a", true, false, "<0><new>")]
    [InlineData("a=outer; a=temporary printf -v a '%s' new; printf '<%s>' \"$a\"", "a", false, false, "<new>")]
    [InlineData("export a=outer; a=temporary printf -v 'a[0]' '%s' new; printf '<%s>' \"${!a[@]}\" \"${a[@]}\"", "a", true, true, "<0><new>")]
    [InlineData("i=outer; i=0 printf -v 'a[i++]' value; printf '<%s>' \"$i\"", "i", false, false, "<1>")]
    [InlineData("count=old; count=temp printf 'abc%n' count; printf '<%s>' \"$count\"", "count", false, false, "abc<3>")]
    public async Task PrintfWritesPreserveBindingKindAndCallerAttributes(
        string script, string name, bool array, bool exported, string output)
    {
        var state = new Eval(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(output), host.StandardOutput.Content.ToArray());
        Assert.Equal(array, state.IndexedArrays.ContainsKey(name));
        Assert.Equal(exported, state.ExportedVariables.Contains(name));
    }

    [Theory]
    [InlineData("declare -n ref; declare -n hop=ref; printf -v hop destination; printf '<%s>' \"$?\"", "<1>", "", true)]
    [InlineData("declare -n ref; printf -v ref 'bad name'; printf '<%s>' \"$?\"", "<1>", "7072696E74663A2060626164206E616D65273A206E6F7420612076616C6964206964656E7469666965720A", false)]
    [InlineData("declare -n ref; read ref <<< 'bad target'; printf '<%s>' \"$?\"", "<1>", "726561643A206062616420746172676574273A206E6F7420612076616C6964206964656E7469666965720A", false)]
    [InlineData("declare -n ref; declare -n hop=ref; read hop <<< target; printf '<%s>' \"$?\"", "<1>", "", true)]
    public async Task RejectedWritesRetainUnsetNamerefBindings(
        string script, string output, string errorHex, bool indirect)
    {
        var state = new Eval(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(output), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), host.StandardError.Content.ToArray());
        Assert.Equal(ShellValue.Empty, state.Namerefs["ref"]);
        if (indirect)
            Assert.Equal(ShellValue.FromText("ref"), state.Namerefs["hop"]);
        Assert.False(state.IndexedArrays.ContainsKey("ref"));
        Assert.False(state.Variables.ContainsKey("ref"));
    }
}
