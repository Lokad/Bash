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
}
