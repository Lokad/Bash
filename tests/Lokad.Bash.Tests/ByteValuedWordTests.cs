using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteValuedWordTests
{
    [Fact]
    public async Task PathnameGlobRejectsNonUtf8ThroughTheExpansionErrorPath()
    {
        var result = await EvaluationTestHelper.RunAsync("printf '%s' $'\\377'*; printf after");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("shell: pathname pattern must be valid UTF-8\n", result.StdErr);
    }

    [Theory]
    [InlineData("true $'\\377'; printf after", "after", "")]
    [InlineData(": $'\\377'; printf after", "after", "")]
    [InlineData("false \"$(printf '\\377')\"; printf '%s' \"$?\"", "1", "")]
    [InlineData("A=1 builtin true $'\\377'; printf after", "after", "")]
    [InlineData("command true $'\\377'; printf after", "after", "")]
    [InlineData("x=0; : \"$((x+=1))\" \"$(printf side >&2; printf '\\377')\"; printf '%s' \"$x\"", "1", "side")]
    [InlineData("pwd $'\\377'", "/\n", "")]
    [InlineData("f() { caller $'\\377'; }; f", "0 f\n", "")]
    [InlineData("pushd /tmp >/dev/null; popd $'\\377'", "/\n", "")]
    [InlineData("hash $'\\377'; printf '%s/after' \"$?\"", "1/after", "")]
    [InlineData("A=1 builtin hash \"$(printf '\\377')\"; printf '%s/after' \"$?\"", "1/after", "")]
    [InlineData("x=0; command hash \"$((x+=1))\" \"$(printf side >&2; printf '\\377')\"; printf '%s/%s' \"$?\" \"$x\"", "1/1", "side")]
    [InlineData("hash ''; printf '%s' \"$?\"", "1", "")]
    [InlineData("set --; hash \"$@\"; printf '%s' \"$?\"", "0", "")]
    [InlineData("unset missing; hash $missing; printf '%s' \"$?\"", "0", "")]
    public async Task IgnoredOperands_ExpandWithoutTextDecoding(string script, string expected, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree(["/tmp"], []));

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Preserve the platform's existing surplus-argument handling, not Bash arity validation.
    [Theory]
    [InlineData("mapfile names $'\\377' </dev/null; printf after", "after", 0)]
    [InlineData("readarray names \"$(printf '\\377')\" </dev/null; printf after", "after", 0)]
    [InlineData("cd /tmp $'\\377'; pwd", "/tmp\n", 0)]
    [InlineData("dirs -c $'\\377' >/dev/null; printf after", "after", 0)]
    [InlineData("pushd /tmp $'\\377' >/dev/null; pwd", "/tmp\n", 0)]
    [InlineData("set -- a b; shift 1 $'\\377'; printf '%s' \"$1\"", "b", 0)]
    public async Task IgnoredTrailingOperands_DoNotRequireText(string script, string expected, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree(["/tmp"], []));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    // Bash 5.2.37: ignored mapfile operands still expand once before the builtin reads input.
    [Theory]
    [InlineData("mapfile")]
    [InlineData("readarray")]
    [InlineData("builtin mapfile")]
    [InlineData("command readarray")]
    public async Task IgnoredTrailingOperands_PreserveExpansionEffects(string command)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=0; " + command + " names \"$((x+=1))$(printf side >&2; printf '\\377')\" </input; printf '%s/%s' \"$x\" \"${names[0]}\"",
            EndToEndHarness.BuildTree([("/input", "data")]));

        Assert.Equal("1/data", result.StdOut);
        Assert.Equal("side", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("{a,b}$'\\377'", new byte[] { 0x61, 0xff }, new byte[] { 0x62, 0xff })]
    [InlineData("$'\\377'{a,b}", new byte[] { 0xff, 0x61 }, new byte[] { 0xff, 0x62 })]
    public async Task BraceExpansionPreservesNonUtf8Literals(string source, byte[] first, byte[] second)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var word = Assert.IsType<WordToken>(token).Word;
        var evaluation = new Evaluation.Evaluation(Parser.Parse(string.Empty));
        var expanded = await WordExpansion.ExpandToWordsAsync(
            word, new ScopedEvaluation(evaluation, Variables.CreateEmpty(), new Evaluation.Evaluation.EvaluationContext(0, null, Evaluation.Evaluation.ReturnTargetKind.None)));

        Assert.Collection(expanded,
            value => Assert.Equal(first, value.ToValue(evaluation.Memory).Bytes.ToArray()),
            value => Assert.Equal(second, value.ToValue(evaluation.Memory).Bytes.ToArray()));
    }

    [Fact]
    public async Task SingleLiteralRetainsBytesAcrossScalarAndFieldExpansion()
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse(string.Empty));
        var bytes = new byte[] { 0xff, 0xfe, 0xfd };
        var word = new Word([WordPart.FromValue(WordPartKind.SingleQuoted, ShellValue.FromBytes(bytes))]);
        var scope = new ScopedEvaluation(evaluation, Variables.CreateEmpty(), new Evaluation.Evaluation.EvaluationContext(0, null, Evaluation.Evaluation.ReturnTargetKind.None));

        var expanded = await WordExpansion.ExpandToWordsAsync(word, scope);
        var scalar = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);

        Assert.True(Assert.Single(expanded).ToValue(evaluation.Memory).Bytes.Equals(bytes.AsMemory()));
        Assert.True(scalar.Bytes.Equals(bytes.AsMemory()));
    }

    [Theory]
    [InlineData(WordPartKind.SingleQuoted)]
    [InlineData(WordPartKind.Escaped)]
    [InlineData(WordPartKind.DoubleQuoted)]
    [InlineData(WordPartKind.Unquoted)]
    public async Task LiteralFieldsReuseSyntaxStorageWithoutSplittingOrLosingGlobQuoting(WordPartKind kind)
    {
        var evaluation = new Evaluation.Evaluation(Parser.Parse(string.Empty));
        var bytes = new byte[] { 0xff, 0x20, 0x0d, (byte)'*' };
        var word = new Word([WordPart.FromSource(kind, ShellSource.FromBytes(bytes))]);
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(" *")));
        var scope = new ScopedEvaluation(evaluation, variables,
            new Evaluation.Evaluation.EvaluationContext(0, null, Evaluation.Evaluation.ReturnTargetKind.None));

        var first = await WordExpansion.ExpandToWordsAsync(word, scope);
        var second = await WordExpansion.ExpandToWordsAsync(word, scope);
        var unsplit = await WordExpansion.ExpandUnsplitWordAsync(word, scope, ExpansionPurpose.Scalar, true);
        Assert.Same(first, second);
        Assert.Same(Assert.Single(first), unsplit);
        var segment = Assert.Single(unsplit.Segments);
        Assert.True(segment.Content.Bytes.Equals(bytes.AsMemory()));
        Assert.Equal(kind != WordPartKind.Unquoted, segment.IsGlobQuoted);

        evaluation.SetFatalExpansionError(ExpansionFailureKind.NamerefDefault);
        Assert.Empty(await WordExpansion.ExpandToWordsAsync(word, scope));
        Assert.Empty(await WordExpansion.ExpandWithoutBraceAsync(word, scope, false, ExpansionPurpose.Scalar, true));
    }

    [Fact]
    public void FlatteningJoinsBytesAndRetainsSingleSegments()
    {
        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        var bytes = new byte[] { 0xff, 0xfe };
        var segment = new ExpandedSegment(ShellValue.FromBytes(bytes), true);

        Assert.Equal(new byte[] { 0xff, 0xfe, 0xff, 0xfe }, new ExpandedWord([segment, segment]).ToValue(budget).Bytes.ToArray());
        Assert.True(new ExpandedWord([segment]).ToValue(budget).Bytes.Equals(bytes.AsMemory()));
        Assert.True(new ExpandedWord([]).ToValue(budget).IsEmpty);
    }

    [Theory]
    [InlineData("$'\\377'", new byte[] { 0xff })]
    [InlineData("$'\\uD800'", new byte[] { 0xed, 0xa0, 0x80 })]
    [InlineData("$'\\U00200000'", new byte[] { 0xf8, 0x88, 0x80, 0x80, 0x80 })]
    [InlineData("$'\\cλ'", new byte[] { 0x0e, 0xbb })]
    [InlineData("$'\\c🙂'", new byte[] { 0x10, 0x9f, 0x99, 0x82 })]
    public async Task LiteralBytes_SurviveTokenizationAndWordExpansion(string source, byte[] expected)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source + " tail"));
        var token = tokenizer.NextToken();
        Assert.True(token.TryGetWordToken(out var wordToken));
        var word = Assert.IsType<WordToken>(wordToken).Word;
        var literal = Assert.IsType<LiteralWordPart>(Assert.Single(word.Parts));
        Assert.Equal(expected, literal.Value.Bytes.ToArray());
        Assert.Equal(ShellSource.FromText(source).Bytes.ToArray(), ShellSource.FromText(source + " tail").Bytes[token.Source].ToArray());

        var evaluation = new Evaluation.Evaluation(Parser.Parse(string.Empty));
        var expanded = await WordExpansion.ExpandToWordsAsync(
            word, new ScopedEvaluation(evaluation, Variables.CreateEmpty(), new Evaluation.Evaluation.EvaluationContext(0, null, Evaluation.Evaluation.ReturnTargetKind.None)));

        Assert.Equal(expected, Assert.Single(expanded).ToValue(evaluation.Memory).Bytes.ToArray());
        Assert.All(Assert.Single(expanded).Segments, segment => Assert.True(segment.IsGlobQuoted));
    }

    [Theory]
    [InlineData("printf '%s' $'\\303'$'\\251'\n", "é")]
    [InlineData("printf before; false && printf '%s' $'\\377'; printf after\n", "beforeafter")]
    [InlineData("f(){ printf '%s' $'\\377'; }; printf ready\n", "ready")]
    [InlineData("printf '%s' pre$'\\303'$'\\251'post\n", "preépost")]
    [InlineData("x=$'\\303'$'\\251'; printf '%s' \"$x\"\n", "é")]
    [InlineData("printf '%s' $'\\303'$'\\251''*'\n", "é*")]
    [InlineData("printf '%s' $'\\303'$'\\251'*\n", "é-one")]
    [InlineData("cat <<$'\\303'$'\\251'\nbody\né\n", "body\n")]
    [InlineData("printf '%s' \\🙂\n", "🙂")]
    [InlineData("OLDPWD=/base; printf '%s' ~-/$'\\303'$'\\251'\n", "/base/é")]
    [InlineData("OLDPWD=/base; x='a b'; printf '<%s>' ~-/\"$x\"\n", "</base/a b>")]
    [InlineData("OLDPWD=/base; printf '<%s>' ~-/\"$(printf 'a b')\"\n", "</base/a b>")]
    public async Task Evaluation_DecodesOnlyAfterLiteralConcatenation(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            script, EndToEndHarness.BuildTree([("/é-one", string.Empty)]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Utf8Text.Encode(expected).ToArray(), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public void ValueEquality_UsesBytesNotBufferIdentity()
    {
        var first = ShellValue.FromBytes(new byte[] { 0xff });
        var second = ShellValue.FromBytes(new byte[] { 0xff });

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first == second);
        Assert.True(first != ShellValue.FromBytes(new byte[] { 0xfe }));
        Assert.True(ShellValue.Empty.IsEmpty);
        Assert.True(ShellValue.Empty.TryGetUtf8Text(out var emptyText));
        Assert.Empty(emptyText);
    }

    [Fact]
    public void TextValue_PreservesUtf8()
    {
        var value = ShellValue.FromText("é🙂");

        Assert.Equal("é🙂"u8.ToArray(), value.Bytes.ToArray());
        Assert.Equal("é🙂", value.ToUtf8String());
        Assert.True(value.TryGetUtf8Text(out var text));
        Assert.Equal("é🙂", text);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("EDA080")]
    [InlineData("C080")]
    public void TextBoundaries_RejectInvalidUtf8WithoutReplacement(string hex)
    {
        var value = ShellValue.FromBytes(Convert.FromHexString(hex));

        Assert.False(value.TryGetUtf8Text(out var text));
        Assert.Empty(text);
        Assert.Throws<System.Text.DecoderFallbackException>(() => value.ToUtf8String());
    }

    [Fact]
    public void TextInspection_ConcatenatesBeforeCheckingUtf8()
    {
        var command = ShellCommandParser.TryParse("printf $'\\303'$'\\251'");

        Assert.True(command.HasValue);
        Assert.Equal("é", Assert.Single(command.Value.Arguments));
    }

    [Fact]
    public void TextInspection_DeclinesNonUtf8Arguments()
    {
        Assert.Null(ShellCommandParser.TryParse("printf $'\\377'"));
    }

    [Fact]
    public void GlobMetadata_SurvivesMultibyteCharactersAcrossQuoteModes()
    {
        var word = new ExpandedWord([
            new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xc3 }), true),
            new ExpandedSegment(ShellValue.FromBytes(new byte[] { 0xa9 }), false),
            new ExpandedSegment(ShellValue.FromText("*"), false)
        ]);

        var budget = new EvaluationMemory(new global::Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.EvaluationBytes));
        Assert.True(GlobPattern.TryFromExpandedWord(word, budget, out var pattern));
        Assert.Equal("é*", pattern.Pattern);
        Assert.Equal(new byte[] { 0, GlobMask.Active }, Assert.Single(pattern.SplitPathSegments(budget)).MetaMask.Memory.ToArray());
    }
}
