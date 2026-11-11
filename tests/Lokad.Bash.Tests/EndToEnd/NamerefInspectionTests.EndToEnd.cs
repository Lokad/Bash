using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class NamerefInspectionTestsEndToEnd
{
    // Recorded status/output contracts; nonempty diagnostics are substring assertions.
    [Theory]
    [InlineData("target=value; declare -n ref=target; declare -n chain=ref; printf '%s:%s\\n' \"${!chain}\" \"${!ref}\"", "target:target\n", 0, "")]
    [InlineData("declare -n ref; declare -n chain=ref; printf '<%s>:<%s>\\n' \"${!chain}\" \"${!ref}\"", "", 1, "chain: invalid indirect expansion")]
    [InlineData("a=(zero one); i=0; declare -n ref='a[i++]'; declare -n chain=ref; printf '%s:%s\\n' \"${!chain}\" \"$i\"", "a[i++]:0\n", 0, "")]
    [InlineData("declare -n ref; printf '<%s>\\n' \"${!ref}\"", "", 1, "ref: invalid indirect expansion")]
    [InlineData("declare -n a=b; declare -n b=a; printf '<%s>\\n' \"${!a}\"; printf after", "", 1, "a: invalid indirect expansion")]
    [InlineData("declare target; declare -n ref=target; declare -n chain=ref; printf '%s\\n' \"${!chain}\"", "target\n", 0, "")]
    public async Task IndirectExpansion_InspectsTheTerminalNamerefWithoutEvaluatingItsSelector(
        string script, string expectedOutput, int expectedStatus, string diagnostic)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedStatus, result.ExitCode);
        if (diagnostic.Length == 0)
            Assert.Empty(result.StdErr);
        else
            Assert.Contains(diagnostic, result.StdErr, StringComparison.Ordinal);
    }
}
