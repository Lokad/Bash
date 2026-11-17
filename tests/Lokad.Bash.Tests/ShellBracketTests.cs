using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class ShellBracketTests
{
    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task SharedBracketRolesPreserveNativeBytesAndCompleteEffects(
        string name, string script, string outputHex, string filesJson, string directoriesJson, string entriesJson, bool prepared)
    {
        Assert.NotEmpty(name);
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        using var files = JsonDocument.Parse(filesJson);
        foreach (var file in files.RootElement.EnumerateObject())
            host.AddFile("/" + file.Name, file.Value.GetString() ?? throw new InvalidDataException());
        using var directories = JsonDocument.Parse(directoriesJson);
        foreach (var directory in directories.RootElement.EnumerateArray())
            host.AddDirectory("/" + (directory.GetString() ?? throw new InvalidDataException()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(outputHex, Convert.ToHexString(host.StandardOutput.Content.Span));
        Assert.Empty(host.StandardError.Content.ToArray());
        using var expected = JsonDocument.Parse(entriesJson);
        var entries = host.CaptureEntries();
        Assert.Equal(expected.RootElement.EnumerateObject().Count(), entries.Count);
        foreach (var entry in expected.RootElement.EnumerateObject())
        {
            Assert.True(entries.TryGetValue(entry.Name, out var actual), entry.Name);
            Assert.Equal(entry.Value.GetString(), actual.Kind + ":" + actual.ContentHex);
        }
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScalarMembershipRetainsTheExplicitFixedClassProfile(bool prepared)
    {
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var source = "value=$'\\303\\251'; [[ $value == [[:alpha:]] ]]; printf '<%s>' \"$?\"; "
            + "[[ $value == [![:alpha:]] ]]; printf '<%s>' \"$?\"; "
            + "[[ $value == [![:ascii:]] ]]; printf '<%s>' \"$?\"; "
            + "[[ $value == [[.$value.]] ]]; printf '<%s>' \"$?\"; "
            + "value=$'\\377'; [[ $value == [![:ascii:]] ]]; printf '<%s>' \"$?\"";
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        // Scalar width follows UTF-8; class membership remains ASCII/C rather than locale-dependent.
        Assert.Equal("<1><0><0><0><0>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
