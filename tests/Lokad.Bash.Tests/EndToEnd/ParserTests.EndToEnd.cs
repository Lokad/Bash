using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
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

    [Theory]
    [InlineData("for x in { }; { printf '<%s>' \"$x\"; }", "<{><}>")]
    [InlineData("case } in (a|{|}) printf '<%s>' }; esac", "<}>")]
    [InlineData("a=( { } ); printf '<%s>' \"${a[@]}\"", "<{><}>")]
    [InlineData("x=payload; read -r value <<}\n$x\n}\nprintf '<%s>' \"$value\"", "<payload>")]
    [InlineData("f(){ printf '<%s>' { }; }; eval \"$(declare -f f)\"; f", "<{><}>")]
    [InlineData("function { { printf '<%s>' \"$x\"; }; x=prefix {", "<prefix>")]
    public async Task Parse_BraceWordRoles(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Parse_BraceWordCanBeAGroupRedirectionTarget()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("{ printf '<%s>' }; } > {; printf tail", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("<}>", host.ReadFile("/{"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Parse_UnclosedBraceGroupPreservesEarlierCommandUnitEffects()
    {
        const string script = "printf earlier >before\n{ printf payload >after }";
        Assert.Throws<SyntaxException>(() => Parser.Parse(script));

        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.NotEmpty(host.StandardError.Content.ToArray());
        Assert.Equal("earlier", host.ReadFile("/before"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/after"));
        Assert.Equal(0, host.OpenFileCount);
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
