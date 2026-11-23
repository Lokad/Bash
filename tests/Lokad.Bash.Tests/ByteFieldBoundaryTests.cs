using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteFieldBoundaryTests
{
    [Fact]
    public void EmptyAggregateFieldsRetainTheirMarkerUntilTheyGainBytes()
    {
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        var memory = new EvaluationMemory(governor);
        try
        {
            var builder = new ExpandedWordBuilder(Variables.CreateEmpty(), memory,
                ExpansionPurpose.Normal, ImplicitEmptyPolicy.Preserve);
            builder.AppendFields([ShellValue.Empty, ShellValue.FromText("value"), ShellValue.Empty],
                allowWordSplitting: false, globQuoted: true, ExpansionQuoteContext.DoubleQuoted);
            builder.AppendSource(ShellSource.FromText("tail"), globQuoted: false);

            Assert.Collection(builder.Build(),
                empty =>
                {
                    Assert.True(empty.IsEmptyAggregateField);
                    var segment = Assert.Single(empty.Segments);
                    Assert.True(segment.Content.IsEmpty);
                    Assert.True(segment.IsGlobQuoted);
                },
                value =>
                {
                    Assert.False(value.IsEmptyAggregateField);
                    Assert.Equal(ShellValue.FromText("value"), Assert.Single(value.Segments).Content);
                },
                extended =>
                {
                    Assert.False(extended.IsEmptyAggregateField);
                    Assert.Collection(extended.Segments,
                        prefix =>
                        {
                            Assert.True(prefix.Content.IsEmpty);
                            Assert.True(prefix.IsGlobQuoted);
                        },
                        tail =>
                        {
                            Assert.Equal(ShellValue.FromText("tail"), tail.Content);
                            Assert.False(tail.IsGlobQuoted);
                        });
                });
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void SplitRunsReuseScratchWithinTheSameWord()
    {
        const int RunCount = 16;
        var body = new string('x', 8192);
        var input = ShellValue.FromText(" " + body + " ");
        var marker = ShellValue.FromText("|");
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        var memory = new EvaluationMemory(governor);
        try
        {
            var builder = new ExpandedWordBuilder(Variables.CreateEmpty(), memory,
                ExpansionPurpose.Normal, ImplicitEmptyPolicy.Preserve);
            for (var index = 0; index < RunCount; index++)
            {
                builder.AppendExpansion(input, allowWordSplitting: true, globQuoted: false);
                builder.AppendValue(marker, globQuoted: true);
            }

            memory.ReleaseUnusedCapacity();
            // Admit split output and one scratch window while the input builders remain live.
            using var held = governor.Reserve(governor.LimitBytes - governor.ReservedBytes - 768 * 1024);
            var fields = builder.Build();
            Assert.Equal(2 * RunCount, fields.Count);
            for (var index = 0; index < fields.Count; index++)
            {
                var segment = Assert.Single(fields[index].Segments);
                Assert.Equal(index % 2 == 0 ? body : "|", segment.Content.ToUtf8String());
                Assert.Equal(index % 2 != 0, segment.IsGlobQuoted);
            }
        }
        finally { memory.Finish(retainVariables: false); }
        Assert.Equal(0, governor.ReservedBytes);
    }

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
        builder.AppendValue(ShellValue.FromText("pre"), globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xc3 }),
            allowWordSplitting: true, globQuoted: false);
        builder.AppendExpansion(ShellValue.FromBytes(new byte[] { 0xa9 }),
            allowWordSplitting: true, globQuoted: true);
        builder.AppendValue(ShellValue.FromText("post"), globQuoted: false);

        Assert.Equal(new[] { "pre", "post" }, builder.Build().Select(word => word.ToValue(budget).ToUtf8String()));
    }

    [Fact]
    public void CaseFieldSelectionKeepsAValidCharacterContainingTheDelimiterByte()
    {
        var variables = Variables.CreateInitial(
            new Dictionary<BindingName, ShellValue> { ["IFS"] = ShellValue.FromBytes(new byte[] { 0xa9 }) }, []);
        var governor = new MemoryGovernor(TestMemoryBudget.EvaluationBytes);
        using (var owner = governor.CreateScope())
        {
            var memory = new EvaluationMemory(owner);
            var builder = new ExpandedWordBuilder(variables, memory, ExpansionPurpose.CasePattern, ImplicitEmptyPolicy.Preserve);
            builder.AppendFields([ShellValue.FromBytes(new byte[] { 0xc3, 0xa9, 0xa9, (byte)'x' })],
                allowWordSplitting: false, globQuoted: false, ExpansionQuoteContext.Unquoted, AtFieldSource.Projected);

            Assert.Equal(new[] { "C3A9", "78" }, builder.BuildCasePattern()
                .Select(word => Convert.ToHexString(word.ToValue(memory).Bytes.Span)));
        }
        Assert.Equal(0, governor.ReservedBytes);
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
    [InlineData("IFS=0; printf '[%s]' $((1020)); printf '|[%s]' \"$((1020))\"\n", "5B315D5B325D7C5B313032305D")]
    [InlineData("printf '[%s]' $'\\377'$((320))$'\\376'\n", "5BFF333230FE5D")]
    public async Task Evaluation_PreservesByteFieldBoundaries(string script, string outputHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }
}
