using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class NamerefArrayInvariantTestsEndToEnd
{
    // Storage contract, not Bash 5.2 parity: its cyclic-local array/nameref hybrids violate exclusive value kinds.
    [Theory]
    [InlineData("-a first=(value)", "0")]
    [InlineData("-A first=([key]=value)", "key")]
    public async Task Evaluation_CyclicLocalArrayConversion_PreservesExclusiveValueKinds(string declaration, string key)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "f() { local -n first=second second=first; local " + declaration
            + "; printf '<%s>\\n' \"${first[" + key + "]}\"; }; f");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<value>\n", result.StdOut);
        Assert.Equal("warning: first: circular name reference\nwarning: first: circular name reference\n", result.StdErr);
    }

    [Theory]
    [InlineData("local -n slot=dest", "-n", "-an slot=()", 0)]
    [InlineData("local -n slot", "-An", "-An slot", 1)]
    [InlineData("local -nr slot=dest", "-An", "-Anr slot=()", 0)]
    [InlineData("local -n through=dest slot=through", "-nx", "-an slot=()", 0)]
    public async Task RejectedLocalNamerefArrayRetainsKindWithoutReferent(
        string setup, string flags, string declaration, int referenceStatus)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + $"  {setup}\n  local {flags} 'slot[]=valid'\n"
            + "  printf '<%s:%s>' \"$?\" \"${#slot[@]}\"; "
            + "[[ -R slot ]]; printf '<%s>' \"$?\"; [[ -v slot ]]; printf '<%s>\\n' \"$?\"; declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal($"<1:0><{referenceStatus}><1>\ndeclare {declaration}\ndeclare -- slot=\"outside\"\n"
            + "declare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("slot=changed", "<0>\ndeclare -an slot=()\n", "")]
    [InlineData("local -n slot=second", "<1>\ndeclare -an slot=()\n",
        "line 5: local: slot: reference variable cannot be an array\n")]
    [InlineData("unset -v slot", "<0>\ndeclare -an slot=()\n", "")]
    [InlineData("unset -n slot", "<0>\ndeclare -- slot\n", "")]
    public async Task RejectedLocalNamerefArrayUsesItsOwnBindingForLaterCommands(
        string command, string output, string error)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + "  local -n slot=dest\n  local -n 'slot[]=valid'\n"
            + $"  {command}; " + "printf '<%s>\\n' \"$?\"; declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal(output + "declare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n" + error, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ClearingRejectedArrayNamerefInChildKeepsTheParentBinding()
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outside; dest=target\nf(){\n"
            + "  local -n slot=dest\n  local -An 'slot[]=valid'\n"
            + "  ( local +n slot; slot=changed; declare -p slot ); declare -p slot\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal("declare -A slot=([0]=\"changed\" )\ndeclare -An slot=()\n"
            + "declare -- slot=\"outside\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal("line 4: local: slot: reference variable cannot be an array\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n ref=dest", "local +n 'ref[]='", "-an ref=()", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "declare -A 'ref[]=value'", "-An ref", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "local -ix 'ref[]=value'", "-ainx ref", "ref[]: bad array subscript")]
    [InlineData("local -n ref", "local -ix 'ref[]=two words'", "-an ref",
        "local: `two words': invalid variable name for name reference")]
    [InlineData("local -nr ref=dest", "local -A +n 'ref[]=value'", "-Anr ref=()", "local: ref: readonly variable")]
    [InlineData("local -nr through; local -n ref=through", "local -A 'ref[]=value'", "-An ref=()", "ref[]: bad array subscript")]
    public async Task EmptyLocalElementRejectionRetainsThePreparedNamerefArray(
        string setup, string command, string declaration, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("ref=outer; dest=target\nf(){\n"
            + $"  {setup}\n  {command}\n"
            + "  printf 'reached=%s\\n' \"$?\"; declare -p ref dest\n"
            + "}\nf\ndeclare -p ref dest\n");

        Assert.Equal($"reached=1\ndeclare {declaration}\ndeclare -- dest=\"target\"\n"
            + "declare -- ref=\"outer\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n ref=dest", "local -n 'ref[]=value'", "-n ref=\"dest\"\ndeclare -a dest",
        "local: ref: reference variable cannot be an array")]
    [InlineData("local -n through=dest ref=through", "local +n 'ref[]=value'", "-n ref=\"through\"\ndeclare -a dest",
        "ref[]: bad array subscript")]
    [InlineData("declare -gn through=dest; local -n ref=through", "local -Ax 'ref[]=two words'", "-An ref=()",
        "local: `two words': invalid variable name for name reference")]
    public async Task EmptyLocalElementRejectionDistinguishesMissingTargetOwners(
        string setup, string command, string declaration, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("ref=outer\nf(){\n"
            + $"  {setup}\n  {command}\n"
            + "  printf 'reached=%s\\n' \"$?\"; declare -p ref; declare -p dest 2>/dev/null || :\n"
            + "}\nf\ndeclare -p ref\n");

        Assert.Equal($"reached=1\ndeclare {declaration}\ndeclare -- ref=\"outer\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("local -n slot=dest", "+n", "i++", "-an slot=()", 1, "local: `slot[i++]': not a valid identifier")]
    [InlineData("local -n slot", "", "0", "-an slot", 0, "local: `slot[0]': not a valid identifier")]
    [InlineData("local -n slot", "-a", "i++", "-an slot", 1, "local: `slot[i++]': not a valid identifier")]
    [InlineData("local -n slot", "-A", "i++", "-An slot", 1, "local: `slot[i++]': not a valid identifier")]
    [InlineData("local -n slot", "-A", "i++ - 1", "-An slot", 1, "slot[i++ - 1]: bad array subscript")]
    public async Task RejectedLocalNamerefElementKeepsSelectorEffectsAndThePreparedArray(
        string setup, string flags, string selector, string declaration, int indexEffects, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync("slot=outer; dest=target; i=0\nf(){\n"
            + $"  {setup}\n  local {flags} 'slot[{selector}]=value'\n"
            + "  printf '<%s:%s>\\n' \"$?\" \"$i\"; declare -p slot dest\n"
            + "}\nf\ndeclare -p slot dest\n");

        Assert.Equal($"<1:{indexEffects}>\ndeclare {declaration}\ndeclare -- dest=\"target\"\n"
            + "declare -- slot=\"outer\"\ndeclare -- dest=\"target\"\n", result.StdOut);
        Assert.Equal($"line 4: {diagnostic}\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RejectedLocalNamerefElementRetainsFieldEffectsAndVisitsLaterOperands()
    {
        var result = await EvaluationTestHelper.RunAsync("i=0; j=0\nf(){\n"
            + "  local -n slot\n  local -A 'slot[i++]'=entry$((j++)) after=kept\n"
            + "  printf '<%s:%s:%s>\\n' \"$?\" \"$i\" \"$j\"; declare -p slot after\n"
            + "}\nf\n");

        Assert.Equal("<1:1:1>\ndeclare -An slot\ndeclare -A after=([0]=\"kept\" )\n", result.StdOut);
        Assert.Equal("line 4: local: `slot[i++]': not a valid identifier\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
