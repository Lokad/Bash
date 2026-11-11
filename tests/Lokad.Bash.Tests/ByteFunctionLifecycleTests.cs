using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteFunctionLifecycleTests
{
    // Bash 5.2.21/UTF-8; only executable/function/line prefixes are omitted.
    [Theory]
    [InlineData("probe_f(){ :; }; declare -fp $'\\377' probe_f; printf '%s' \"$?\"", "70726F62655F66202829200A7B200A202020203A0A7D0A31", "6465636C6172653A20FF3A206E6F7420666F756E640A")]
    [InlineData("probe_f(){ :; }; declare -Fp $'\\377' probe_f; printf '%s' \"$?\"", "6465636C617265202D662070726F62655F660A31", "6465636C6172653A20FF3A206E6F7420666F756E640A")]
    [InlineData("probe_f(){ :; }; declare -fp probe_f=$'\\377' probe_f; printf '%s' \"$?\"", "70726F62655F66202829200A7B200A202020203A0A7D0A31", "6465636C6172653A2070726F62655F663DFF3A206E6F7420666F756E640A")]
    [InlineData("unset $'-\\377'; printf '%s' \"$?\"", "32", "756E7365743A202DFF3A20696E76616C6964206F7074696F6E0A756E7365743A2075736167653A20756E736574205B2D665D205B2D765D205B2D6E5D205B6E616D65202E2E2E5D0A")]
    [InlineData("probe_f(){ printf '%s' $'\\377\\r'; }; readonly -f probe_f; probe_f; unset -f probe_f; printf '%s' \"$?\"", "FF0D31", "756E7365743A2070726F62655F663A2063616E6E6F7420756E7365743A20726561646F6E6C792066756E6374696F6E0A")]
    [InlineData("function $'probe_\\141' { :; }; printf '%s' \"$?\"", "31", "602770726F62655F6127273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("function $'probe_\\047' { :; }; printf '%s' \"$?\"", "31", "602770726F62655F275C272727273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task FunctionQueriesRetainBytesAndCanonicalNameDiagnostics(
        string script, string outputHex, string errorHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("FF28297B203A3B207D3B206465636C617265202D46702024275C333737273B206578706F7274202D662024275C333737273B206465636C617265202D46702024275C333737273B20756E736574202D662024275C333737273B206465636C617265202D46702024275C333737273B207072696E746620272573272022243F22", "6465636C617265202D6620FF0A6465636C617265202D667820FF0A31", "6465636C6172653A20FF3A206E6F7420666F756E640A")]
    [InlineData("FF28297B203A3B207D3B20726561646F6E6C79202D662024275C333737273B20FF28297B207072696E74662077726F6E673B207D3B20756E736574202D662024275C333737273B206465636C617265202D46702024275C33373727", "6465636C617265202D667220FF0A", "FF3A20726561646F6E6C792066756E6374696F6E0A756E7365743A20FF3A2063616E6E6F7420756E7365743A20726561646F6E6C792066756E6374696F6E0A")]
    [InlineData("6628297B207072696E746620272573272027FF273B207D3B206465636C617265202D6620663B2066", "66202829200A7B200A202020207072696E746620272573272027FF270A7D0AFF", "")]
    public async Task RawFunctionNamesRetainOneLifecycleNamespace(
        string sourceHex, string outputHex, string errorHex)
    {
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        var result = await EndToEndHarness.RunSourceAsync(Convert.FromHexString(sourceHex), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("6628297B207072696E746620272573272027FF273B207D", "f", "FF", "")]
    [InlineData("6628297B207072696E74662027257327205CFF3B207D", "f", "FF", "")]
    [InlineData("6628297B20636174203C3C27454E44270AFF0D0A454E440A7D", "f", "FF0D0A", "")]
    [InlineData("6628297B20783D27FF273B207072696E7466202725732720222478223B207D", "f", "FF", "")]
    [InlineData("6628297B206465636C617265202D4120613D285B6B65795D3D27FF27293B207072696E746620272573272022247B615B6B65795D7D223B207D", "f", "FF", "")]
    [InlineData("6628297B207072696E74662027257327202224287072696E74662027FF2729223B207D", "f", "FF", "")]
    [InlineData("6628297B2066756E6374696F6E20242770726F62655F5C31343127207B203A3B207D3B207072696E746620272573272022243F223B207D", "f", "31", "602770726F62655F6127273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("6628297B2066756E6374696F6E20242770726F62655F5C30343727207B203A3B207D3B207072696E746620272573272022243F223B207D", "f", "31", "602770726F62655F275C272727273A206E6F7420612076616C6964206964656E7469666965720A")]
    public async Task DefinitionOutputReplaysByteSourceWithoutChangingEffects(
        string definitionHex, string invocation, string outputHex, string errorHex)
    {
        var definitionBytes = Convert.FromHexString(definitionHex);
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(definitionBytes).Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        using var replay = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        replay.Append(output.ToMemory()).Append(Utf8Text.Encode(invocation));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));

        var result = await EndToEndHarness.RunSourceAsync(replay.ToMemory(), host, TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), host.StandardError.Content.ToArray());
    }
}
