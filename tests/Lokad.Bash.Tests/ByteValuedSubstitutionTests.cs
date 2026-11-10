using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ByteValuedSubstitutionTests
{
    [Theory]
    [InlineData("A=$'\\377'; A=$'\\376' B=$(printf '%s' \"$A\") sh -c 'printf \"%s/%s\" \"$A\" \"$B\"'; printf '%s' \"$A\"", "FE2FFEFF", "")]
    [InlineData("x=outer; x=('' $'\\377') value=$(printf '%s' \"$x\") eval 'printf \"%s\" \"$value\"'; printf '%s' \"$x\"", "2820FF296F75746572", "")]
    [InlineData("v=outer; v=$'\\377' printf '%s' \"$(printf '%s' \"$v\")\"; printf '/%s' \"$v\"", "6F757465722F6F75746572", "")]
    [InlineData("v=outer; v=$'\\377' captured=$(printf '%s' \"$v\") eval 'printf \"%s\" \"$captured\"'; printf '/%s' \"$v\"", "FF2F6F75746572", "")]
    [InlineData("arr[2147483648]=$'\\377'; declare -n ref=arr; printf '%s' \"$(printf '%s' \"${ref[2147483648]}\"; arr[2147483648]=$'\\376')\" \"${arr[2147483648]}\"", "FFFF", "")]
    [InlineData("set -- $'\\377'; printf '%s' \"$(printf '%s' \"$1\"; set -- $'\\376'; printf '%s' \"$1\")\" \"$1\"", "FFFEFF", "")]
    [InlineData("v=global; f() { local v=$'\\377'; printf '%s' \"$(local v=$'\\376'; printf '%s' \"$v\"; return 7)\" \"$v\"; }; f; printf '%s' \"$v\"", "FEFF676C6F62616C", "")]
    [InlineData("printf '%s' \"$(printf '\\xc3')\"$(printf '\\xa9')\n", "C3A9", "")]
    [InlineData("printf '%s' pre$(printf '\\xc3')$'\\251'post\n", "707265C3A9706F7374", "")]
    [InlineData("printf '<%s>' \"$(printf 'a\\r\\n\\n')\"\n", "3C610D3E", "")]
    [InlineData("printf '<%s>' \"$(printf 'a\\n\\0\\n')\"\n", "3C613E", "warning: command substitution: ignored null byte in input\n")]
    [InlineData("printf '<%s>' \"$(printf 'a\\0b\\0c\\n\\n')\"\n", "3C6162633E", "warning: command substitution: ignored null byte in input\n")]
    [InlineData("IFS=é; printf '<%s>' $(printf 'aéb')\n", "3C613E3C623E", "")]
    [InlineData("IFS=🙂; printf '<%s>' $(printf 'a🙂b')\n", "3C613E3C623E", "")]
    [InlineData("IFS=🙂; set -- a b; printf '%s' \"$*\"\n", "61F09F998262", "")]
    [InlineData("IFS=🙂 read -r a b <<'EOF'\nfirst🙂second\nEOF\nprintf '<%s>' \"$a\" \"$b\"\n", "3C66697273743E3C7365636F6E643E", "")]
    [InlineData("printf '<%s>' \"$(printf '\\0\\n\\n')\"\n", "3C3E", "warning: command substitution: ignored null byte in input\n")]
    [InlineData("printf '%s' \"$(printf '\\xc3\\0')\"$(printf '\\xa9')\n", "C3A9", "warning: command substitution: ignored null byte in input\n")]
    [InlineData("IFS=' ,'; printf '<%s>' $(printf ' a, ,b, ')\n", "3C613E3C3E3C623E", "")]
    [InlineData("IFS=; printf '%s' $(printf '\\xc3')$(printf '\\xa9')\n", "C3A9", "")]
    [InlineData("printf '<%s>' \"$(printf 'a\\n\\r')\"\n", "3C610A0D3E", "")]
    [InlineData("printf '%s:%s' \"$(printf '\\377\\r\\n\\n'; exit 7)\" \"$?\"", "FF0D3A37", "")]
    [InlineData("printf '%s:%s' \"$(printf '\\377\\0\\r\\n'; exit 7)\" \"$?\"", "FF0D3A37", "warning: command substitution: ignored null byte in input\n")]
    public async Task Substitution_PreservesBytesAndFieldBoundaries(
        string script, string outputHex, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(expectedError),
            result.Host.StandardError.Content.ToArray());
    }
}
