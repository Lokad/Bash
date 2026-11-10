using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ParameterEscapeTransformTests
{
    // Isolated Git Bash 5.2.37 files; strip only recorded diagnostic location prefixes.
    [Theory]
    [InlineData("x='a\\tb\\nc\\rD\\a\\b\\e\\E\\f\\v'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<a\tb\nc\rD\u0007\b\u001b\u001b\f\u000b>\nafter\n", "", 0)]
    [InlineData("x='a\\\\b\\\"c\\'\\''d\\?e'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<a\\b\"c'd?e>\nafter\n", "", 0)]
    [InlineData("x='\\q\\z\\x\\u\\U'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<\\q\\z\\x\\u\\U>\nafter\n", "", 0)]
    [InlineData("x='\\1017\\0101\\0007tail'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<A7\b1>\nafter\n", "", 0)]
    [InlineData("x='\\x41F\\x4Z\\x00tail'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<AF\u0004Z>\nafter\n", "", 0)]
    [InlineData("x='\\u03bb\\U0001f642'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<λ🙂>\nafter\n", "", 0)]
    [InlineData("x='\\303\\251'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<é>\nafter\n", "", 0)]
    [InlineData("x='\\cA\\c?\\c@tail'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<\u0001>\nafter\n", "", 0)]
    [InlineData("x='left\\c'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<left\\c>\nafter\n", "", 0)]
    [InlineData("x='left\\'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<left\\>\nafter\n", "", 0)]
    [InlineData("x='${missing}$(printf bad >&2)`printf bad >&2`$((1+2))'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n", "<${missing}$(printf bad >&2)`printf bad >&2`$((1+2))>\nafter\n", "", 0)]
    [InlineData("f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; unset x; f \"${x@E}\"; x=''; f \"${x@E}\"; f ${x@E}\n", "n=1\n<>\nn=1\n<>\nn=0\n<>\n", "", 0)]
    [InlineData("a=([2]='a\\tb' [5]='' [9]='c\\nd'); IFS=:; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[@]@E}\"; f \"${a[*]@E}\"\n", "n=3\n<a\tb>\n<>\n<c\nd>\nn=1\n<a\tb::c\nd>\n", "", 0)]
    [InlineData("a=('a\\0b' '\\0b' 'c'); f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[@]@E}\"\n", "n=3\n<a>\n<>\n<c>\n", "", 0)]
    [InlineData("x='a\\tb\\nc'; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f ${x@E}; f \"${x@E}\"\n", "n=3\n<a>\n<b>\n<c>\nn=1\n<a\tb\nc>\n", "", 0)]
    [InlineData("a=([2]='a\\tb' [3]='c\\nd'); i=2; p='a[i++]'; printf '<%s>\\n' \"${!p@E}\" \"$i\"; printf '<%s>\\n' \"${a[2]@E}\"\n", "<c\nd>\n<4>\n<a\tb>\n", "", 0)]
    [InlineData("set -- 'a\\tb' '' 'c\\nd'; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${@@E}\"; f \"${*@E}\"\n", "n=3\n<a\tb>\n<>\n<c\nd>\nn=1\n<a\tb  c\nd>\n", "", 0)]
    [InlineData("readonly x='a\\tb'; printf '<%s>\\n' \"${x@E}\" \"$x\"\n", "<a\tb>\n<a\\tb>\n", "", 0)]
    [InlineData("false && printf '<%s>\\n' \"${x@E}\"; x=kept; printf '<%s>\\n' \"${x:-${bad!}}\"; printf after\n", "<kept>\nafter", "", 0)]
    [InlineData("unset x; y='a\\tb'; printf '<%s>\\n' \"${x:-${y@E}}\"; printf '<%s>\\n' \"${x:=${y@E}}\" \"$x\"\n", "<a\tb>\n<a\tb>\n<a\tb>\n", "", 0)]
    [InlineData("x='a\\0b'; printf '<%s>\\n' pre\"${x@E}\"post\n", "<preapost>\n", "", 0)]
    public async Task Evaluation_DecodesParameterEscapes(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Fact]
    public async Task Evaluation_PreservesLiteralCrAndBackslashLf()
    {
        // The LF-only input contract differs from Git Bash's physical-file CR removal.
        const string Script = "x='a\\\nb\rc'; printf '<%s>\\n' \"${x@E}\"; printf 'after\\n'\n";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<a\\\nb\rc>\nafter\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

}
