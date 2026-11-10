using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ReadDescriptorSelectionTests
{
    // Bash 5.2.37 (C.UTF-8); diagnostics omit the native process/line prefix.
    [Theory]
    [InlineData("read -u3 value 3<<< selected <<< stdin; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u 3 value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -ru3 value 3<<< 'a\\b'; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C615C623E", "")]
    [InlineData("read -u3 -u4 value 3<<< first 4<<< second; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C7365636F6E643E", "")]
    [InlineData("read -u9 -u3 value 3<<< selected; printf '<%s>' \"$?\"", "3C313E", "726561643A20393A20696E76616C69642066696C652064657363726970746F723A204261642066696C652064657363726970746F720A")]
    [InlineData("exec 3<<< $'first\\nsecond'; exec 4<&3; read -u3 a; read -u4 b; printf '<%s><%s>' \"$a\" \"$b\"", "3C66697273743E3C7365636F6E643E", "")]
    [InlineData("read -u3 value 3<<< $'first\\\\\\nsecond'; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C66697273747365636F6E643E", "")]
    [InlineData("read -u $'\\377' value; printf '<%s>' \"$?\"", "3C313E", "726561643A20FF3A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u-1 value; printf '<%s>' \"$?\"", "3C313E", "726561643A202D313A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u2147483648 value; printf '<%s>' \"$?\"", "3C313E", "726561643A20323134373438333634383A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u '' value; printf '<%s>' \"$?\"", "3C313E", "726561643A203A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u -- value; printf '<%s>' \"$?\"", "3C313E", "726561643A202D2D3A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u+3 value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u ' 3 ' value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u3 1bad 3<&-; printf '<%s>' \"$?\"", "3C313E", "726561643A20333A20696E76616C69642066696C652064657363726970746F723A204261642066696C652064657363726970746F720A")]
    [InlineData("value=old; read -u3 value 3</dev/null; printf '<%s><%s>' \"$?\" \"$value\"", "3C313E3C3E", "")]
    [InlineData("read -u $'\\v3\\f' value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C313E3C3E", "726561643A200B330C3A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u03 value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u-0 value <<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u0x3 value 3<<< selected; printf '<%s>' \"$?\"", "3C313E", "726561643A203078333A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    [InlineData("read -u2147483647 value; printf '<%s>' \"$?\"", "3C313E", "726561643A20323134373438333634373A20696E76616C69642066696C652064657363726970746F723A204261642066696C652064657363726970746F720A")]
    [InlineData("declare -A a; read -u3 $'a[\\377]' 3<<< selected; printf '<%s>' \"${a[$'\\377']}\"", "3C73656C65637465643E", "")]
    [InlineData("readonly value=old; read -u3 value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C313E3C6F6C643E", "76616C75653A20726561646F6E6C79207661726961626C650A")]
    [InlineData("exec 3<<< $'first\\nsecond'; read -u3 value; read next <&3; printf '<%s><%s>' \"$value\" \"$next\"", "3C66697273743E3C7365636F6E643E", "")]
    [InlineData("{ read -u3 selected; read normal; printf '<%s><%s>' \"$selected\" \"$normal\"; } 3<<< selected <<< stdin", "3C73656C65637465643E3C737464696E3E", "")]
    [InlineData("exec 3<<< data; read -u3 1bad; read -u3 value; printf '<%s>' \"$value\"", "3C646174613E", "726561643A206031626164273A206E6F7420612076616C6964206964656E7469666965720A")]
    [InlineData("read -u $'\\r3' value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C303E3C73656C65637465643E", "")]
    [InlineData("read -u $'3\\r' value 3<<< selected; printf '<%s><%s>' \"$?\" \"$value\"", "3C313E3C3E", "726561643A20330D3A20696E76616C69642066696C652064657363726970746F722073706563696669636174696F6E0A")]
    public async Task SelectedInputPreservesBytesOrderingAndDescriptorAliases(
        string script, string outputHex, string diagnosticHex)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), result.Host.StandardOutput.Content.ToArray());
        Assert.Equal(Convert.FromHexString(diagnosticHex), result.Host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task MissingDescriptorArgumentReportsOnlySupportedUsage()
    {
        var result = await EndToEndHarness.RunScriptAsync("read -u", EndToEndHarness.BuildTree([]));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.StdOut);
        Assert.Equal("read: -u: option requires an argument\nread: usage: read [-r] [-d delim] [-u fd] [name ...]\n", result.StdErr);
    }
}
