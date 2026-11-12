using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteFieldBoundaryTests
{
    [Theory]
    [InlineData(false, false, new[] { "707265", "706F7374" })]
    [InlineData(true, false, new[] { "707265C3", "706F7374" })]
    [InlineData(false, true, new[] { "707265", "A9706F7374" })]
    public void AdjacentExpansionBytes_PreserveQuotedFieldBoundaries(
        bool quoteFirst, bool quoteSecond, string[] expected)
    {
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["IFS"] = ShellValue.FromText("é") }, []);
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var builder = new ExpandedWordBuilder(variables, budget, ExpansionPurpose.Normal, ImplicitEmptyPolicy.Preserve);
        builder.AppendSource(ShellSource.FromText("pre"), globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xc3 }),
            allowWordSplitting: !quoteFirst, globQuoted: quoteFirst);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xa9 }),
            allowWordSplitting: !quoteSecond, globQuoted: quoteSecond);
        builder.AppendSource(ShellSource.FromText("post"), globQuoted: false);

        Assert.Equal(expected, builder.Build().Select(word => Convert.ToHexString(word.ToValue(budget).Bytes.Span)));
    }

    [Fact]
    public void GlobMetadata_DoesNotInterruptAFieldSeparatorCharacter()
    {
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["IFS"] = ShellValue.FromText("é") }, []);
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var builder = new ExpandedWordBuilder(variables, budget, ExpansionPurpose.Normal, ImplicitEmptyPolicy.Preserve);
        builder.AppendText("pre", globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xc3 }),
            allowWordSplitting: true, globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xa9 }),
            allowWordSplitting: true, globQuoted: true);
        builder.AppendText("post", globQuoted: false);

        Assert.Equal(new[] { "pre", "post" }, builder.Build().Select(word => word.ToValue(budget).ToUtf8String()));
    }

    [Theory]
    [InlineData("preé", "C3A9", "post", new[] { "707265C3", "", "706F7374" })]
    [InlineData("pre", "C3", "épost", new[] { "707265", "C3", "706F7374" })]
    [InlineData("pre", "FF", "post", new[] { "707265FF706F7374" })]
    public void SourceAndInvalidBytes_RetainTheirFieldProvenance(
        string prefix, string expansionHex, string suffix, string[] expected)
    {
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["IFS"] = ShellValue.FromText("é") }, []);
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var builder = new ExpandedWordBuilder(variables, budget, ExpansionPurpose.Normal, ImplicitEmptyPolicy.Preserve);
        builder.AppendSource(ShellSource.FromText(prefix), globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(Convert.FromHexString(expansionHex)),
            allowWordSplitting: true, globQuoted: false);
        builder.AppendSource(ShellSource.FromText(suffix), globQuoted: false);

        Assert.Equal(expected, builder.Build().Select(word => Convert.ToHexString(word.ToValue(budget).Bytes.Span)));
    }

    [Theory]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')\"\"$(printf '\\xa9')post\n", "3C7072653E3C3E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')$missing$(printf '\\xa9')post\n", "3C7072653E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' $(printf '\\xc3')$(printf '\\xa9')\"\"\n", "3C3E3C3E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3\\xa9')post\n", "3C7072653E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')$(printf '\\xa9\\xc3')$(printf '\\xa9')post\n", "3C7072653E3C3E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')post\n", "3C7072653E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xa9')post\n", "3C7072653E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')$(printf '\\xa9')post\n", "3C7072653E3C706F73743E")]
    [InlineData("IFS=�; printf '%s' $(printf '\\xc3')$(printf '\\xa9')\n", "C3A9")]
    [InlineData("IFS=é; printf '<%s>' pre$'\\303'$(printf '\\xa9')post\n", "3C707265C33E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')$'\\251'post\n", "3C7072653E3CA9706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xff')post\n", "3C707265FF706F73743E")]
    [InlineData("IFS=é; printf '<%s>' preé$(printf '\\xc3\\xa9')post\n", "3C707265C33E3C3E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')épost\n", "3C7072653E3CC33E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre\"$(printf '\\xc3')\"$(printf '\\xa9')post\n", "3C707265C33E3C706F73743E")]
    [InlineData("IFS=é; printf '<%s>' pre$(printf '\\xc3')\"$(printf '\\xa9')\"post\n", "3C7072653E3CA9706F73743E")]
    public async Task Evaluation_PreservesByteFieldBoundaries(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }
}
