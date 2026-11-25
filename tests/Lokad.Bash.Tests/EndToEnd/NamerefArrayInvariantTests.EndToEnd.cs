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
}
