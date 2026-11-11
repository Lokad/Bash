using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using System.Threading.Tasks;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EngineTestsEndToEnd
{
    [Fact]
    public async Task Engine_RunAsync_VisitsCommandsInOrder()
    {
        var tree = EndToEndHarness.BuildTree([]);

        var result = await EndToEndHarness.RunScriptAsync("echo first; echo second", tree);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("first\nsecond\n", result.StdOut);
    }
}
