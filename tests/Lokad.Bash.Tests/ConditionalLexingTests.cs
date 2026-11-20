using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class ConditionalLexingTests
{
    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task ConditionalLexicalRolesPreserveNativeBytesAndCompleteEffects(
        string name, string script, int status, string outputHex, bool diagnostic,
        string filesJson, string entriesJson, bool invalidSyntax, bool prepared)
    {
        Assert.NotEmpty(name);
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        using var files = JsonDocument.Parse(filesJson);
        foreach (var file in files.RootElement.EnumerateObject())
            host.AddFile("/" + file.Name, file.Value.GetString() ?? throw new InvalidDataException());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (prepared && invalidSyntax)
        {
            // Complete preparation rejects invalid root syntax before any evaluation.
            Assert.Throws<SyntaxException>(() => Parser.Parse(script));
            Assert.Empty(host.StandardOutput.Content.ToArray());
            Assert.Empty(host.StandardError.Content.ToArray());
        }
        else
        {
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
                : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);
            Assert.Equal(status, result.ExitCode);
            Assert.Equal(outputHex, Convert.ToHexString(host.StandardOutput.Content.Span));
            Assert.Equal(diagnostic, !host.StandardError.Content.IsEmpty);
        }
        using var expected = JsonDocument.Parse(entriesJson);
        var entries = host.CaptureEntries();
        Assert.Equal(expected.RootElement.EnumerateObject().Count(), entries.Count);
        foreach (var entry in expected.RootElement.EnumerateObject())
        {
            Assert.True(entries.TryGetValue(entry.Name, out var actual), entry.Name);
            Assert.Equal(FilePathKind.File, actual.Kind);
            Assert.Equal(entry.Value.GetString(), "File:" + actual.ContentHex);
        }
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ '#' == # ]] && : >effect")]
    [InlineData("[[ x == # ]] fake close\n ]] && : >effect")]
    [InlineData("[[ x == x # inert \\\n&& ]] && : >effect")]
    [InlineData("[[ x || -n > ]] && : >effect")]
    [InlineData("[[ x =~ (a ]] && : >effect")]
    public async Task MissingOperandsCannotExecuteEarlierLookingClosingDelimiters(string invalid)
    {
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var source = ": >before\n" + invalid + "\n: >after";
        Assert.False(Parser.TryParse(source, out _, out _));
        var result = await host.RunAsync(source, CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("before", entry.Key);
        Assert.Equal((FilePathKind.File, ""), entry.Value);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedConditionalSyntaxFitsOneSharedAllowanceWithoutExecutingTheSkippedTree(bool prepared)
    {
        var expression = "printf x >forbidden";
        for (var index = 0; index < 8; index++) expression = "[[ $(" + expression + ") ]]";
        var source = "false && " + expression + "; printf tail";
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(16 * 1024);
        await using var host = new TestHost { MemoryBudget = owner };
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, CancellationToken.None)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(16 * 1024, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedConditionalSyntaxPreservesEarlierCommandUnitsAndRefundsNormalizationScratch(bool comments)
    {
        var expression = string.Join(comments ? " # inert ]] $( '\n&& " : " && ", Enumerable.Repeat("x == x", 800));
        var governor = new MemoryGovernor(48 * 1024);
        using var owner = governor.CreateScope();
        using var retained = owner.Reserve(8 * 1024);
        await using var host = new TestHost { MemoryBudget = owner };
        var result = await host.RunAsync(": >before\n[[ " + expression + " ]] && : >forbidden\n: >after", CancellationToken.None);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("memory", host.StandardError.TextContent, StringComparison.OrdinalIgnoreCase);
        var entry = Assert.Single(host.CaptureEntries());
        Assert.Equal("before", entry.Key);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(8 * 1024, governor.ReservedBytes);
        retained.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
