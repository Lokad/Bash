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
    [InlineData("PWD=logical; printf '<%s>' ~0 ~+00 ~-0", "<logical><logical><logical>")]
    [InlineData("PWD=; printf '<%s>' ~0 ~+0 ~-0", "<><><>")]
    [InlineData("unset PWD; printf '<%s>' ~0 ~+0 ~-0 ~+", "<~0><~+0><~-0></>")]
    [InlineData("PWD=logical; printf '<%s>' ~1 ~-1 ~4294967296 ~-2147483648 ~1x", "<~1><~-1><~4294967296><~-2147483648><~1x>")]
    [InlineData("PWD=logical; printf '<%s>' ~0\"\" ~\\0 ~0\\/tail ~0/\"x y\"", "<~0><~0><~0/tail><logical/x y>")]
    [InlineData("PWD=logical; n=0; printf '<%s>' ~$n ~0$(printf '')", "<~0><~0>")]
    [InlineData("PWD=logical; x=~+0; export y=~-0; printf '<%s>' \"$x\" \"$y\" ${z:-~0} \"${z:-~0}\"", "<logical><logical><logical><~0>")]
    public async Task NumericStackPrefixesPreserveSpellingAndTheTopEntry(string script, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("HOME=home; c=:; a=x\":\"~; b=x\\:~; d=x${c}~; e=x:~\"\":~; printf '<%s>' \"$a\" \"$b\" \"$d\" \"$e\"",
        "<x:~><x:~><x:~><x:~:home>")]
    [InlineData("unset HOME; x=${HOME:=new}:~; printf '<%s>' \"$x\"", "<new:new>")]
    [InlineData("HOME=home; unset v; a[3]=${v:-a:~}:~; printf '<%s>' \"${a[3]}\" x=${v:-a:~}:~",
        "<a:home:home><x=a:~:home>")]
    [InlineData("HOME=home; a=(a:~ ~:~); b=([2]=a:~ [3]=~:~); printf '<%s>' \"${a[@]}\" \"${b[@]}\"",
        "<a:~><home:~><a:home><home:home>")]
    [InlineData("HOME=home; i=0; x=pre:~/$((++i ? i : 0)):~/$((++i)); printf '<%s>' \"$x\" \"$i\"",
        "<pre:home/1:home/2><2>")]
    [InlineData("HOME=home; cat <<< a:~; cat <<EOF\na:~\nEOF", "a:home\na:~\n")]
    [InlineData("HOME=home; v=data; printf '<%s>' ~:a\"b\" ~:a/\"b\" ~:$v ~:$(printf a) ~:$(printf \"a\")",
        "<~:ab><home:a/b><home:$v><home:$(printf a)><~:a>")]
    public async Task ColonDelimitedTildeUsesTheReachedSourceRole(string script, string expected)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task AssignmentTildePreservesInsertedBytesWithoutExpandingThemAgain()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("HOME=$'\\377:~'; x=~:~; printf '%s' \"$x\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("FF3A7E3AFF3A7E", Convert.ToHexString(host.StandardOutput.Content.Span));
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task NumericStackPrefixesSelectTheCurrentChildStackAndRealPaths()
    {
        await using var host = EndToEndHarness.BuildTree(["/a", "/b", "/c"], []);
        var result = await host.RunAsync(
            "cd /a; pushd /b >/dev/null; pushd /c >/dev/null; PWD=logical; "
            + "printf '<%s>' ~0 ~+01 ~2 ~-0 ~-1 ~-002; "
            + "printf '<%s>' \"$(pushd /a >/dev/null; printf '%s' ~1)\" ~1; "
            + "printf selected > ~+1/result", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<logical></b></a></a></b><logical></c></b>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("selected", host.ReadFile("/b/result"));
        Assert.Equal(0, host.OpenFileCount);
    }

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
    [InlineData(new[] { "~", "+000000000000", "00000000", "/tail" }, "logical/tail")]
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

    [Theory]
    [InlineData("~\"\"", "~")]
    [InlineData("~:tail\"\"", "~:tail")]
    public async Task AnIneligiblePrefixDoesNotDetachAnUnreachedHomeArray(string source, string expected)
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
            var token = new Tokenizer(ShellSource.FromText(source)).NextToken();
            Assert.True(token.TryGetWordToken(out var parsed));
            var word = Assert.IsType<WordToken>(parsed).Word;
            var scope = new ScopedEvaluation(evaluation, variables, new Eval.EvaluationContext(0, null, Eval.ReturnTargetKind.None));
            IReadOnlyList<ExpandedWord> fields = [];

            var failure = await Record.ExceptionAsync(async () => { fields = await WordExpansion.ExpandToWordsAsync(word, scope); });

            Assert.Null(failure);
            Assert.Equal(ShellValue.FromText(expected), Assert.Single(fields).ToValue(evaluation.Memory));
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
