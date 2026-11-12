using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using System.Threading.Tasks;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ParserTestsEndToEnd
{
    [Theory]
    [InlineData("echo 600 > /output; cat /output")]
    [InlineData("echo 600 1\\\n> /output; cat /output")]
    [InlineData("echo 600 2> /output; cat /output")]
    public async Task Parse_Redirections_PreserveNumericArgument(string script)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, BuildDefaultTree());

        Assert.Equal("600\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Parse_Comments_AreIgnored()
    {
        var script = @"echo hi # c
who".Replace("\r\n", "\n", StringComparison.Ordinal);
        var tree = BuildDefaultTree();
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(127, result.ExitCode);
    }

    [Fact]
    public async Task Parse_LineContinuations_AreRemoved()
    {
        var script = @"echo a\
 b".Replace("\r\n", "\n", StringComparison.Ordinal);
        var tree = BuildDefaultTree();
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Parse_ListSequence_NewlineBuildsList()
    {
        var script = @"echo a
 echo b".Replace("\r\n", "\n", StringComparison.Ordinal);
        var tree = BuildDefaultTree();
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("a\nb\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Parse_Group_ListBody()
    {
        var script = @"{ echo a
 echo b; }".Replace("\r\n", "\n", StringComparison.Ordinal);
        var tree = BuildDefaultTree();
        var result = await EndToEndHarness.RunScriptAsync(script, tree);

        Assert.Equal("a\nb\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    private static TestHost BuildDefaultTree()
    {
        return EndToEndHarness.BuildTree(
            ["/docs"],
            [
                ("/input", "input\\n"),
                ("/file", "content\\n"),
                ("/docs/note.txt", "note\\n")
            ]);
    }
}
