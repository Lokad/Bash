using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class EvaluationMapfileTargetTests
{
    // Bash 5.2.21/C.UTF-8; only the executable/line prefix is omitted from diagnostics.
    [Theory]
    [InlineData("mapfile 'bad-name' <<< line; printf '%s' \"$?\"", "31", "6D617066696C653A20606261642D6E616D65273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile $'\\377' <<< line; printf '%s' \"$?\"", "31", "6D617066696C653A2060FF273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile 'arr[0]' <<< line; printf '%s' \"$?\"", "31", "6D617066696C653A20606172725B305D273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile 'arr=value' <<< line; printf '%s' \"$?\"", "31", "6D617066696C653A20606172723D76616C7565273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("arr=(old); declare -nr ref=arr; mapfile -t ref <<< line; printf '%s:<%s>' \"$?\" \"${arr[@]}\"", "303A3C6C696E653E", "", 0)]
    [InlineData("readonly arr; declare -n ref=arr; mapfile ref <<< line; printf '%s' \"$?\"", "31", "7265663A20726561646F6E6C79207661726961626C650A", 0)]
    [InlineData("declare -n ref; mapfile ref <<< line; printf '%s:<%s>' \"$?\" \"${ref[@]}\"", "303A3C6C696E650A3E", "7761726E696E673A207265663A2072656D6F76696E67206E616D65726566206174747269627574650A", 0)]
    [InlineData("declare -n first=second second=first; mapfile first <<< line; printf '%s:<%s>' \"$?\" \"${first[@]}\"", "303A3C6C696E650A3E", "7761726E696E673A2066697273743A2063697263756C6172206E616D65207265666572656E63650A", 0)]
    [InlineData("readarray bad-name <<< line; printf '%s' \"$?\"", "31", "7265616461727261793A20606261642D6E616D65273A206E6F7420612076616C6964206964656E7469666965720A", 0)]
    [InlineData("mapfile ${missing:?boom} \"$(printf forbidden >&2)\" <<< line; printf after", "", "6D697373696E673A20626F6F6D0A", 1)]
    [InlineData("printf 'still\\n' > input; declare -A arr=([key]=old); exec 3< input; mapfile arr <&3; status=$?; read next <&3; printf '%s:<%s>' \"$status\" \"$next\"", "313A3C7374696C6C3E", "6D617066696C653A206172723A206E6F7420616E20696E64657865642061727261790A", 0)]
    [InlineData("declare -nr first=second; declare -n second=first; mapfile first <<< line; printf '%s' \"$?\"", "30", "7761726E696E673A2066697273743A2063697263756C6172206E616D65207265666572656E63650A", 0)]
    [InlineData("declare -nr ref; mapfile ref <<< line; printf '%s' \"$?\"", "31", "7761726E696E673A207265663A2072656D6F76696E67206E616D65726566206174747269627574650A7265663A20726561646F6E6C79207661726961626C650A", 0)]
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
        Assert.Equal((BindingName)"PIPESTATUS", Assert.Single(state.IndexedArrays).Key);
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
