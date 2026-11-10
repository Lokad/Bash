using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ParameterSubstringTests
{

    [Theory]
    [InlineData("x=abc; a=(1 2); eval 'printf \"%s\" \"${x:${a[@]:0:2}:1}\"'; printf 'after:%s' \"$?\"", "after:1", "1 2: invalid arithmetic expression\n")]
    [InlineData("x=abc; a=(1 2); eval 'printf \"%s\" \"${x:0:${a[@]:0:2}}\"'; printf 'after:%s' \"$?\"", "after:1", "1 2: invalid arithmetic expression\n")]
    [InlineData("x=abc; set -- 1 2; eval 'printf \"%s\" \"${x:${@:1:2}:1}\"'; printf 'after:%s' \"$?\"", "after:1", "1 2: invalid arithmetic expression\n")]
    [InlineData("a=(1 2); eval 'printf \"%s\" \"$((${a[@]:0:2}))\"'; printf 'after:%s' \"$?\"", "after:1", "1 2: invalid arithmetic expression\n")]
    [InlineData("x=abc; a=('' 1); printf '<%s>' \"${x:${a[@]:0:2}:1}\"", "<b>", "")]
    [InlineData("x=abc; a=(1 2); IFS=:; eval 'printf \"%s\" \"${x:${a[@]:0:2}:1}\"'; printf 'after:%s' \"$?\"", "after:1", "1 2: invalid arithmetic expression\n")]
    [InlineData("x=abc; eval \"printf '%s' \\\"\\${x:1:'2'}\\\"\"; printf 'after:%s' \"$?\"", "after:1", "'2': invalid arithmetic expression\n")]
    [InlineData("x=abc; eval 'printf \"%s\" \"${x:1:\\2}\"'; printf 'after:%s' \"$?\"", "after:1", "\\2: invalid arithmetic expression\n")]
    [InlineData("x=abc; printf '<%s>' \"${x:1:\"2\"}\"", "<bc>", "")]
    [InlineData("printf '<%s>' \"$((\"2\"))\"", "<2>", "")]
    [InlineData("eval \"printf '%s' \\\"\\$(('2'))\\\"\"; printf 'after:%s' \"$?\"", "after:1", "'2': invalid arithmetic expression\n")]
    [InlineData("x=abc; printf '<%s>' \"${x:~0:1}\"", "<c>", "")]
    [InlineData("x=abcdef; a=(1 1); IFS=+; printf '<%s>' \"${x:${a[*]:0:2}:1}\"", "<c>", "")]
    [InlineData("x=abc; printf '<%s>' \"${x:1:$'2'}\"", "<bc>", "")]
    [InlineData("x=abc; n=21; printf '<%s>|<%s>|<%s>' \"$((${n#'2'}))\" \"${x:${n#'2'}:1}\" \"${x:0:${n#'2'}}\"", "<1>|<b>|<a>", "")]
    [InlineData("x=abc; n=21; printf '<%s>|<%s>|<%s>' \"$((${n##'2'}))\" \"${x:${n##'2'}:1}\" \"${x:0:${n##'2'}}\"", "<1>|<b>|<a>", "")]
    [InlineData("x=abc; n=12; printf '<%s>|<%s>|<%s>' \"$((${n%'2'}))\" \"${x:${n%'2'}:1}\" \"${x:0:${n%'2'}}\"", "<1>|<b>|<a>", "")]
    [InlineData("x=abc; n=12; printf '<%s>|<%s>|<%s>' \"$((${n%%'2'}))\" \"${x:${n%%'2'}:1}\" \"${x:0:${n%%'2'}}\"", "<1>|<b>|<a>", "")]
    [InlineData("x=abc; n=21; printf '<%s>' \"${x:${n#'$(printf BAD >&2; printf 2)'}:1}\"", "<>", "")]
    [InlineData("n=21; printf '<%s>' \"$((${n#'$(printf BAD >&2; printf 2)'}))\"", "<21>", "")]
    [InlineData("x=abc; n=21; unset p; printf '<%s>' \"${x:${n#${p:-'2'}}:1}\"", "<b>", "")]
    [InlineData("x=abc; n=21; printf '<%s>' \"${x:${n#\\2}:1}\"", "<b>", "")]
    [InlineData("x=abc; n=21; printf '<%s>' \"${x:${n#$(printf 2)}:$(printf length >&2; printf 1)}\"", "<b>", "length")]
    [InlineData("x=abc; n=21; printf '<%s>' \"${x:${n#'2'}:$(printf length >&2; printf 1)}\"", "<b>", "length")]
    [InlineData("x=abc; n=21; printf '<%s>' \"${x:${n#$((1+1))}:1}\"", "<b>", "")]
    public async Task ArithmeticOperandsRetainFieldsAndSyntax(string script, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("${x:$n:1}", "FF", 0)]
    [InlineData("${x:0:$n}", "FF", 0)]
    [InlineData("$(($n))", "FF", 0)]
    [InlineData("${x:i++,$n:1}", "692B2B2CFF", 1)]
    [InlineData("${x:0:i++,$n}", "692B2B2CFF", 1)]
    [InlineData("$((i++,$n))", "692B2B2CFF", 1)]
    public async Task MalformedArithmeticBytesReportRecoverableErrors(string operand, string expressionHex, int writes)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=abc; n=$'\\377'; i=0; eval 'printf bad\"" + operand + "\"'; printf 'after:%s:%s' \"$?\" \"$i\"",
            EndToEndHarness.BuildTree([]));

        Assert.Equal("after:1:" + writes, result.StdOut);
        Assert.Equal(0, result.ExitCode);
        byte[] expected = [.. Convert.FromHexString(expressionHex), .. ": invalid arithmetic expression\n"u8];
        Assert.Equal(expected, result.Host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("${files[@]:1}", "</two words>\n</three>\n")]
    [InlineData("${files[@]:1:1}", "</two words>\n")]
    [InlineData("${files[@]}", "</one>\n</two words>\n</three>\n")]
    public async Task ArraySliceRetainsFilenameArguments(string expansion, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "files=(/one '/two words' /three); for f in \"" + expansion + "\"; do printf '<%s>\\n' \"$f\"; done");

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("a=([2]=abcdef); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p:1:3}\" \"$i\"\n", "<>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p:1:3}\" \"$i\"\n", "<vwx>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz [4]=MNOPQR); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p:i++:2}\" \"$i\"\n", "<yz>\n<5>\n", "", 0)]
    [InlineData("a=([3]=uvwxyz); p='a[i++]'; i=2; j=0; printf '<%s>\\n' \"${!p:j++:2}\" \"$i\" \"$j\"\n", "<>\n<3>\n<0>\n", "", 0)]
    [InlineData("a=([2]='' [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>|%s' \"${!p:1:3}\" \"$i\"", "<vwx>|4", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>|%s' \"${!p:99:$(printf bad >&2; printf 1)}\" \"$i\"", "<>|4", "", 0)]
    [InlineData("a=(abc def); p='a[$(printf selected >&2; printf 0)]'; printf '<%s>' \"${!p:$(printf offset >&2; printf 1):$(printf length >&2; printf 1)}\"", "<b>", "selectedselectedoffsetlength", 0)]
    [InlineData("a=(abc def); p='a[i++]'; i=0; eval 'printf \"%s\" \"${!p:}\"'; printf 'after:%s' \"$i\"", "after:1", "${!p:}: bad substitution\n", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); i=2; printf '<%s>\\n' \"${a[i++]:i++:2}\" \"$i\"\n", "<de>\n<4>\n", "", 0)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); i=2; declare -n p='a[i++]'; printf '<%s>|%s' \"${p:1:3}\" \"$i\"", "<bcd>|3", "", 0)]
    [InlineData("a=(abcdef uvwxyz); i=0; declare -n r='a[i++]'; declare -n q=r; p=q; printf '<%s>|%s' \"${!p:1:2}\" \"$i\"", "<bc>|1", "", 0)]
    [InlineData("a=(abcdef uvwxyz); i=0; declare -n r='a[i++]'; p=(r); printf '<%s>|%s' \"${!p[@]:1:2}\" \"$i\"", "<bc>|1", "", 0)]
    [InlineData("a=(abcdef uvwxyz); i=0; declare -n r='a[i++]'; p=r; printf '<%s>|%s' \"${!p:i++:i++}\" \"$i\"", "<bc>|3", "", 0)]
    [InlineData("a=(abcdef uvwxyz); i=0; declare -n r=a; p='r[i++]'; printf '<%s>|%s' \"${!p:1:2}\" \"$i\"", "<vw>|2", "", 0)]
    [InlineData("a=([5]=five); printf '<%s>\\n' \"${a[@]:${a[2]:=2}:2}\"; printf 'all=<%s>\\n' \"${a[@]}\"\n", "<2>\n<five>\nall=<2>\nall=<five>\n", "", 0)]
    [InlineData("a=([5]=five); printf '<%s>\\n' \"${a[@]:2:${a[2]:=2}}\"; printf 'all=<%s>\\n' \"${a[@]}\"\n", "<2>\n<five>\nall=<2>\nall=<five>\n", "", 0)]
    [InlineData("a=([2]=two); printf '<%s>\\n' \"${a[@]:${a[9]:=-1}:1}\"; printf 'all=<%s>\\n' \"${a[@]}\"\n", "<-1>\nall=<two>\nall=<-1>\n", "", 0)]
    [InlineData("a=([2]=two); printf '<%s>\\n' \"${a[@]: -1:${a[9]:=1}}\"; printf 'all=<%s>\\n' \"${a[@]}\"\n", "<two>\nall=<two>\nall=<1>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five [9]=nine); p='a[@]'; printf '<%s>\\n' \"${!p:3:1}\"\n", "<five>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five); i=0; printf '<%s>\\n' \"${!a[@]:i++:$(printf bad >&2; printf 2)}\"; printf after\n", "", "two five: invalid variable name\n", 1)]
    [InlineData("files=([2]=/two [5]='/five words' [9]=/nine); for f in \"${files[@]:3:1}\"; do printf '<%s>\\n' \"$f\"; done", "</five words>\n", "", 0)]
    [InlineData("a=([2]='' [9]=last); for f in \"${a[@]:2:1}\"; do printf '<%s>' \"$f\"; done", "<>", "", 0)]
    [InlineData("x=abc; eval 'printf \"${x:}\"'; printf 'after:%s' \"$?\"", "after:1", "${x:}: bad substitution\n", 0)]
    [InlineData("a=([2]=two [5]=five [9]=nine); printf '<%s>\\n' \"${a[@]:3:1}\" \"${a[@]:3:2}\" \"${a[@]:6}\" \"${a[@]:0:2}\"\n", "<five>\n<five>\n<nine>\n<nine>\n<two>\n<five>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five [9]=nine); printf '<%s>\\n' \"${a[@]: -1}\" \"${a[@]: -5:2}\" \"${a[@]: -11:2}\"\n", "<nine>\n<five>\n<nine>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five); printf '<%s>\\n' \"${a[@]:2:-1}\"; printf after\n", "", "-1: substring expression < 0\n", 1)]
    [InlineData("unset a; i=0; printf '<%s>\\n' \"${a[@]:i++:i++}\" \"$i\"; a=(); printf '<%s>\\n' \"${a[@]:i++:i++}\" \"$i\"\n", "<0>\n<0>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five); i=0; printf '<%s>\\n' \"${a[@]:6:i++}\" \"$i\" \"${a[@]:99:i++}\" \"$i\" \"${a[@]: -99:i++}\" \"$i\"\n", "<0>\n<0>\n<0>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five); printf '<%s>\\n' \"${a[@]:6:-1}\"; printf after\n", "<>\nafter", "", 0)]
    [InlineData("set -- one two three; printf '<%s>\\n' \"${@:1:2}\" \"${@:2}\" \"${@:4}\"\n", "<one>\n<two>\n<two>\n<three>\n", "", 0)]
    [InlineData("set -- one two; printf '<%s>\\n' \"${@:1:-1}\"; printf after\n", "", "-1: substring expression < 0\n", 1)]
    [InlineData("set -- one two; i=0; printf '<%s>\\n' \"${@:3:i++}\" \"$i\" \"${@:99:i++}\" \"$i\" \"${@: -99:i++}\" \"$i\"\n", "<1>\n<1>\n<1>\n", "", 0)]
    [InlineData("set -- 'a b' '' c; IFS=:; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${*:2:2}\"; f \"${@:2:2}\"\n", "n=1\n<:c>\nn=2\n<>\n<c>\n", "", 0)]
    [InlineData("x=abcdef; printf '<%s>\\n' \"${x[@]:0:1}\" \"${x[@]:1:1}\" \"${x[@]: -1:1}\" \"${x[@]: -2:1}\"\n", "<a>\n<b>\n<f>\n<e>\n", "", 0)]
    [InlineData("a=([2]=two [5]=five); declare -n p=a; printf '<%s>\\n' \"${p[@]:3:1}\"\n", "<five>\n", "", 0)]
    [InlineData("a=([9223372036854775807]=last); printf '<%s>\\n' \"${a[@]: -1:1}\" \"${a[@]:9223372036854775807:1}\"\n", "<last>\n<last>\n", "", 0)]
    [InlineData("x=abcdef; printf '<%s>\\n' \"${x:2}\" \"${x:1:3}\" \"${x: -2}\" \"${x:1:-1}\"\n", "<cdef>\n<bcd>\n<ef>\n<bcde>\n", "", 0)]
    [InlineData("x=abcdef; i=1; printf '<%s>\\n' \"${x:i++:i++}\" \"$i\"\n", "<bc>\n<3>\n", "", 0)]
    [InlineData("unset x; i=1; printf '<%s>\\n' \"${x:$(printf effect >&2; printf 1):i++}\" \"$i\"\n", "<>\n<1>\n", "", 0)]
    [InlineData("x=abcdef; printf '<%s>\\n' \"${x:1?2:3:2}\" \"${x:(1?2:3):2}\"\n", "<cd>\n<cd>\n", "", 0)]
    [InlineData("x=abc; i=0; printf '<%s>\\n' \"${x:99:i++}\" \"$i\" \"${x: -99:i++}\" \"$i\"\n", "<>\n<0>\n<>\n<0>\n", "", 0)]
    [InlineData("x=abc; printf '<%s>\\n' \"${x:2:-2}\"; printf after\n", "", "-2: substring expression < 0\n", 1)]
    [InlineData("a=(abcdef); printf '%s' \"${a[0]:1:3}\"", "bcd", "", 0)]
    [InlineData("a=(); a[2]=''; a[9]=last; for f in \"${a[@]:2:1}\"; do printf '<%s>' \"$f\"; done", "<>", "", 0)]
    [InlineData("a=(); a[2]=''; a[9]=last; for f in \"${a[@]:2:0}\"; do printf bad; done", "", "", 0)]
    [InlineData("a=('a b' '' c); IFS=:; printf '<%s>' \"${a[*]:1:2}\"", "<:c>", "", 0)]
    [InlineData("x=abc; eval 'printf bad\"${x:1/0}\"'; printf '%s' \"$?\"", "1", "1/0: division by 0\n", 0)]
    [InlineData("x=abc; eval 'printf bad\"${x:2:-2}\"'; printf '%s' \"$?\"", "1", "-2: substring expression < 0\n", 0)]
    [InlineData("a=(a b c); i=0; eval 'printf \"${a[@]:i++:1/0}\"'; printf '%s:%s' \"$?\" \"$i\"", "1:1", "1/0: division by 0\n", 0)]
    [InlineData("a=([2]='a b' [5]='' [9]=c); IFS=:; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[*]:3:2}\"; f \"${a[@]:3:2}\"\n", "n=1\n<:c>\nn=2\n<>\n<c>\n", "", 0)]
    [InlineData("a=([2]=two [5]=''); f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${a[@]:6}\"; f \"${a[*]:6}\"; f \"${a[@]:5:0}\"; f \"${a[@]:5:1}\"\n", "n=0\n<>\nn=1\n<>\nn=0\n<>\nn=1\n<>\n", "", 0)]
    [InlineData("set -- one two; f(){ printf 'n=%s\\n' \"$#\"; [ \"$1\" = \"$0\" ] && printf zero; }; f \"${@:0:1}\"; printf '\\n'; f \"${@:0:2}\"\n", "n=1\nzero\nn=2\nzero", "", 0)]
    [InlineData("a=([2]='' [9]=last); f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f pre\"${a[@]:3:0}\"post; f pre\"${a[@]:2:1}\"post\n", "n=1\n<prepost>\nn=1\n<prepost>\n", "", 0)]
    [InlineData("a=([2]=two); printf '<%s>\\n' \"${a[@]:3:${a[9]:=1}}\"; printf 'all=<%s>\\n' \"${a[@]}\"\n", "<>\nall=<two>\n", "", 0)]
    [InlineData("a=([2]=two [9]=nine); i=0; printf '<%s>\\n' \"${a[@]:3:i++}\" \"$i\"; printf '<%s>\\n' \"${a[@]:3:-1}\"; printf after\n", "<1>\n", "-1: substring expression < 0\n", 1)]
    [InlineData("x=''; i=0; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${x[@]:i++:i++}\"; printf 'i=%s\\n' \"$i\"\n", "n=1\n<>\ni=2\n", "", 0)]
    [InlineData("sh -c 'set --; i=0; f(){ printf \"n=%s\\n\" \"$#\"; printf \"<%s>\\n\" \"$@\"; }; f \"${@:0:1}\"; f \"${@:i++:i++}\"; printf \"i=%s\\n\" \"$i\"' chosen-zero\n", "n=1\n<chosen-zero>\nn=1\n<chosen-zero>\ni=2\n", "", 0)]
    [InlineData("sh -c 'set -- one two three; f(){ printf \"n=%s\\n\" \"$#\"; printf \"<%s>\\n\" \"$@\"; }; f \"${@: -4:1}\"; f \"${@: -5:1}\"' chosen-zero\n", "n=1\n<chosen-zero>\nn=0\n<>\n", "", 0)]
    [InlineData("x=abc; i=0; printf '<%s>\\n' \"${x:3:i++}\" \"$i\"; printf '<%s>\\n' \"${x:3:-1}\"; printf after\n", "<>\n<1>\n", "-1: substring expression < 0\n", 1)]
    [InlineData("x=abcdef; i=1; printf '<%s>\\n' \"${x:$(printf 2):i?2:1}\" \"$i\"\n", "<cd>\n<1>\n", "", 0)]
    [InlineData("declare -A a; a[one]=ONE; a[two]=TWO; printf '<%s>\\n' \"${a[@]:0:1}\" \"${a[@]:1:1}\" \"${a[@]:2:1}\"", "<ONE>\n<ONE>\n<TWO>\n", "", 0)]
    public async Task SubstringsPreserveFieldsAndOperandEffects(string script, string expected, string error, int status)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        Assert.Equal(expected, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(status, result.ExitCode);
    }

    [Theory]
    [InlineData("x=$'\\377\\u03bb\\U0001f642z'; printf '%s|%s|%s' \"${x:0:1}\" \"${x:1:2}\" \"${x: -1}\"\n", "FF7CCEBBF09F99827C7A", "", 0)]
    [InlineData("x=$'\\360\\237\\377z'; printf '%s|%s' \"${x:1:2}\" \"${x: -3:2}\"\n", "9FFF7C9FFF", "", 0)]
    [InlineData("a=([2]=x [9]=last); a[2]=$'\\377'; printf '<%s>' \"${a[@]:0:2}\"\n", "3CFF3E3C6C6173743E", "", 0)]
    [InlineData("set -- $'\\377' $'\\u03bb' z; IFS=$'\\377'; printf '%s' \"${*:1:2}\"\n", "FFFFCEBB", "", 0)]
    [InlineData("x=$'\\377'; printf '%s' \"${x:9:$(printf bad >&2; printf 1)}\"; printf after\n", "6166746572", "", 0)]
    [InlineData("x=$'\\377'; printf '%s' \"${x:1:$(printf effect >&2; printf 0)}\"; printf after\n", "6166746572", "656666656374", 0)]
    [InlineData("x=$'\\377'; message=$'\\377'; printf '%s' \"${x:0:${missing:?$message}}$(printf bad >&2)\"; printf after\n", "", "6D697373696E673A20FF0A", 1)]
    public async Task SubstringsPreserveValueBytes(string script, string expectedHex, string errorHex, int status)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));
        Assert.Equal(Convert.FromHexString(expectedHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(errorHex), result.Host.StandardError.Content.ToArray());
        Assert.Equal(status, result.ExitCode);
    }
}
