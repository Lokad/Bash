using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ParameterTransformTests
{
    [Theory]
    [InlineData("^")]
    [InlineData("^^")]
    [InlineData("@U")]
    public async Task UnchangedCaseConversionUsesOrdinaryExpansionWorkspace(string operation)
    {
        var subject = new string('A', 16 * 1024);
        var plain = await MeasureAsync("\"$x\"");
        var converted = await MeasureAsync($"\"${{x{operation}}}\"");
        Assert.InRange(converted - plain, 0, 4096);

        async Task<long> MeasureAsync(string source)
        {
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            var evaluation = new Eval(Parser.Parse(string.Empty));
            evaluation.Memory.Budget = owner;
            var variables = Variables.CreateEmpty();
            Assert.True(variables.TrySetScalar("x", ShellValue.FromText(subject)));
            var scope = new ScopedEvaluation(evaluation, variables, default);
            var tokenizer = new Tokenizer(ShellSource.FromText(source));
            Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
            var word = Assert.IsType<WordToken>(token).Word;

            long workspace;
            using (evaluation.Memory.BeginCommand(scope))
            {
                var before = evaluation.Memory.Workspace.Bytes;
                var result = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);
                workspace = evaluation.Memory.Workspace.Bytes - before;
                Assert.Equal(subject, result.ToUtf8String());
            }
            evaluation.Memory.Finish(retainVariables: false);
            Assert.Equal(0, governor.ReservedBytes);
            return workspace;
        }
    }

    [Fact]
    public async Task UnchangedArrayCaseConversionUsesOrdinaryFieldWorkspace()
    {
        var values = Enumerable.Range(0, 512).Select(index => ShellValue.FromText($"ITEM {index}")).ToArray();
        var plain = await MeasureArrayFieldsAsync("\"${items[@]}\"", values, CheckFields);
        var converted = await MeasureArrayFieldsAsync("\"${items[@]^^}\"", values, CheckFields);

        Assert.InRange(converted - plain, 0, 4096);

        void CheckFields(IReadOnlyList<ExpandedWord> words)
        {
            Assert.Equal(values.Length, words.Count);
            for (var index = 0; index < values.Length; index++)
            {
                var field = Assert.Single(words[index].Segments);
                Assert.True(field.IsGlobQuoted);
                Assert.Equal(values[index], field.Content);
            }
        }
    }

    [Fact]
    public async Task JoinedEscapeConversionRetainsOnlyEmittedWorkspace()
    {
        const int count = 128;
        const int prefixLength = 256;
        var raw = ShellValue.FromText(new string('A', prefixLength) + @"\377");
        var decodedBytes = new byte[prefixLength + 1];
        decodedBytes.AsSpan(0, prefixLength).Fill((byte)'A');
        decodedBytes[^1] = 0xff;
        var decoded = ShellValue.FromBytes(decodedBytes);
        var plain = await MeasureArrayFieldsAsync("\"${items[*]}\"",
            Enumerable.Repeat(decoded, count).ToArray(), CheckJoined);
        var converted = await MeasureArrayFieldsAsync("\"${items[*]@E}\"",
            Enumerable.Repeat(raw, count).ToArray(), CheckJoined);

        Assert.InRange(converted - plain, 0, 4096);

        void CheckJoined(IReadOnlyList<ExpandedWord> words)
        {
            var field = Assert.Single(Assert.Single(words).Segments);
            Assert.True(field.IsGlobQuoted);
            var bytes = field.Content.Bytes.Span;
            Assert.Equal(count * (decoded.Bytes.Length + 1) - 1, bytes.Length);
            for (var index = 0; index < count; index++)
            {
                if (index != 0)
                    Assert.Equal((byte)':', bytes[index * (decoded.Bytes.Length + 1) - 1]);
                Assert.True(bytes.Slice(index * (decoded.Bytes.Length + 1), decoded.Bytes.Length)
                    .SequenceEqual(decoded.Bytes.Span));
            }
        }
    }

    [Theory]
    [InlineData("@")]
    [InlineData("*")]
    public async Task PositionalAssignmentRetainsOnlyEmittedWorkspace(string aggregate)
    {
        const string prefix = "set -- ";
        var padding = new string('x', 250);
        var values = Enumerable.Range(0, 128).Select(index => ShellValue.FromText($"{index:D3} {padding}")).ToArray();
        var rendered = values.Select(value => ShellValue.FromText($"'{value.ToUtf8String()}'")).ToArray();
        var plain = await MeasureFieldsAsync($"\"$prefix${aggregate}\"", variables =>
        {
            variables.PositionalParameters = rendered;
            Assert.True(variables.TrySetScalar("prefix", ShellValue.FromText(prefix)));
        }, CheckFields);
        var converted = await MeasureFieldsAsync($"\"${{{aggregate}@A}}\"", variables => variables.PositionalParameters = values,
            CheckFields);

        Assert.InRange(converted - plain, 0, 4096);

        void CheckFields(IReadOnlyList<ExpandedWord> words)
        {
            if (aggregate == "*")
            {
                var segment = Assert.Single(Assert.Single(words).Segments);
                Assert.True(segment.IsGlobQuoted);
                Assert.Equal(prefix + string.Join(':', rendered.Select(value => value.ToUtf8String())), segment.Content.ToUtf8String());
                return;
            }
            Assert.Equal(rendered.Length, words.Count);
            for (var index = 0; index < words.Count; index++)
            {
                var segment = Assert.Single(words[index].Segments);
                Assert.True(segment.IsGlobQuoted);
                Assert.Equal((index == 0 ? prefix : string.Empty) + rendered[index].ToUtf8String(), segment.Content.ToUtf8String());
            }
        }
    }

    private static Task<long> MeasureArrayFieldsAsync(
        string source, IReadOnlyList<ShellValue> values, Action<IReadOnlyList<ExpandedWord>> check) =>
        MeasureFieldsAsync(source, variables => Assert.True(variables.TryAssignArray("items", values, ArrayKind.Indexed)), check);

    private static async Task<long> MeasureFieldsAsync(
        string source, Action<Variables> prepare, Action<IReadOnlyList<ExpandedWord>> check)
    {
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(string.Empty));
        evaluation.Memory.Budget = owner;
        var variables = Variables.CreateEmpty();
        prepare(variables);
        Assert.True(variables.TrySetScalar("IFS", ShellValue.FromText(":")));
        var scope = new ScopedEvaluation(evaluation, variables, default);
        var tokenizer = new Tokenizer(ShellSource.FromText(source));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));

        long workspace;
        using (evaluation.Memory.BeginCommand(scope))
        {
            var before = evaluation.Memory.Workspace.Bytes;
            var words = await WordExpansion.ExpandToWordsAsync(token.Word, scope);
            workspace = evaluation.Memory.Workspace.Bytes - before;
            check(words);
        }
        evaluation.Memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
        return workspace;
    }

    [Theory]
    [InlineData("""${x//b/"$r"}""", true)]
    [InlineData("""${x//z/"$r"}""", false)]
    public async Task QuotedReplacementWorkspaceFollowsEmittedBytes(string expansion, bool matched)
    {
        const int width = 16 * 1024;
        const string subject = "b";
        var replacement = new string('x', width - 1) + "&";
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(string.Empty));
        evaluation.Memory.Budget = owner;
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("x", ShellValue.FromText(subject)));
        Assert.True(variables.TrySetScalar("r", ShellValue.FromText(replacement)));
        var scope = new ScopedEvaluation(evaluation, variables, default);
        var tokenizer = new Tokenizer(ShellSource.FromText("\"" + expansion + "\""));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var word = Assert.IsType<WordToken>(token).Word;

        using (evaluation.Memory.BeginCommand(scope))
        {
            var baseline = evaluation.Memory.Workspace.Bytes;
            var result = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);
            Assert.Equal(matched ? replacement : subject, result.ToUtf8String());
            var workspace = evaluation.Memory.Workspace.Bytes - baseline;
            // Operand masks and backing are still needed in both cases. Only a match keeps output backing.
            Assert.InRange(workspace, 0, (matched ? 6L : 4L) * width);
        }
        evaluation.Memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task EscapeDecodingKeepsThePlainByteValue()
    {
        var bytes = new byte[16 * 1024];
        bytes.AsSpan().Fill(0xff);
        var original = ShellValue.FromBytes(bytes);
        var governor = new MemoryGovernor(4 * 1024 * 1024);
        using var owner = governor.CreateScope();
        var evaluation = new Eval(Parser.Parse(string.Empty));
        evaluation.Memory.Budget = owner;
        var variables = Variables.CreateEmpty();
        Assert.True(variables.TrySetScalar("x", original));
        var scope = new ScopedEvaluation(evaluation, variables, default);
        var tokenizer = new Tokenizer(ShellSource.FromText("\"${x@E}\""));
        Assert.True(tokenizer.NextToken().TryGetWordToken(out var token));
        var word = Assert.IsType<WordToken>(token).Word;

        using (evaluation.Memory.BeginCommand(scope))
        {
            var result = await WordExpansion.ExpandAsync(word, scope, ExpansionPurpose.Scalar);
            Assert.True(original.Bytes.Equals(result.Bytes));
        }
        evaluation.Memory.Finish(retainVariables: false);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueQuotingAdmitsTheActualRenderedBytesBeforeOpeningOutput(bool denseEscapes)
    {
        var governor = new MemoryGovernor(512 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var state = new Eval(Parser.Parse("""printf before; : "${value@Q}" >effect"""));
        var bytes = new byte[32 * 1024];
        bytes.AsSpan().Fill(denseEscapes ? (byte)0xff : (byte)'x');
        state.SetVariable("value", ShellValue.FromBytes(bytes));

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(denseEscapes ? 1 : 0, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal(denseEscapes, host.StandardError.TextContent.Contains("shared memory", StringComparison.Ordinal));
        Assert.Equal(!denseEscapes, host.CaptureEntries().ContainsKey("effect"));
        Assert.Equal(bytes, state.Variables["value"].Bytes.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("""x=; declare y; export z; show "${x@A}" "${y@A}" "${z@A}" "${missing@A}" """,
        "<4>[x=''][][declare -x z][]\n")]
    [InlineData("""a=([4]='two words'); readonly a; show "${a@a}" "${a[9]@a}" "${a[@]@a}"; show "${a[4]@A}" "${a[9]@A}" """,
        "<3>[ar][ar][ar]\n<2>[declare -ar a='two words'][declare -ar a]\n")]
    [InlineData("""a=('two words'); show "pre${a[@]@A}post"; IFS=:; show "${a[@]@A}"; show "${a[*]@A}" """,
        "<3>[predeclare][-a][a=([0]=\"two words\")post]\n<1>[declare -a a=([0]=\"two words\")]\n<1>[declare -a a=([0]=\"two words\")]\n")]
    [InlineData("""declare -a a; show "${a[@]@a}"; show "${a[@]@A}"; a=(); show "${a[@]@a}"; show "${a[@]@A}"; show "${a[@]@K}" """,
        "<1>[a]\n<3>[declare][-a][a]\n<0>\n<3>[declare][-a][a=()]\n<0>\n")]
    [InlineData("""a=([4]='two words' [12]=''); d="${a[*]@A}"; unset a; eval "$d"; show "${a[4]}" "${a[12]+present}" "${!a[@]}" """,
        "<4>[two words][present][4][12]\n")]
    [InlineData("""a=([9223372036854775807]=last [2]='' [10]=ten [0]=zero); show "${!a[@]}"; IFS=:; show "${!a[*]}"; show "${a[@]@k}"; show "${a[@]@K}" """,
        "<4>[0][2][10][9223372036854775807]\n<1>[0:2:10:9223372036854775807]\n<8>[0][zero][2][][10][ten][9223372036854775807][last]\n<1>[0 \"zero\" 2 \"\" 10 \"ten\" 9223372036854775807 \"last\"]\n")]
    [InlineData("""a=([4]='two words' [9]=''); show "${a[@]@K}"; show "pre${a[@]@k}post"; show pre${a[@]@k}post""",
        "<1>[4 \"two words\" 9 \"\"]\n<4>[pre4][two words][9][post]\n<5>[pre4][two][words][9][post]\n")]
    [InlineData("""declare -A m=(['b key']='two words' ['a key']=''); show "${m[@]@K}"; show "${m[@]@k}"; show "${m[*]@A}" """,
        "<1>[\"a key\" \"\" \"b key\" \"two words\" ]\n<4>[a key][][b key][two words]\n<1>[declare -A m=([\"a key\"]=\"\" [\"b key\"]=\"two words\" )]\n")]
    [InlineData("""a=([4]='two words' [9]=''); IFS=:; x="${a[@]@k}"; show "$x"; [[ ${a[@]@k} == '4 two words 9 ' ]] && show match""",
        "<1>[4:two words:9:]\n<1>[match]\n")]
    [InlineData("""a=(one two ''); IFS=:; x="${a[@]@a}"; show "$x"; show "${a[@]@a}"; show "${a[*]@a}" """,
        "<1>[a:a:a]\n<3>[a][a][a]\n<1>[a:a:a]\n")]
    [InlineData("""set -- 'first value' ''; show "${@@A}"; IFS=:; show "${*@A}"; show "${@@a}" """,
        "<4>[set][--]['first value']['']\n<1>[set -- 'first value':'']\n<2>[][]\n")]
    [InlineData("""a=(zero one two); declare -n r='a[i++]'; i=0; show "${r@A}" "$i"; i=0; show "${!r@A}" "$i"; i=0; p='a[i++]'; show "${!p@A}" "$i" """,
        "<2>[][1]\n<2>[declare -a a='zero'][1]\n<2>[declare -a a='one'][2]\n")]
    [InlineData("""export x=value; declare -n r=x; show "${r@A}" "${!r@A}" "${!r@K}" """,
        "<3>[declare -x x='value'][declare -x x='x']['x']\n")]
    public async Task InspectionTransformsRetainSupportedFields(string script, string expected)
    {
        const string Show = """show(){ printf '<%s>' "$#"; for arg; do printf '[%s]' "$arg"; done; printf '\n'; }; """;
        var result = await EndToEndHarness.RunScriptAsync(Show + script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task InspectionTransformsUseSpaceJoinedFieldsInHereDocuments()
    {
        const string Script = "a=(one 'two words'); set -- 'first value' last; IFS=:; cat <<EOF\n"
            + "${a[@]@k}\n${a[*]@k}\n${@@A}\n${*@A}\nEOF\n";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0 one 1 two words\n0 one 1 two words\nset -- 'first value' 'last'\nset -- 'first value' 'last'\n",
            result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task KeyValueTransformsKeepMalformedUtf8AtTheByteBoundary()
    {
        const string Script = """a=([4]=$'x\377y' [9]=''); printf '[%s]' "${a[@]@K}" "${a[@]@k}" """;
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString("5B34202427785C333737792720392022225D5B345D5B78FF795D5B395D5B5D"),
            result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationRenderingAdmitsExpandedBytesBeforeOpeningOutput(bool denseEscapes)
    {
        var governor = new MemoryGovernor(512 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new TestHost { MemoryBudget = owner };
        var state = new Eval(Parser.Parse("""printf before; : "${value@A}" >effect"""));
        var bytes = new byte[32 * 1024];
        bytes.AsSpan().Fill(denseEscapes ? (byte)0xff : (byte)'x');
        state.SetExportedVariable("value", ShellValue.FromBytes(bytes));

        var result = await new Engine().RunAsync(state, host, owner, CancellationToken.None);

        Assert.Equal(denseEscapes ? 1 : 0, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal(denseEscapes, host.StandardError.TextContent.Contains("shared memory", StringComparison.Ordinal));
        Assert.Equal(!denseEscapes, host.CaptureEntries().ContainsKey("effect"));
        Assert.Equal(bytes, state.Variables["value"].Bytes.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("a=abc", "1|<>|2;0|<>|2")]
    [InlineData("a=(abc)", "1|<>|2;0|<>|2")]
    [InlineData("a=(abc '')", "1|<''>|2;1|<''>|2")]
    [InlineData("a=([1]=abc)", "1|<>|1;0|<>|1")]
    [InlineData("declare -A a=([0]=abc)", "1|<>|2;0|<>|2")]
    public async Task IndirectQuoteDistinguishesUnsetAndEmptySelections(string setup, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; p='a[$((i++))]'; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; "
            + "i=0; f \"${!p@Q}\"; printf '|%s;' \"$i\"; i=0; f ${!p@Q}; printf '|%s' \"$i\"");

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Reconciled from the saved Bash 5.2.37 parameter-operator cases.
    [Theory]
    [InlineData("x=abcabc; printf '<%s>\\n' \"${x/a/Z}\" \"${x//a/Z}\" \"${x/#a/Z}\" \"${x/%c/Z}\"\n", "<Zbcabc>\n<ZbcZbc>\n<Zbcabc>\n<abcabZ>\n", "", 0)]
    [InlineData("x=abcabc; printf '<%s>\\n' \"${x/a*c/Z}\" \"${x//?/Z}\" \"${x//a/\\&}\" \"${x//a/&X}\"\n", "<Z>\n<ZZZZZZ>\n<&bc&bc>\n<aXbcaXbc>\n", "", 0)]
    [InlineData("x='a/b'; printf '<%s>\\n' \"${x/\\//X}\" \"${x/'a'/Z}\"\n", "<aXb>\n<Z/b>\n", "", 0)]
    [InlineData("x=abC; printf '<%s>\\n' \"${x^}\" \"${x^^}\" \"${x,}\" \"${x,,}\" \"${x^^[ab]}\"\n", "<AbC>\n<ABC>\n<abC>\n<abc>\n<ABC>\n", "", 0)]
    [InlineData("x=\"a'b\"; printf '<%s>\\n' \"${x@Q}\"; unset x; printf 'argc='; f(){ printf '%s\\n' \"$#\"; }; f \"${x@Q}\"\n", "<'a'\\''b'>\nargc=1\n", "", 0)]
    [InlineData("x=''; printf '<%s>\\n' \"${x/*/X}\" \"${x//*/X}\"; x=abc; printf '<%s>\\n' \"${x//x*/X}\" \"${x//a*/X}\"\n", "<X>\n<X>\n<abc>\n<X>\n", "", 0)]
    [InlineData("unset x; printf '<%s>\\n' \"${x/$(printf bad >&2)/$(printf bad >&2)}\"\n", "<>\n", "", 0)]
    [InlineData("unset x; printf '<%s>\\n' \"${x^^$(printf bad >&2)}\"\n", "<>\n", "", 0)]
    [InlineData("x=abC; printf '<%s>\\n' \"${x@U}\" \"${x@u}\" \"${x@L}\"\n", "<ABC>\n<AbC>\n<abc>\n", "", 0)]
    [InlineData("x=abcabc; p=a; r='&X'; printf '<%s>\\n' \"${x//$p/$r}\" \"${x//$p/\"$r\"}\" \"${x//a/'&X'}\"\n", "<aXbcaXbc>\n<&Xbc&Xbc>\n<&Xbc&Xbc>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p/a/Z}\" \"$i\"\n", "<uvwxyz>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p@Q}\" \"$i\"\n", "<'uvwxyz'>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abc); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p/$(printf pattern >&2; printf a)/$(printf replacement >&2; printf X)}\" \"$i\"\n", "<>\n<4>\n", "patternreplacement", 0)]
    public async Task SavedValueOperators(string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Linux Bash 5.2.21 / C.UTF-8. Keep effects, argument counts and literal replacement bytes observable.
    [Theory]
    [InlineData("x=abc; printf '<%s>' \"${x/#/X}\" \"${x/%/X}\" \"${x//?/X}\" \"${x//*/X}\" \"${x//*/&X}\" \"${x//z*/X}\" \"${x/a*}\"", "<Xabc><abcX><XXX><X><abcX><abc><>")]
    [InlineData("x=ababa; printf '<%s>' \"${x//a?/X}\" \"${x/*b/X}\" \"${x/%a*/X}\" \"${x/#b/X}\" \"${x//[ab]/X}\"", "<XXa><Xa><X><ababa><XXXXX>")]
    [InlineData("x=abc; r='\\&'; printf '<%s>' \"${x/b/$r}\" \"${x/b/\"$r\"}\"; r='\\\\&'; printf '<%s>' \"${x/b/$r}\"", "<a&c><a\\&c><a\\bc>")]
    [InlineData("x='a/b'; p='/'; r='/&/'; printf '<%s>' \"${x/$p/$r}\" \"${x/$(printf /)/X}\" \"${x/a/${missing:-X/Y}}\"", "<a///b><aXb><X/Y/b>")]
    [InlineData("x='*ab'; p='*'; printf '<%s>' \"${x/\"$p\"/X}\" \"${x/'*'/X}\" \"${x/\\*/X}\" \"${x/$p/X}\"", "<Xab><Xab><Xab><X>")]
    [InlineData("x='abc'; printf '<%s>' \"${x//a/}\" \"${x//a}\" \"${x//}\" \"${x/}\"", "<bc><bc><abc><abc>")]
    [InlineData("x=''; printf '<%s>' \"${x/*/X}\" \"${x//*/X}\" \"${x/#/X}\" \"${x/%/X}\" \"${x//?/X}\"", "<X><X><X><X><>")]
    [InlineData("x=abc; printf '<%s>' \"${x^^a}\" \"${x^b}\" \"${x^^''}\" \"${x^^[ab]}\" \"${x@u}\" \"${x@U}\"; x=ABC; printf '<%s>' \"${x,,[AB]}\" \"${x,B}\" \"${x@L}\"", "<Abc><abc><abc><ABc><Abc><ABC><abC><ABC><abc>")]
    [InlineData("x='éλ🙂z'; printf '<%s>' \"${x@U}\" \"${x//?/X}\" \"${x/λ/Y}\" \"${x^^[éλ]}\"", "<ÉΛ🙂Z><XXXX><éY🙂z><ÉΛ🙂z>")]
    [InlineData("x='🙂a'; printf '<%s>' \"${x//[🙂]/X}\" \"${x/%?/X}\" \"${x/#?/X}\"", "<Xa><🙂X><Xa>")]
    [InlineData("a=([2]='a b' [7]='' [9]=cat); IFS=:; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${a[@]//a/X}\"; f \"${a[*]@U}\"; f \"${a[@]@Q}\"; f \"${a[*]@Q}\"", "3|<X b><><cXt>1|<A B::CAT>3|<'a b'><''><'cat'>1|<'a b':'':'cat'>")]
    [InlineData("set -- 'a b' '' c; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${@@Q}\"; f \"${*//?/X}\"; set --; f \"${@@Q}\"; f \"${*@Q}\"", "3|<'a b'><''><'c'>1|<XXX  X>0|<>1|<>")]
    [InlineData("unset x; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f \"${x@Q}\"; x=''; f \"${x@Q}\"; unset a; f \"${a[@]@Q}\"; a=(''); f \"${a[@]@Q}\"", "1|<>1|<''>0|<>1|<''>")]
    [InlineData("x='a b'; f(){ printf '%s|' \"$#\"; printf '<%s>' \"$@\"; }; f ${x@Q}; f \"${x@Q}\"; f pre\"${x@Q}\"post", "2|<'a><b'>1|<'a b'>1|<pre'a b'post>")]
    [InlineData("a=(aa bb); printf '<%s>' \"${a[@]//${a[0]:=x}/X}\" \"${a[@]}\"", "<X><bb><aa><bb>")]
    [InlineData("x=abc; printf '<%s>' \"${x//a/$((i+=1))}\" \"$i\"; unset x; printf '<%s>' \"${x//${y:=a}/${z:=b}}\" \"$y\" \"$z\"", "<1bc><1><><><>")]
    [InlineData("a=(one two three); i=0; p='a[i++]'; printf '%s|%s' \"${!p@U}\" \"$i\"", "TWO|2")]
    [InlineData("readonly x='a b'; q=${x@Q}; eval \"y=$q\"; printf '%s|%s' \"$x\" \"$y\"", "a b|a b")]
    [InlineData("x='/a/b/'; printf '<%s>' \"${x///}\" \"${x////Z}\" \"${x///b/_}\" \"${x///_}\"", "<ab><ZaZbZ></a_/></a/b/>")]
    [InlineData("x='ab/xy'; printf '<%s>' \"${x///?/Z}\" \"${x/#/Z}\" \"${x/%/Z}\" \"${x/#//Z}\" \"${x/%//Z}\"", "<abZy><Zab/xy><ab/xyZ></Zab/xy><ab/xy/Z>")]
    [InlineData("a=('' xyz); printf '<%s>' \"${a[@]//x/${a[0]:=new}}\" \"${a[@]}\"", "<new><newyz><new><xyz>")]
    [InlineData("a=(abc def); printf '<%s>' \"${a[@]//a/$((a[9]=9))}\" \"${a[@]}\"", "<9bc><def><9><abc><def><9>")]
    [InlineData("a=('' xyz); declare -n p=a; printf '<%s>' \"${p[*]//x/${a[0]:=new}}\"", "<new newyz>")]
    [InlineData("a=('' xyz); p='a[@]'; printf '<%s>' \"${!p//x/${a[0]:=new}}\"", "<new><newyz>")]
    [InlineData("declare -A a=([key]=''); printf '<%s>' \"${a[@]//x/${a[key]:=new}}\"", "<new>")]
    [InlineData("a=(abc def); printf '<%s>' \"${a[@]^^$((a[0]=7))}\" \"${a[@]}\"", "<7><def><7><def>")]
    [InlineData("a=(ABC DEF); printf '<%s>' \"${a[*],,${a[9]:=D}}\" \"${a[@]}\"", "<ABC dEF d><ABC><DEF><D>")]
    [InlineData("x=one; printf '<%s>' \"${x//o/$((x=9))}\" \"$x\"", "<9ne><9>")]
    [InlineData("x=abc; r='\\'; printf '<%s>' \"${x/b/$r'&'}\" \"${x/b/$r\\&}\" \"${x/b/$r\"&\"}\" \"${x/b/$r\"$r\"&}\"", "<a\\bc><a\\bc><a\\bc><a\\&c>")]
    [InlineData("x=abc; r='\\'; printf '<%s>' \"${x/b/\"$r\"&}\" \"${x/b/$r\"$r\"}\" \"${x/b/\"$r\"$r'&'}\"", "<a\\bc><a\\\\c><a\\\\bc>")]
    [InlineData("x=abcabc; p='#a'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\"; p='%c'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\"", "<Zbcabc><abcabc><abcabc><abcabZ><abcabc><abcabc>")]
    [InlineData("x='#abc#abc'; p='#a'; printf '<%s>' \"${x/$p/Z}\" \"${x/\"$p\"/Z}\" \"${x//$p/Z}\" \"${x/#$p/Z}\"", "<#abc#abc><Zbc#abc><ZbcZbc><Zbc#abc>")]
    [InlineData("x=abc; p=''; printf '<%s>' \"${x/${p}#a/Z}\" \"${x/${p}%c/Z}\" \"${x/$(printf '#a')/Z}\" \"${x/$'#a'/Z}\" \"${x/\\#a/Z}\"", "<Zbc><abZ><Zbc><abc><abc>")]
    [InlineData("x=abc; p='#'; q='%'; printf '<%s>' \"${x/$p/Z}\" \"${x/$q/Z}\"; p='\\#'; x='#abc'; printf '<%s>' \"${x/$p/Z}\"", "<Zabc><abcZ><Zabc>")]
    [InlineData("x='a*b'; p='\\*'; printf '<%s>' \"${x//$p/X}\" \"${x//\"$p\"/X}\"; x='a\\b'; p='\\\\'; printf '<%s>' \"${x/$p/X}\"", "<aXb><a*b><aXb>")]
    [InlineData("x=aba; p='\\a'; printf '<%s>' \"${x^^$p}\" \"${x^^\"$p\"}\"; x=ABA; p='\\A'; printf '<%s>' \"${x,,$p}\"", "<AbA><aba><aBa>")]
    [InlineData("x='a]b'; p='[\\]]'; printf '<%s>' \"${x//$p/X}\"; x='a-b'; p='[a\\-c]'; printf '<%s>' \"${x//$p/X}\"", "<aXb><XXb>")]
    [InlineData("x='a*b'; p='\\'; printf '<%s>' \"${x/$p*/X}\" \"${x/$p'*'/X}\"; x='a\\*b'; printf '<%s>' \"${x/$p'*'/X}\"", "<aXb><a*b><a\\*b>")]
    [InlineData("x=$'a\\001*b'; p='\\'; printf '<%s>' \"${x/$p'*'/X}\"; x=$'a\\001\\001b'; p=$'\\\\\\001'; printf '<%s>' \"${x/$p/X}\"", "<aX><aXb>")]
    [InlineData("x=abc; p='\\'; printf '<%s>' \"${x/$p/${y:=X}}\" \"$y\" \"${x^^$p}\"; x='a\\b'; printf '<%s>' \"${x/$p/X}\" \"${x/\"$p\"/X}\"", "<abc><X><abc><a\\b><aXb>")]
    [InlineData("a=(abc def); p='\\'; printf '<%s>' \"${a[@]/$p/${a[9]:=X}}\" \"${a[@]}\"", "<abc><def><X><abc><def><X>")]
    [InlineData("x=$'a\\377b'; p=$'\\\\\\377'; printf '<%s>' \"${x/$p/X}\"; x='aλb'; p='\\λ'; printf '<%s>' \"${x/$p/X}\"", "<aXb><aXb>")]
    [InlineData("x='a/b'; p='/'; printf '<%s>' \"${x/$p/Z}\" \"${x/${p}a/Z}\"; x='a#b'; p=''; printf '<%s>' \"${x/$p\\#/Z}\"", "<aZb><a/b><aZb>")]
    [InlineData("x=ababab; p='[a]'; printf '<%s>' \"${x^^$p}\"; p='[b]'; printf '<%s>' \"${x^^$p}\"", "<AbAbAb><aBaBaB>")]
    public async Task ValueOperatorsRetainFieldsAndQuoting(string script, string expectedOutput)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("x=aBc; p=''; printf '<%s>' \"${x^^$p}\" \"${x^$p}\" \"${x^^\"$p\"}\"", "<ABC><ABc><aBc>")]
    [InlineData("unset p; x=AbC; printf '<%s>' \"${x,,$p}\" \"${x,$p}\"", "<abc><abC>")]
    [InlineData("a=(''); x=abc; printf '<%s>' \"${x^^${a[@]}}\" \"${x^^${a[0]}}\"; a=(); printf '<%s>' \"${x^^${a[*]}}\"", "<abc><ABC><ABC>")]
    [InlineData("set --; x=abc; printf '<%s>' \"${x^^\"$@\"}\" \"${x^^$*}\" \"${x^^\"$*\"}\"", "<ABC><ABC><abc>")]
    [InlineData("set -- ''; unset p; x=abc; printf '<%s>' \"${x^^${p:-$p}}\" \"${x^^${p:-$@}}\" \"${x^^${p:-''}}\"", "<ABC><abc><abc>")]
    [InlineData("a=(abc Def ''); IFS=:; p=''; printf '<%s>' \"${a[@]^^$p}\" \"${a[*]^^$p}\"", "<ABC><DEF><><ABC:DEF:>")]
    public async Task CasePatternsDistinguishMissingAndEmptyFields(string script, string expectedOutput)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task EmptyCasePatternCaptureRunsOnceBeforeConversion()
    {
        const string script = "x=abc; printf '<%s>' \"${x^^$(printf first >>seen)}\" \"${x^^\"$(printf second >>seen)\"}\"";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<ABC><abc>", result.StdOut);
        Assert.Equal("firstsecond", await EndToEndHarness.ReadFileAsync(result.Host, "/seen"));
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("x=$'\\377aλ'; printf '%s' \"${x@U}\"", "FF41CE9B")]
    [InlineData("x=$'\\377aλ'; printf '%s|%s' \"${x^}\" \"${x^^}\"", "FF61CEBB7CFF41CE9B")]
    [InlineData("x='aé'; p=$'[\\303][\\251]'; printf '%s' \"${x^^$p}\"", "61C389")]
    [InlineData("x=$'a\\377é'; p='?'; printf '%s' \"${x^^$p}\"", "41FFC389")]
    [InlineData("x='𐐨é'; printf '%s|%s' \"${x^}\" \"${x^^}\"", "F0909080C3A97CF0909080C389")]
    [InlineData("x='AK𐐀'; printf '%s' \"${x,,}\"", "616BF09090A8")]
    [InlineData("x=$'\\377aλ'; printf '%s' \"${x//?/X}\"", "585858")]
    [InlineData("x=$'a\\377b'; printf '%s' \"${x/$'\\377'/$'\\376'}\"", "61FE62")]
    [InlineData("x=$'\\377abc\\376'; a=$'\\377'; b=$'\\376'; printf '<%s><%s><%s>' \"${x#$a}\" \"${x%$b}\" \"${x/$b/X}\"",
        "3C616263FE3E3CFF6162633E3CFF616263583E")]
    [InlineData("x=$'a\\377a\\377a'; printf '<%s><%s>' \"${x//a/X}\" \"${x//$'\\377'/Y}\"",
        "3C58FF58FF583E3C61596159613E")]
    [InlineData("x=$'a\\377b'; printf '%s' \"${x//a/$'\\376&'}\"", "FE26FF62")]
    [InlineData("x=$'\\377'; printf '%s' \"${x@Q}\"", "24275C33373727")]
    [InlineData("x='\\377'; printf '%s' \"${x@E}\"", "FF")]
    [InlineData("x='\\uD800'; printf '%s' \"${x@E}\"", "EDA080")]
    [InlineData("x='\\U00200000'; printf '%s' \"${x@E}\"", "F888808080")]
    [InlineData("x='\\cλ'; printf '%s' \"${x@E}\"", "0EBB")]
    [InlineData("x='\\c🙂'; printf '%s' \"${x@E}\"", "109F9982")]
    [InlineData("x='\\303\\251'; printf '%s' \"${x@E}\"", "C3A9")]
    [InlineData("x='\\0tail'; printf '%s' \"${x@E}\"", "")]
    [InlineData("x='\\U80000000'; printf '%s' \"${x@E}\"", "")]
    [InlineData("x=$'\\377λ\\n'; q=${x@Q}; eval \"y=$q\"; printf '%s' \"$y\"", "FFCEBB0A")]
    [InlineData("x='eλeλeλ'; printf '%s' \"${x^^[e]}\"", "45CEBB45CEBB45CEBB")]
    [InlineData("a=('éaéa' 'ééa'); p=$'[\\303][\\251]'; printf '<%s>' \"${a[@]^^$p}\"", "3CC38961C389613E3CC389C389613E")]
    [InlineData("x=$'a\\377λ\\377a'; p='[aλ]'; printf '%s' \"${x^^$p}\"", "41FFCE9BFF41")]
    public async Task ValueOperatorsPreserveRawBytes(string script, string expectedHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(expectedHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("x=abc; printf '%s' \"${x/${bad:?pattern}/$(printf late >&2)}\"; printf after", "line 1: bad: pattern\n")]
    [InlineData("x=abc; printf '%s' \"${x/$(printf first >&2; printf a)/${bad:?replacement}}\"; printf after", "firstline 1: bad: replacement\n")]
    public async Task FailedOperandsStopLaterEffects(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task UnsuccessfulReplacementCompletesWithoutExecutionGas()
    {
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse("printf '%s' \"${x//*z/Y}\"; printf after"));
        evaluation.SetVariable("x", ShellValue.FromText(new string('a', 2000)));
        var host = EndToEndHarness.WithMemoryBudget(EndToEndHarness.BuildTree([]), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));

        await new Engine().RunAsync(evaluation, host, CancellationToken.None);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(new string('a', 2000) + "after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }
}
