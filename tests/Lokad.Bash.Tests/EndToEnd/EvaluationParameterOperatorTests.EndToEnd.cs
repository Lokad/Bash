using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationParameterOperatorTestsEndToEnd
{
    [Fact]
    public async Task ParameterQuotesUseTheShortApostropheSpellingAndReplayIt()
    {
        var result = await EvaluationTestHelper.RunAsync(
            """v=$'\x27'; q=${v@A}; v=reset; eval "$q"; printf '<%s:%s:%s>' "$q" "${v@Q}" "$v" """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<v=\\':\\':'>", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Pattern operations preserve shell bytes and allow later operands to execute.
    [Theory]
    [InlineData("a=$'\\377'", "${a%%x}", "FF6166746572")]
    [InlineData("a=(ok $'\\377')", "${a[@]%x}", "6F6BFF6166746572")]
    [InlineData("a=($'\\377')", "${a[*]#x}", "FF6166746572")]
    [InlineData("declare -A a; a[key]=$'\\377'", "${a[@]##x}", "FF6166746572")]
    [InlineData("set -- ok $'\\377'", "${@##x}", "6F6BFF6166746572")]
    [InlineData("a=(abc def); pattern=$'\\377'", "${a[@]%$pattern}", "6162636465666166746572")]
    public async Task PatternRemoval_PreservesBytesAndLaterEffects(
        string setup, string expansion, string stdoutHex)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; printf '%s' \"$(printf before >&2)\" \"" + expansion
            + "\" \"$(printf later >&2)\"; printf after");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(stdoutHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal("beforelater", result.StdErr);
    }

    [Fact]
    public async Task PatternRemoval_DoesNotExpandUnreachedOperands()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "value=ok; a=($'\\377'); printf '%s' \"${value:-${a[@]%$(printf unexpected >&2)}}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("#", "a[0]=771,a[3]=772", "<71><4><72>")]
    [InlineData("##", "a[0]=771,a[3]=772", "<><4><>")]
    [InlineData("%", "a[0]=177,a[3]=277", "<17><4><27>")]
    [InlineData("%%", "a[0]=177,a[3]=277", "<1><4><2>")]
    public async Task PatternRemoval_EnumeratesUpdatedArrayAfterExpandingThePatternOnce(
        string patternOperator, string mutation, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(713 4); p='7*'; n=0; printf '<%s>' \"${a[@]" + patternOperator
            + "${p:0:$((" + mutation + ",n+=2))}}\"; printf '/%s' \"$n\"");

        Assert.Equal(expected + "/2", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=([9]=711); IFS=:", "a[*]", "a[9]=79,a[2]=72", "<2:9>")]
    [InlineData("a=(711 8); declare -n ref=a", "ref[@]", "a[0]=77,a[3]=79", "<7><8><9>")]
    [InlineData("a=(711 8); ref='a[@]'", "!ref", "a[0]=77,a[3]=79", "<7><8><9>")]
    [InlineData("declare -A a=([key]=711)", "a[@]", "a[key]=77,a[new]=77", "<7><7>")]
    [InlineData("a=711", "a", "a=77", "<11>")]
    [InlineData("a=711", "a[@]", "a=77", "<11>")]
    public async Task PatternRemoval_UsesTheSelectedParameterKind(
        string setup, string parameter, string mutation, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; p=7; printf '<%s>' \"${" + parameter + "#${p:0:$((" + mutation + ",1))}}\"");

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset a", "a", "<x><><y>", "")]
    [InlineData("a=", "a", "<x><><y>", "")]
    [InlineData("a=('')", "a[0]", "<x><><y>", "")]
    [InlineData("a=('')", "a[@]", "<x><><y>", "pattern")]
    [InlineData("a=", "a[*]", "<x><><y>", "pattern")]
    [InlineData("a=()", "a[@]", "<x><y>", "")]
    [InlineData("unset a", "a[*]", "<x><><y>", "")]
    [InlineData("declare -A a=()", "a[@]", "<x><y>", "")]
    [InlineData("set --", "@", "<x><y>", "")]
    [InlineData("set -- ''", "@", "<x><><y>", "pattern")]
    public async Task PatternRemoval_SkipsEmptyScalarsAndZeroFieldAggregates(
        string setup, string parameter, string expectedOutput, string expectedError)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; printf '<%s>' x \"${" + parameter + "%%$(printf pattern >&2)}\" y");

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task PatternRemoval_EmptyArraySkipsAFatalPattern()
    {
        var result = await EvaluationTestHelper.RunAsync(
            """a=(); unset missing; printf '<%s>' x "${a[@]#${missing:?unreached}}" y""");

        Assert.Equal("<x><y>", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("${value:-${inner:-}}", "<x><y>")]
    [InlineData("${value:-${inner:-$missing}}", "<x><y>")]
    [InlineData("${value:-${inner:-\"$@\"}}", "<x><y>")]
    [InlineData("${value:-${inner:-$(printf '')}}", "<x><y>")]
    [InlineData("${value:-${inner:+}}", "<x><y>")]
    [InlineData("${value:-${inner:=}}", "<x><y>")]
    [InlineData("\"${value:-${inner:-}}\"", "<x><><y>")]
    [InlineData("${value:-${inner:-\"\"}}", "<x><><y>")]
    public async Task NestedEmptyOperands_PreserveOnlyExplicitlyQuotedArguments(string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "unset value inner missing; set --; printf '<%s>' x " + operand + " y");

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    public async Task PatternRemoval_DoesNotSpendTheWordAllowanceOnPathComponents(string value)
    {
        var pattern = string.Concat(Enumerable.Repeat("a/", 66000));
        var result = await EvaluationTestHelper.RunAsync(
            "value='" + value + "'; pattern='" + pattern + "'; "
            + "printf '<%s>' \"${value#$pattern}\" \"${value##$pattern}\" \"${value%$pattern}\" \"${value%%$pattern}\"");

        Assert.Equal(string.Concat(Enumerable.Repeat("<" + value + ">", 4)), result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterOperatorWords_RespectInnerQuotes()
    {
        // Inner quotes protect default/alternate words, but an assigned scalar
        // is split according to the outer context when emitted.
        var script = @"unset first third; second=set; set -- ${first:-""default value""} ${second:+""alternate value""} ${third:=""assigned value""}; printf '%s:<%s>:<%s>:<%s>:<%s>\n' ""$#"" ""$1"" ""$2"" ""$3"" ""$third""; unset first; set -- ${first:-""""}; value=prefixfix; printf '%s:<%s>:<%s>\n' ""$#"" ""$1"" ${value%""fix""}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("4:<default value>:<alternate value>:<assigned>:<assigned value>\n1:<>:<prefix>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterOperatorWord_PreservesNestedDoubleQuotedExpansion()
    {
        var script = @"unset value; inner='two words'; set -- ""${value:-""$inner""}""; printf '%s:<%s>\n' ""$#"" ""$1""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<two words>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterOperatorWord_StarUsesFirstIfsCharacter()
    {
        var script = @"unset value; set -- a b; printf 'default=<%s>\n' ""${value:-$*}""; IFS=:; printf 'custom=<%s>\n' ""${value:-$*}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("default=<a b>\ncustom=<a:b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("=")]
    [InlineData(":=")]
    public async Task Evaluation_ParameterAssignDefault_JoinsWordsIntoAssignedScalar(string parameterOperator)
    {
        var script = $@"set -- 'one x' two; unset value; printf 'expanded=<%s>\n' ""${{value{parameterOperator}""$@""}}""; printf 'stored=<%s>\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("expanded=<one x two>\nstored=<one x two>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("=")]
    [InlineData(":=")]
    public async Task Evaluation_ParameterAssignDefault_PreservesUnsplitValue(string parameterOperator)
    {
        var script = $@"IFS=:; source='a:b'; unset value; set -- ${{value{parameterOperator}$source}}; printf 'expanded=%s:<%s>:<%s> stored=<%s>\n' ""$#"" ""$1"" ""$2"" ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("expanded=2:<a>:<b> stored=<a:b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterPatternRemoval_DoesNotSplitPattern()
    {
        var script = @"value='abc d'; pattern='c d'; set -- ${value%$pattern}; printf '%s:<%s>:<%s>\n' ""$#"" ""$1"" ""${2-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<ab>:<unset>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterOperatorWords_PreserveSingleQuotesInsideOuterDoubleQuotes()
    {
        var script = @"unset value; printf '<%s>:<%s>\n' ""${value:-'literal'}"" ""${value:-'}'}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<'literal'>:<'}'>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterOperatorWords_ScanNestedQuotedExpansion()
    {
        var script = @"unset outer inner; printf '<%s>\n' ""${outer:-""${inner:-""value""}""}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<value>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("count=0; value=set; printf '<%s>:<%s>\\n' \"${value:-$((count+=1))}\" \"$count\"", "<set>:<0>\n", "", 0)]
    [InlineData("count=0; unset value; printf '<%s>:<%s>\\n' \"${value:-$((count+=1))}\" \"$count\"", "<1>:<1>\n", "", 0)]
    [InlineData("count=0; unset value; printf '<%s>:<%s>\\n' \"${value:+$((count+=1))}\" \"$count\"", "<>:<0>\n", "", 0)]
    [InlineData("count=0; value=set; printf '<%s>:<%s>\\n' \"${value:+$((count+=1))}\" \"$count\"", "<1>:<1>\n", "", 0)]
    [InlineData("count=0; unset value; printf '<%s>:<%s>:<%s>\\n' \"${value:=$((count+=1))}\" \"$value\" \"$count\"", "<1>:<1>:<1>\n", "", 0)]
    [InlineData("unset value; printf '<%s>\\n' \"${value:-$(printf once >&2; printf 'two words')}\"", "<two words>\n", "once", 0)]
    [InlineData("value=set; printf '<%s>\\n' \"${value:-$(printf unused >&2; printf 'two words')}\"", "<set>\n", "", 0)]
    [InlineData("readonly value=; printf '<%s>\\n' \"${value:=assigned}\"; printf continued", "", "line 1: value: readonly variable\n", 2)]
    [InlineData("unset value inner; printf '%s' \"${value:=${inner:?missing}}$(printf continued >&2)\"; printf continued", "", "line 1: inner: missing\n", 1)]
    [InlineData("unset value inner; printf '%s' \"${value:-${inner:?missing}}$(printf continued >&2)\"; printf continued", "", "line 1: inner: missing\n", 1)]
    [InlineData("unset inner; printf '%s' \"$((${inner:?missing} + 1))$(printf continued >&2)\"; printf continued", "", "line 1: inner: missing\n", 1)]
    [InlineData("cd /; IFS=/; unset value; set -- ${value:-~+}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:</>\n", "", 0)]
    public async Task ParameterOperand_EvaluatesLazilyAndStopsOnFatalErrors(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedStatus)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedStatus, result.ExitCode);
    }

    [Fact]
    public async Task ContinuedParameterDefaultReportsTheLocationOfBothSubscriptFailures()
    {
        var result = await EvaluationTestHelper.RunAsync(
            ":\ntarget=(old)\n: \\\n \"${target[-4]:=new}\"\nprintf '<%s:%s>' \"$?\" \"$target\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2:old>", result.StdOut);
        Assert.Equal("line 4: target: bad array subscript\nline 4: target[-4]: bad array subscript\n", result.StdErr);
    }

    public static TheoryData<string, string, string, string, int> ConditionalOperators =>
        new()
        {
            { "unset value", "-", "word", "unset", 0 },
            { "value=", "-", "", "", 0 },
            { "value=set", "-", "set", "set", 0 },
            { "unset value", ":-", "word", "unset", 0 },
            { "value=", ":-", "word", "", 0 },
            { "value=set", ":-", "set", "set", 0 },
            { "unset value", "+", "", "unset", 0 },
            { "value=", "+", "word", "", 0 },
            { "value=set", "+", "word", "set", 0 },
            { "unset value", ":+", "", "unset", 0 },
            { "value=", ":+", "", "", 0 },
            { "value=set", ":+", "word", "set", 0 },
            { "unset value", "=", "word", "word", 0 },
            { "value=", "=", "", "", 0 },
            { "value=set", "=", "set", "set", 0 },
            { "unset value", ":=", "word", "word", 0 },
            { "value=", ":=", "word", "word", 0 },
            { "value=set", ":=", "set", "set", 0 },
            { "unset value", "?", "", "unset", 1 },
            { "value=", "?", "", "", 0 },
            { "value=set", "?", "set", "set", 0 },
            { "unset value", ":?", "", "unset", 1 },
            { "value=", ":?", "", "", 1 },
            { "value=set", ":?", "set", "set", 0 },
        };

    [Theory]
    [MemberData(nameof(ConditionalOperators))]
    public async Task ConditionalOperatorMatrix_DistinguishesUnsetEmptyAndNonEmpty(
        string setup,
        string parameterOperator,
        string expectedExpansion,
        string expectedStoredValue,
        int expectedOperationStatus)
    {
        var script = $@"{setup}; printf 'result=<%s>\n' ""${{value{parameterOperator}word}}""; operation_status=$?; printf 'status=<%s> value=<%s>\n' ""$operation_status"" ""${{value-unset}}""";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOperationStatus, result.ExitCode);
        if (expectedOperationStatus != 0)
        {
            Assert.Empty(result.StdOut);
            Assert.Equal("line 1: value: word\n", result.StdErr);
            return;
        }

        Assert.Equal($"result=<{expectedExpansion}>\nstatus=<0> value=<{expectedStoredValue}>\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("unset value; set -- ${value:-a b}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=2:<a>:<b>\n")]
    [InlineData("unset value; set -- \"${value:-}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("unset value; set -- ${value:-}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=0\n")]
    [InlineData("unset value missing; set -- ${value:-$missing}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=0\n")]
    [InlineData("unset value; set --; set -- \"${value:-\"$@\"}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("unset value; set --; set -- ${value:-\"$@\"}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=0\n")]
    [InlineData("unset value; set -- '' 'a b' c; set -- \"pre${value:-\"$@\"}post\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=3:<pre>:<a b>:<cpost>\n")]
    [InlineData("unset value; set -- \"${value:+word}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("value=; set -- \"${value:+word}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("unset value; set -- ${value:=\"two words\"}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=2:<two>:<words>\n")]
    [InlineData("unset value; set -- '' 'a b' c; set -- ${value:=\"$@\"}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=3:<a>:<b>:<c>\n")]
    [InlineData("unset value; set -- '' 'a b' c; set -- \"${value:=\"$@\"}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:< a b c>\n")]
    [InlineData("value='abc*'; pattern='*'; set -- ${value%\"$pattern\"}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<abc>\n")]
    [InlineData("value='abc*'; pattern='*'; set -- \"${value%$pattern}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<abc*>\n")]
    [InlineData("value='a/b.txt'; set -- \"${value%/*}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<a>\n")]
    [InlineData("value='a b'; set -- a b; set -- \"${value%$@}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("value='a b'; set -- a b; set -- \"${value%\"$@\"}\"; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<>\n")]
    [InlineData("unset value; set -- a b; IFS=:; set -- ${value:-\"$*\"}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:<a:b>\n")]
    [InlineData("unset value\ncat <<EOF\n${value:-'literal'}\n${value:-\"quoted\"}\nEOF\n", "'literal'\nquoted\n")]
    [InlineData("set -- a b; unset value; x=pre${value:-\"$@\"}post; printf '<%s>' \"$x\"", "<prea bpost>")]
    [InlineData("set -- a b; value=set; x=\"pre${value:+\"$@\"}post\"; printf '<%s>' \"$x\"", "<prea bpost>")]
    [InlineData("set -- '' a ''; unset value inner; x=pre${value:-${inner:-$@}}post; printf '<%s>' \"$x\"", "<pre a post>")]
    [InlineData("set -- '' a ''; unset value inner; printf '<%s>' ${value:=${inner:-$@}}; printf 'stored=<%s>' \"$value\"", "<a>stored=< a >")]
    [InlineData("set -- a b; IFS=:; unset value; x=pre${value:-\"$@\"}post; printf '<%s>' \"$x\"", "<prea bpost>")]
    [InlineData("set -- a b; unset value; declare x=pre${value:-\"$@\"}post; printf '<%s>' \"$x\"", "<prea bpost>")]
    [InlineData("set -- a b; unset value; cat <<EOF\nbefore:${value:-\"$@\"}:after\nsecond line\nEOF\n", "before:a b:after\nsecond line\n")]
    [InlineData("set -- '' a ''; unset value; cat <<EOF\nbefore:${value:-$@}:after\nsecond line\nEOF\n", "before: a :after\nsecond line\n")]
    [InlineData("set -- a b; unset value; cat <<<pre${value:-\"$@\"}post", "prea bpost\n")]
    [InlineData("unset value; printf '<%s>' \"${value:-\\}}\"", "<}>")]
    [InlineData("unset value; printf '<%s>' \"${value:=\\}}\" \"$value\"", "<}><}>")]
    [InlineData("value=set; printf '<%s>' \"${value:+\\}}\"", "<}>")]
    [InlineData("unset value inner; printf '<%s>' \"${value:-${inner:-\\}}}\"", "<}>")]
    [InlineData("unset value; printf '<%s>' \"${value:-\\q\\'}\" \"\\}\"", "<\\q\\'><\\}>")]
    public async Task ParameterOperand_PreservesFieldAndPatternBoundaries(
        string script,
        string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
