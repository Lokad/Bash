using System.Text.Json;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed partial class TildePrefixTests
{
    [Theory]
    [MemberData(nameof(NativeCases))]
    public async Task PrefixRolesPreserveNativeBytesAndCompleteTrees(
        string name, string script, string output, string error, int status, string? source, string tree, bool prepared)
    {
        Assert.NotEmpty(name);
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        if (source != null) host.AddFile("/unit", source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, deadline.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, deadline.Token);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, Convert.ToHexString(host.StandardOutput.Content.Span));
        Assert.Equal(error, host.StandardError.TextContent);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(tree) ?? throw new InvalidOperationException();
        var actual = host.CaptureEntries().ToDictionary(p => p.Key, p => p.Value.Kind + ":" + p.Value.ContentHex);
        Assert.Equal(expected.OrderBy(p => p.Key, StringComparer.Ordinal), actual.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(0, host.OpenFileCount);
        if (!prepared) Assert.Equal(0, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("~\"\"", new[] { WordPartKind.Unquoted, WordPartKind.DoubleQuoted })]
    [InlineData("~''", new[] { WordPartKind.Unquoted, WordPartKind.SingleQuoted })]
    [InlineData("~\\/x", new[] { WordPartKind.Unquoted, WordPartKind.Escaped, WordPartKind.Unquoted })]
    [InlineData("~$(printf '')", new[] { WordPartKind.Unquoted, WordPartKind.CommandSubstitution })]
    [InlineData("~/\"\"", new[] { WordPartKind.Unquoted, WordPartKind.DoubleQuoted })]
    public void ParserRetainsPrefixProvenanceIncludingEmptyQuotes(string source, WordPartKind[] kinds)
    {
        var token = new Tokenizer(ShellSource.FromText(source)).NextToken();
        Assert.True(token.TryGetWordToken(out var parsed));
        var word = Assert.IsType<WordToken>(parsed).Word;

        Assert.Equal(kinds, word.Parts.Select(p => p.Kind));
        Assert.Equal(source.StartsWith("~/", StringComparison.Ordinal) ? "~/" : "~",
            word.Parts[0].GetUnexpandedValue().ToUtf8String());
        if (source.EndsWith("\"\"", StringComparison.Ordinal) || source.EndsWith("''", StringComparison.Ordinal))
            Assert.True(word.Parts[^1].GetUnexpandedValue().IsEmpty);
    }

    [Theory]
    [InlineData(new[] { "~", "/tail" }, "home/tail")]
    [InlineData(new[] { "~", "+", "/tail" }, "logical/tail")]
    [InlineData(new[] { "~", "+" }, "logical")]
    [InlineData(new[] { "", "~", "-", "/tail" }, "previous/tail")]
    [InlineData(new[] { "~", "", "/tail" }, "home/tail")]
    public async Task AdjacentUnquotedSourcePartsSelectTheCompletePrefix(string[] sources, string expected)
    {
        var word = new Word(sources.Select(s => WordPart.FromText(WordPartKind.Unquoted, s)).ToArray());
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("HOME", ShellValue.FromText("home")));
        Assert.True(variables.TrySetScalar("PWD", ShellValue.FromText("logical")));
        Assert.True(variables.TrySetScalar("OLDPWD", ShellValue.FromText("previous")));
        var evaluation = new Eval(Parser.Parse(""));
        try
        {
            var scope = new ScopedEvaluation(evaluation, variables, new Eval.EvaluationContext(0, null, Eval.ReturnTargetKind.None));
            var fields = await WordExpansion.ExpandToWordsAsync(word, scope);

            Assert.Equal(ShellValue.FromText(expected), Assert.Single(fields).ToValue(evaluation.Memory));
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
    }

    [Fact]
    public async Task AnIneligiblePrefixDoesNotDetachAnUnreachedHomeArray()
    {
        var caller = Variables.CreateEmpty();
        Assert.True(caller.TryAssignArray("HOME", Enumerable.Repeat(ShellValue.FromText("home"), 4096).ToArray(), ArrayKind.Indexed));
        var original = caller.IndexedArrays["HOME"];
        var governor = new MemoryGovernor(caller.EstimatedMemoryBytes + 16 * 1024);
        using var callerStorage = governor.Reserve(caller.EstimatedMemoryBytes);
        var evaluation = new Eval(Parser.Parse(""), EvaluationDescriptorSetup.Standard,
            publishNextStep: null, onComplete: null, EvaluationOptions.Default, [], governor, sourceMemory: null);
        try
        {
            var variables = Variables.CreateCommandScope(caller, evaluation.Memory, CancellationToken.None);
            var token = new Tokenizer(ShellSource.FromText("~\"\"")).NextToken();
            Assert.True(token.TryGetWordToken(out var parsed));
            var word = Assert.IsType<WordToken>(parsed).Word;
            var scope = new ScopedEvaluation(evaluation, variables, new Eval.EvaluationContext(0, null, Eval.ReturnTargetKind.None));
            IReadOnlyList<ExpandedWord> fields = [];

            var failure = await Record.ExceptionAsync(async () => { fields = await WordExpansion.ExpandToWordsAsync(word, scope); });

            Assert.Null(failure);
            Assert.Equal(ShellValue.FromText("~"), Assert.Single(fields).ToValue(evaluation.Memory));
            Assert.Same(original, caller.IndexedArrays["HOME"]);
            Assert.Equal(4096, original.Count);
            Assert.Throws<MemoryBudgetExceededException>(() => variables.TryGetScalar("HOME", out _));
        }
        finally { evaluation.Memory.Finish(retainVariables: false); }
        Assert.Equal(callerStorage.Bytes, governor.ReservedBytes);
        callerStorage.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
