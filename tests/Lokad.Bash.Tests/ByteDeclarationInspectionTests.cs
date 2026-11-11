using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteDeclarationInspectionTests
{
    // Bash 5.2.21/UTF-8; only executable/function/line prefixes are omitted from diagnostics.
    [Theory]
    [InlineData("x=$'\\377\\376\\r\\n'; declare -p x; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C3337375C3337365C725C6E270AFFFE0D0A", "")]
    [InlineData("a=([1000000000]=$'\\377' [2]=$'\\376'); declare -p a; printf '%s' \"${a[@]}\"", "6465636C617265202D6120613D285B325D3D24275C33373627205B313030303030303030305D3D24275C33373727290AFEFF", "")]
    [InlineData("declare -A a=(['two words']=$'\\377\\r'); declare -p a; printf '%s' \"${a['two words']}\"", "6465636C617265202D4120613D285B2274776F20776F726473225D3D24275C3337375C722720290AFF0D", "")]
    [InlineData("x=$'\\377'; declare -n ref=x; declare -p ref x; printf '%s' \"$ref\"", "6465636C617265202D6E207265663D2278220A6465636C617265202D2D20783D24275C333737270AFF", "")]
    [InlineData("declare x; y=$'\\377'; z=; declare -p x y z; printf '<%s>' \"${x-unset}\" \"$y\" \"$z\"", "6465636C617265202D2D20780A6465636C617265202D2D20793D24275C333737270A6465636C617265202D2D207A3D22220A3C756E7365743E3CFF3E3C3E", "")]
    [InlineData("x=$'\\376'; declare -p x=$'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6465636C6172653A20783DFF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\376'; declare -p x+=$'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6465636C6172653A20782B3DFF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\376'; declare -p $'\\377' x; printf '%s:' \"$?\"; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFE", "6465636C6172653A20FF3A206E6F7420666F756E640A")]
    [InlineData("x=$'\\377'; f(){ local x=$'\\376'; local -p x=$'\\375' x; printf '%s:' \"$?\"; printf '%s' \"$x\"; }; f; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333736270A313AFEFF", "6C6F63616C3A20783DFD3A206E6F7420666F756E640A")]
    [InlineData("probe_x=$'\\377'; probe_x=$'\\376' declare -p probe_x; probe_x=$'\\375' declare -p | fixture-bindings; declare -p probe_x", "6465636C617265202D782070726F62655F783D24275C333736270A6465636C617265202D2D2070726F62655F783D24275C333737270A6465636C617265202D2D2070726F62655F783D24275C333737270A", "")]
    [InlineData("probe_x=$'\\377\\r\\n'; declare | fixture-bindings; printf '%s' \"$probe_x\"", "70726F62655F783D24275C3337375C725C6E270AFF0D0A", "")]
    [InlineData("declare -pa a=($'\\377' $'\\376'); printf '%s' \"${a[@]}\"", "6465636C617265202D6120613D285B305D3D24275C33373727205B315D3D24275C33373627290AFFFE", "")]
    [InlineData("x=$'\\377\\r\\n'; snapshot=$(declare -p x); x=changed; eval \"$snapshot\"; printf '%s' \"$x\"", "FF0D0A", "")]
    public async Task InspectionPreservesValueBytesAndBindingState(
        string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("x='", "'; declare -p x; printf '%s' \"$x\"", "6465636C617265202D2D20783D24275C333737270AFF", "")]
    [InlineData("x=ok; declare -p '", "' x; printf '%s' \"$?\"", "6465636C617265202D2D20783D226F6B220A31", "6465636C6172653A20FF3A206E6F7420666F756E640A")]
    public async Task InspectionPreservesRawSourceBytes(
        string prefix, string suffix, string outputHex, string errorHex)
    {
        var source = ByteMemoryBuilder.CreateUnaccounted(0);
        source.Append(Utf8Text.Encode(prefix)).Append(0xff).Append(Utf8Text.Encode(suffix));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        var result = await EndToEndHarness.RunSourceAsync(source.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void RenderingPreservesBytesWithoutChangingTheBinding()
    {
        var variables = Variables.CreateEmpty();
        var value = ShellValue.FromBytes(new byte[] { 0xff });
        Assert.True(variables.TrySetScalar("x", value));
        variables.SetReadonly("x");
        var expected = Utf8Text.Encode("declare -r x=$'\\377'\n");
        var complete = ByteMemoryBuilder.CreateUnaccounted(0);

        Assert.True(variables.TryAppendVariable("x", complete, VariableDisplayStyle.Declaration));
        Assert.Equal(expected.ToArray(), complete.ToMemory().ToArray());
        Assert.True(variables.TryGetScalar("x", out var retained));
        Assert.Equal(value, retained);
        Assert.True(variables.IsReadonly("x"));
    }

    [Fact]
    public async Task InspectionRefusesUnadmittedOutputWithoutChangingTheBinding()
    {
        const int InputBytes = 128 * 1024;
        const long MemoryBudgetBytes = 1024 * 1024;
        var input = new byte[InputBytes + 1];
        input.AsSpan().Fill(1); // Display expands each control byte to a four-byte escape.
        input[^1] = (byte)'\n';
        var governor = new MemoryGovernor(MemoryBudgetBytes);
        await using var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.ConfigureHost(host => { host.AddFile("/input", input); }), governor);
        var state = new Eval(Parser.Parse(
            "read -r saved </input; declare -p saved >/dev/null; printf '%s' \"$?\""));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("1", host.StandardOutput.TextContent);
        Assert.Equal("declare: shared memory budget exhausted\n", host.StandardError.TextContent);
        Assert.True(state.Variables["saved"].Bytes.Span.SequenceEqual(input.AsSpan(0, InputBytes)));
    }

    [Fact]
    public async Task InspectionStopsAfterAnOutputFailureWithoutChangingBindings()
    {
        var state = new Eval(Parser.Parse("x=one; y=two; declare -p x y"));
        var host = new TestOperationVisitor([]);
        host.EnqueueAppendResult(DescriptorWriteResult.Failure(new OperationError("Write failed\n"))); // A second row would exhaust the host's queue.

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(1, state.LastExitCode);
        Assert.Equal(ShellValue.FromText("one"), state.Variables["x"]);
        Assert.Equal(ShellValue.FromText("two"), state.Variables["y"]);
        Assert.Empty(host.ClosedDescriptors);
    }
}
