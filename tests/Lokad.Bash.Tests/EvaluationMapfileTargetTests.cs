using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class EvaluationMapfileTargetTests
{
    // Bash 5.2.21 in C.UTF-8. The evaluator supplies the line for destination errors.
    [Theory]
    [InlineData("mapfile 'bad-name' <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A206D617066696C653A20606261642D6E616D65273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile $'\\377' <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A206D617066696C653A2060FF273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile 'arr[0]' <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A206D617066696C653A20606172725B305D273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile 'arr=value' <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A206D617066696C653A20606172723D76616C7565273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("arr=(old); declare -nr ref=arr; mapfile -t ref <<< line; printf '%s:<%s>' \"$?\" \"${arr[@]}\"", "303A3C6C696E653E", "", 0)]
    [InlineData("readonly arr; declare -n ref=arr; mapfile ref <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A207265663A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("declare -n ref; mapfile ref <<< line; printf '%s:<%s>' \"$?\" \"${ref[@]}\"", "303A3C6C696E650A3E", "7761726E696E673A207265663A2072656D6F76696E67206E616D65726566206174747269627574650A", 0)]
    [InlineData("declare -n first=second second=first; mapfile first <<< line; printf '%s:<%s>' \"$?\" \"${first[@]}\"", "303A3C6C696E650A3E", "6C696E6520313A207761726E696E673A2066697273743A2063697263756C6172206E616D65207265666572656E63650A", 0)]
    [InlineData("readarray bad-name <<< line; printf '%s' \"$?\"", "31", "6C696E6520313A207265616461727261793A20606261642D6E616D65273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile ${missing:?boom} \"$(printf forbidden >&2)\" <<< line; printf after", "", "6C696E6520313A206D697373696E673A20626F6F6D0A", 1)]
    [InlineData("printf 'still\\n' > input; declare -A arr=([key]=old); exec 3< input; mapfile arr <&3; status=$?; read next <&3; printf '%s:<%s>' \"$status\" \"$next\"", "313A3C7374696C6C3E", "6C696E6520313A206D617066696C653A206172723A206E6F7420616E20696E64657865642061727261790A", 0)]
    [InlineData("declare -nr first=second; declare -n second=first; mapfile first <<< line; printf '%s' \"$?\"", "30", "6C696E6520313A207761726E696E673A2066697273743A2063697263756C6172206E616D65207265666572656E63650A", 0)]
    [InlineData("declare -nr ref; mapfile ref <<< line; printf '%s' \"$?\"", "31", "7761726E696E673A207265663A2072656D6F76696E67206E616D65726566206174747269627574650A6C696E6520313A207265663A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("declare -n first=second second; mapfile first <<< line; printf '%s:<%s>' \"$?\" \"${first[@]}\"", "303A3C6C696E650A3E", "7761726E696E673A2066697273743A2072656D6F76696E67206E616D65726566206174747269627574650A", 0)]
    public async Task TargetsRetainNativeBytesAndBindingPolicy(
        string script, string outputHex, string errorHex, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("mapfile -t -O 1 first <<<7", "[0]=\"other\" [1]=\"7\"")]
    [InlineData("mapfile -t -O 1 -C ':' -c 1 first <<<7", "[0]=\"other\" [1]=\"7\"")]
    [InlineData("read -a first <<< '7 8'", "[0]=\"7\" [1]=\"8\"")]
    public async Task IndexedInputPublishesToTheSavedBindingBehindACircularLocal(string command, string elements)
    {
        var result = await EndToEndHarness.RunScriptAsync($$"""
            readonly other=kept; declare -nx first=other
            f() {
                local -n first=second second=first
                {{command}}
                printf '<%s>' "$?"; declare -p first second
            }; f
            declare -p first other
            """, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<0>declare -nx first=\"second\"\ndeclare -n second=\"first\"\n"
            + $"declare -ax first=({elements})\ndeclare -r other=\"kept\"\n", result.Host.StandardOutput.TextContent);
        Assert.Equal("line 4: warning: first: circular name reference\n", result.Host.StandardError.TextContent);
        Assert.Equal(0, result.Host.OpenFileCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NamerefConversionWaitsForSuccessfulInput(bool circular, bool cancel)
    {
        var script = circular ? "declare -n ref=second second=ref; mapfile ref" : "declare -n ref; mapfile ref";
        var state = new Eval(Parser.Parse(script));
        var host = new TestOperationVisitor([]);
        host.EnqueueAppendResult(DescriptorWriteResult.Open); // The binding warning precedes input consumption.
        host.EnqueueReadResult(ReadResult.Success(new byte[] { 0xff, 10 }, [], reachedEoF: false));
        using var cancellation = new CancellationTokenSource();
        if (cancel)
        {
            cancellation.Cancel();
            host.EnqueueReadTask(Task.FromCanceled<ReadResult>(cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Engine().RunAsync(state, host));
        }
        else
        {
            host.EnqueueReadResult(ReadResult.Failure(error: new OperationError("read error\n")));
            await new Engine().RunAsync(state, host);
            Assert.Equal(1, state.LastExitCode);
        }

        Assert.Equal(ShellValue.FromText(circular ? "second" : string.Empty), state.Namerefs["ref"]);
        if (circular) Assert.Equal(ShellValue.FromText("ref"), state.Namerefs["second"]);
        Assert.Equal(new BindingName[] { "DIRSTACK", "PIPESTATUS" }, state.IndexedArrays.Keys.OrderBy(name => name.CachedText, StringComparer.Ordinal));
        Assert.Empty(host.ClosedDescriptors);
    }

    [Theory]
    [InlineData("declare -n ref")]
    [InlineData("declare -n ref=second second=ref")]
    [InlineData("declare -nx ref=second; declare -n second=ref")]
    [InlineData("declare -nr ref=second; declare -n second=ref")]
    public async Task SuccessfulConversionKeepsArrayAndNamerefKindsExclusive(string declaration)
    {
        var state = new Eval(Parser.Parse(declaration + "; mapfile -t ref"));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        host.SetStandardInputBytes(new byte[] { 0xff, 10 });

        await new Engine().RunAsync(state, host);

        Assert.Equal(0, state.LastExitCode);
        Assert.False(state.Namerefs.ContainsKey("ref"));
        Assert.DoesNotContain("ref", state.ExportedVariables);
        Assert.DoesNotContain("ref", state.ReadonlyVariables);
        Assert.Equal(new byte[] { 0xff }, Assert.Single(state.IndexedArrays["ref"]).Bytes.ToArray());
    }
}
