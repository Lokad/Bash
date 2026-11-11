using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationParameterOperatorTestsEndToEnd
{
    // Text-pattern operators deliberately reject byte operands instead of adopting byte-glob semantics.
    [Theory]
    [InlineData("a=$'\\377'", "${a%%x}", "pattern removal value must be valid UTF-8")]
    [InlineData("a=(ok $'\\377')", "${a[@]%x}", "pattern removal value must be valid UTF-8")]
    [InlineData("a=($'\\377')", "${a[*]#x}", "pattern removal value must be valid UTF-8")]
    [InlineData("declare -A a; a[key]=$'\\377'", "${a[@]##x}", "pattern removal value must be valid UTF-8")]
    [InlineData("set -- ok $'\\377'", "${@##x}", "pattern removal value must be valid UTF-8")]
    [InlineData("a=(abc def); pattern=$'\\377'", "${a[@]%$pattern}", "pattern must be valid UTF-8")]
    public async Task PatternRemoval_RejectsNonUtf8ThroughTheExpansionErrorPath(
        string setup, string expansion, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(setup
            + "; printf '%s' \"$(printf before >&2)\" \"" + expansion
            + "\" \"$(printf later >&2)\"; printf after");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("before" + expansion + ": " + diagnostic + "\n", result.StdErr);
    }

    [Fact]
    public async Task PatternRemoval_DoesNotValidateUnreachedOperands()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "value=ok; a=($'\\377'); printf '%s' \"${value:-${a[@]%x}}\"");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok", result.StdOut);
        Assert.Empty(result.StdErr);
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
    [InlineData("readonly value=; printf '<%s>\\n' \"${value:=assigned}\"; printf continued", "", "value: readonly variable\n", 1)]
    [InlineData("unset value inner; printf '%s' \"${value:=${inner:?missing}}$(printf continued >&2)\"; printf continued", "", "inner: missing\n", 1)]
    [InlineData("unset value inner; printf '%s' \"${value:-${inner:?missing}}$(printf continued >&2)\"; printf continued", "", "inner: missing\n", 1)]
    [InlineData("unset inner; printf '%s' \"$((${inner:?missing} + 1))$(printf continued >&2)\"; printf continued", "", "inner: missing\n", 1)]
    [InlineData("cd /; IFS=/; unset value; set -- ${value:-~+}; printf 'count=%s' \"$#\"; for item in \"$@\"; do printf ':<%s>' \"$item\"; done; printf '\\n'", "count=1:</>\n", "", 0)]
    public async Task ParameterOperand_EvaluatesLazilyAndStopsOnFatalErrors(
        string script,
        string expectedOutput,
        string expectedError,
        int expectedStatus)
    {
        // Readonly operand failures retain the engine's common fatal status 1;
        // Bash script files use 2 for that case.
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedStatus, result.ExitCode);
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
            Assert.Equal("value: word\n", result.StdErr);
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
