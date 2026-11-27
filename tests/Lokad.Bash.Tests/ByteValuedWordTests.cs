using Lokad.Bash.Evaluation;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ByteValuedWordTests
{
    [Fact]
    public async Task QuotedParameterRetainsItsBytesAcrossBindingReplacement()
    {
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(string.Empty));
        evaluation.Memory.Budget = owner;
        var backing = new byte[16 * 1024];
        Array.Fill(backing, (byte)'x');
        backing[0] = 0xff;
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("x", ShellValue.FromBytes(backing)));
        var scope = new ScopedEvaluation(evaluation, variables, default);
        var tokenizer = new Tokenizer(ShellSource.FromText("\"$x\""));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var word = Assert.IsType<WordToken>(token).Word;

        using (evaluation.Memory.BeginCommand(scope))
        {
            var initial = evaluation.Memory.Workspace.Bytes;
            var expanded = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);
            var delta = evaluation.Memory.Workspace.Bytes - initial;
            Assert.True(expanded.Bytes.Equals(backing.AsMemory()));
            Assert.InRange(delta, backing.Length, backing.Length + 4096L);

            using (evaluation.Memory.BeginCommand(scope))
                Assert.True(variables.TrySetScalar("x", ShellValue.FromText("new")));

            Assert.Equal(0xff, expanded.Bytes.Span[0]);
            Assert.True(evaluation.Memory.Workspace.Bytes - variables.EstimatedMemoryBytes >= backing.Length);
        }

        evaluation.Memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignmentJoinsAggregateBytesWithoutKeepingAnotherSegmentList(bool empty)
    {
        const int count = 128;
        var value = ShellValue.FromBytes(new byte[] { 0xff, (byte)'x', 0x0d });
        var values = Enumerable.Range(0, count).Select(index => empty || index % 2 != 0 ? ShellValue.Empty : value).ToArray();
        var expected = new List<byte> { (byte)'[' };
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0)
                expected.Add((byte)' ');
            expected.AddRange(values[index].Bytes.ToArray());
        }
        expected.Add((byte)']');

        var tokenizer = new Tokenizer(ShellSource.FromText("[\"${items[@]}\"]"));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var fieldsWorkspace = await MeasureAsync(join: false);
        var assignmentWorkspace = await MeasureAsync(join: true);

        Assert.InRange(assignmentWorkspace - fieldsWorkspace, 0, 4L * expected.Count + 1024);

        async Task<long> MeasureAsync(bool join)
        {
            var governor = new MemoryGovernor(2 * 1024 * 1024);
            using var owner = governor.CreateScope();
            var evaluation = new Eval(Parser.Parse(string.Empty));
            evaluation.Memory.Budget = owner;
            var variables = Variables.CreateEmpty();
            Assert.True(variables.TryAssignArray("items", values, ArrayKind.Indexed));
            Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
            var scope = new ScopedEvaluation(evaluation, variables, default);
            long delta;
            using (evaluation.Memory.BeginCommand(scope))
            {
                var initial = evaluation.Memory.Workspace.Bytes;
                if (join)
                {
                    var joined = await WordExpansion.ExpandAssignmentValueAsync(token.Word, scope);
                    Assert.Equal(expected.ToArray(), joined.Bytes.ToArray());
                }
                else
                {
                    var fields = await WordExpansion.ExpandWithoutBraceAsync(token.Word, scope,
                        allowWordSplitting: false, ExpansionPurpose.Assignment, TildeExpansion.Assignment);
                    Assert.Equal(count, fields.Count);
                }
                delta = evaluation.Memory.Workspace.Bytes - initial;
            }
            evaluation.Memory.Finish(retainVariables: false);
            Assert.Equal(0, governor.ReservedBytes);
            return delta;
        }
    }

    [Fact]
    public async Task SmallExpandedSliceKeepsIndependentBacking()
    {
        var backing = new byte[32 * 1024];
        var input = backing.AsMemory(1, 3);
        input.Span[0] = 0xff;
        input.Span[1] = (byte)'a';
        input.Span[2] = 0x0d;
        var evaluation = new Eval(Parser.Parse(string.Empty));
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("x", ShellValue.FromBytes(input)));
        var tokenizer = new Tokenizer(ShellSource.FromText("\"$x\""));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));

        var result = await WordExpansion.ExpandAsync(Assert.IsType<WordToken>(token).Word,
            new ScopedEvaluation(evaluation, variables, default), ExpansionPurpose.Scalar);

        Assert.Equal(new byte[] { 0xff, 0x61, 0x0d }, result.Bytes.ToArray());
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(result.Bytes, out var slice));
        Assert.NotNull(slice.Array);
        Assert.InRange(slice.Array.Length, result.Bytes.Length, 2 * result.Bytes.Length);
    }

    [Theory]
    [InlineData(WordPartKind.Unquoted)]
    [InlineData(WordPartKind.DoubleQuoted)]
    public async Task MixedWordPreservesLiteralBytesAcrossAFullScanAndExpansion(WordPartKind kind)
    {
        var prefix = new byte[64 * 1024 + 3];
        Array.Fill(prefix, (byte)'p');
        prefix[64 * 1024 - 1] = 0xc3;
        prefix[64 * 1024] = 0xa9;
        prefix[^1] = 0xff;
        byte[] source = [.. prefix, .. "${x}"u8, .. "end\r"u8];
        byte[] expected = [.. prefix, .. "valueend\r"u8];
        var word = new Word([WordPart.FromSource(kind, ShellSource.FromBytes(source))]);
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(string.Empty));
        evaluation.Memory.Budget = owner;
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("x", ShellValue.FromText("value")));
        var scope = new ScopedEvaluation(evaluation, variables, default);

        using (evaluation.Memory.BeginCommand(scope))
        {
            var result = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);
            Assert.Equal(expected, result.Bytes.ToArray());
        }
        evaluation.Memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task SplitFieldsKeepTheirBytesAfterThePendingBufferIsReused()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("x=$'alpha \\377omega'; y='beta zeta'; printf '<%s>' $x\"/\"$y",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. "<alpha><"u8, 0xff, .. "omega/beta><zeta>"u8];
        Assert.Equal(expected, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

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
    [InlineData("f() { caller 0 $'\\377'; }; g() { f; }; g", "0 g\n", "")]
    [InlineData("pushd /tmp >/dev/null; popd -- $'\\377'", "/\n", "")]
    [InlineData("hash $'\\377'; printf '%s/after' \"$?\"", "1/after", "")]
    [InlineData("A=1 builtin hash \"$(printf '\\377')\"; printf '%s/after' \"$?\"", "1/after", "")]
    [InlineData("x=0; command hash \"$((x+=1))\" \"$(printf side >&2; printf '\\377')\"; printf '%s/%s' \"$?\" \"$x\"", "1/1", "side")]
    [InlineData("hash ''; printf '%s' \"$?\"", "1", "")]
    [InlineData("set --; hash \"$@\"; printf '%s' \"$?\"", "hash: hash table empty\n0", "")]
    [InlineData("unset missing; hash $missing; printf '%s' \"$?\"", "hash: hash table empty\n0", "")]
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
    [InlineData("dirs -c -- $'\\377' >/dev/null; printf after", "after", 0)]
    [InlineData("pushd -n /tmp $'\\377' >/dev/null; dirs", "/ /tmp\n", 0)]
    public async Task IgnoredTrailingOperands_DoNotRequireText(string script, string expected, int expectedExitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree(["/tmp"], []));

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Fact]
    public async Task ShiftCountsByteValuedSurplusOperand()
    {
        var result = await EvaluationTestHelper.RunAsync("set -- a b; shift 1 $'\\377'; printf '%s' \"$1\"");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("line 1: shift: too many arguments\n", result.StdErr);
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("pushd")]
    public async Task DirectoryNavigationRejectsSurplusWithoutDecodingOrSkippingOperandEffects(string command)
    {
        await using var host = EndToEndHarness.BuildTree(["/tmp"], []);
        var result = await host.RunAsync(
            "{ " + command + " /tmp $'\\377' \"$(printf 'reached\\n' >&2)\"; printf '%s:%s' \"$?\" \"$PWD\"; } 2>/errors",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1:/", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal($"reached\nline 1: {command}: too many arguments\n", host.ReadFile("/errors"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PopdReportsAByteValuedInvalidOperandWithoutChangingTheStack()
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "pushd /tmp >/dev/null; popd $'\\377'; printf '%s:%s\\n' \"$?\" \"$PWD\"; dirs",
            EndToEndHarness.BuildTree(["/tmp"], []));
        byte[] expected =
        [
            .. "line 1: popd: "u8, 0xff,
            .. ": invalid argument\npopd: usage: popd [-n] [+N | -N]\n"u8
        ];

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("2:/tmp\n/tmp /\n", result.StdOut);
        Assert.Equal(expected, result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task CallerReportsTheOriginalBytesOfAnInvalidIndex()
    {
        var result = await EndToEndHarness.RunScriptAsync("f() { caller $'\\377'; }; f", new TestHost());
        byte[] expected = [.. "line 1: caller: "u8, 0xff, .. ": invalid number\ncaller: usage: caller [expr]\n"u8];

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(expected, result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task DirsPreservesDeferredPathAndDiagnosticBytes()
    {
        var result = await EndToEndHarness.RunScriptAsync("""
            pushd -n $'home\377/leaf\376' >/dev/null
            HOME=$'home\377' dirs -p
            dirs -c $'\377'
            printf '%s\n' "$?"
            dirs -l +1
            """, new TestHost());

        byte[] expectedOutput = [.. "/\n~/leaf"u8, 0xfe, .. "\n2\nhome"u8, 0xff, .. "/leaf"u8, 0xfe, (byte)'\n'];
        byte[] expectedError = [.. "line 3: dirs: "u8, 0xff, .. ": invalid option\ndirs: usage: dirs [-clpv] [+N] [-N]\n"u8];
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(expectedError, result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task DirsUsesMalformedHomePrefixOnlyForDeferredPaths()
    {
        var host = new TestHost();
        host.AddDirectory("/home");
        var result = await EndToEndHarness.RunScriptAsync("""
            cd /home
            pushd -n $'/home\377/leaf' >/dev/null
            HOME=$'/home\377' dirs -p
            """, host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/home\n~/leaf\n"u8.ToArray(), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
        Assert.Equal(0, result.Host.OpenFileCount);
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

    [Theory]
    [InlineData("$value\"*\"", "FF,622A")]
    [InlineData("{pre,post}$value\"*\"", "707265FF,622A,706F7374FF,622A")]
    public async Task ParameterFieldsKeepOpaqueBytesAndQuotedSuffixAcrossBraceSelection(string source, string expectedHex)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var evaluation = new Eval(Parser.Parse(string.Empty));
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("value", ShellValue.FromBytes(new byte[] { 0xff, (byte)' ', (byte)'b' })));
        var scope = new ScopedEvaluation(evaluation, variables, default);

        var expanded = await WordExpansion.ExpandToWordsAsync(Assert.IsType<WordToken>(token).Word, scope);
        Assert.Equal(expectedHex.Split(','), expanded.Select(field => Convert.ToHexString(field.ToValue(evaluation.Memory).Bytes.Span)));
        Assert.All(expanded, field => Assert.False(GlobPattern.RequiresExpansion(field)));
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
        var unsplit = await WordExpansion.ExpandWithoutBraceAsync(
            word, scope, allowWordSplitting: false, ExpansionPurpose.Scalar, TildeExpansion.Leading);
        Assert.Same(first, second);
        Assert.Same(first, unsplit);
        var segment = Assert.Single(Assert.Single(unsplit).Segments);
        Assert.True(segment.Content.Bytes.Equals(bytes.AsMemory()));
        Assert.Equal(kind != WordPartKind.Unquoted, segment.IsGlobQuoted);

        evaluation.SetFatalExpansionError(ExpansionFailureKind.NamerefDefault);
        Assert.Empty(await WordExpansion.ExpandToWordsAsync(word, scope));
        Assert.Empty(await WordExpansion.ExpandWithoutBraceAsync(word, scope, false, ExpansionPurpose.Scalar, TildeExpansion.Leading));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("name=value:path", true)]
    [InlineData("~", false)]
    [InlineData("name:~", false)]
    [InlineData("name=~", false)]
    [InlineData("name~tail:~", false)]
    [InlineData("name~tail=~", false)]
    [InlineData("name~tail{1,2}", false)]
    [InlineData("name~tail$value", false)]
    [InlineData("name~~tail~", true)]
    public void UnquotedLiteralFieldsRespectExpansionMarkers(string text, bool literal)
    {
        var part = WordPart.FromText(WordPartKind.Unquoted, text);
        var word = new Word([part]);

        if (literal)
        {
            var fields = Assert.IsAssignableFrom<IReadOnlyList<ExpandedWord>>(word.LiteralFields);
            var segment = Assert.Single(Assert.Single(fields).Segments);
            Assert.Equal(part.GetUnexpandedValue().Bytes, segment.Content.Bytes);
            Assert.False(segment.IsGlobQuoted);
        }
        else
            Assert.Null(word.LiteralFields);
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
        var cursor = 0;
        Assert.True(pattern.TryReadPathComponent(ref cursor, out var component));
        Assert.Equal(0..2, component);
        var mask = pattern.GetMask(component);
        Assert.Equal(0, mask[0]);
        Assert.Equal(GlobMask.Active, mask[1]);
        Assert.False(pattern.TryReadPathComponent(ref cursor, out _));
    }
}
