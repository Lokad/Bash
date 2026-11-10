using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class SpecialParameterTests
{
    // Git Bash 5.2.37 script files in an empty environment; only location prefixes are removed.
    [Theory]
    [InlineData("set -- a b c d e f g h i j; printf '<%s>\\n' \"$1\" \"${1}\" \"$10\" \"${10}\" \"${01}\" \"${#1}\" \"${#10}\"\n", "<a>\n<a>\n<a0>\n<j>\n<a>\n<1>\n<1>\n", "", 0)]
    [InlineData("set --; printf '<%s>\\n' \"${#}\" \"${##}\" \"${#@}\" \"${#*}\" \"${#1}\" \"${1:-fallback}\"\n", "<0>\n<1>\n<0>\n<0>\n<0>\n<fallback>\n", "", 0)]
    [InlineData("false; printf '<%s>\\n' \"${?}\" \"$?\" \"${#?}\"\n", "<1>\n<1>\n<1>\n", "", 0)]
    [InlineData("set -- keep; printf '<%s>\\n' \"${1:-$(printf bad >&2)}\" \"${1:+yes}\" \"${2:-fallback}\"\n", "<keep>\n<yes>\n<fallback>\n", "", 0)]
    [InlineData("set -- '' ''; printf '<%s>\\n' \"${@:-fallback}\" \"${@:+yes}\"\n", "<>\n<>\n<yes>\n", "", 0)]
    [InlineData("set -- '' ''; IFS=; printf '<%s>\\n' \"${*:-fallback}\" \"${*:+yes}\"\n", "<fallback>\n<>\n", "", 0)]
    [InlineData("set --; printf '<%s>\\n' \"${@:-a b}\" \"${@+yes}\"\n", "<a b>\n", "", 0)]
    [InlineData("set -- 'a b' c; IFS=:; printf '<%s>\\n' \"${*}\" \"${*:-bad}\" \"${@:-bad}\"\n", "<a b:c>\n<a b:c>\n<a b>\n<c>\n", "", 0)]
    [InlineData("set --; printf '<%s>\\n' \"${1:=$(printf 'effect\\n' >&2; printf value)}\"; printf after\n", "", "$1: cannot assign in this way\n", 1)]
    [InlineData("set -- keep; printf '<%s>\\n' \"${1:=$(printf bad >&2)}\"; printf after\n", "<keep>\nafter", "", 0)]
    [InlineData("set -- ''; printf '<%s>\\n' \"${1:=value}\"; printf after\n", "", "$1: cannot assign in this way\n", 1)]
    [InlineData("set --; printf '<%s>\\n' \"${@:=$(printf 'effect\\n' >&2; printf value)}\"; printf after\n", "", "$@: cannot assign in this way\n", 1)]
    [InlineData("set --; printf '<%s>\\n' \"${1:?stop}\"; printf after\n", "", "1: stop\n", 1)]
    [InlineData("set -- outer; f(){ printf '<%s>\\n' \"${1}\" \"${#}\"; set -- changed; printf '<%s>\\n' \"${1}\"; }; f inner tail; printf '<%s>\\n' \"${1}\" \"${#}\"\n", "<inner>\n<2>\n<changed>\n<outer>\n<1>\n", "", 0)]
    [InlineData("set -- outer; (set -- inner tail; printf '<%s>\\n' \"${1}\" \"${#}\"); printf '<%s>\\n' \"${1}\" \"${#}\"\n", "<inner>\n<2>\n<outer>\n<1>\n", "", 0)]
    [InlineData("set -- value; cat <<EOF\n${1}|$1|${#}\nEOF\n", "value|value|1\n", "", 0)]
    [InlineData("set -- 3 4; ((x=${1}+${#})); printf '<%s>\\n' \"$x\"\n", "<5>\n", "", 0)]
    [InlineData("set -- a; printf '<%s>\\n' \"${999999999999999999999999999999999999:-fallback}\"\n", "<fallback>\n", "", 0)]
    [InlineData("set -- a b c d e; printf '<%s>\\n' \"${#-word}\" \"${#:-word}\" \"${#?word}\"\n", "<5>\n<5>\n<5>\n", "", 0)]
    [InlineData("set --; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${@+yes}\"\n", "n=0\n<>\n", "", 0)]
    [InlineData("set --; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${@:+yes}\"\n", "n=0\n<>\n", "", 0)]
    [InlineData("set -- ''; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${@:+yes}\"\n", "n=1\n<>\n", "", 0)]
    [InlineData("set -- '' ''; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${@:+yes}\"\n", "n=1\n<yes>\n", "", 0)]
    [InlineData("unset a; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[@]+yes}\"\n", "n=0\n<>\n", "", 0)]
    [InlineData("a=(''); f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[@]:+yes}\"\n", "n=1\n<>\n", "", 0)]
    [InlineData("set --; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${*+yes}\"\n", "n=1\n<>\n", "", 0)]
    [InlineData("set --; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"pre${@+yes}post\"\n", "n=1\n<prepost>\n", "", 0)]
    [InlineData("sh -c 'f(){ printf \"<%s>\\n\" \"$0\" \"${0}\" \"${00}\" \"$1\" \"${01}\"; }; f inner' probe-zero outer\n", "<probe-zero>\n<probe-zero>\n<probe-zero>\n<inner>\n<inner>\n", "", 0)]
    public async Task Evaluation_SpecialParametersShareValuesFieldsAndEffects(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }
}
