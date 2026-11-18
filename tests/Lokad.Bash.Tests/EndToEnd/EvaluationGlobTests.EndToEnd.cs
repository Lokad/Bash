using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGlobTestsEndToEnd
{
    [Theory]
    [InlineData("?", "<7><é><😀><😁>")]
    [InlineData("??", "<😀x><😁x>")]
    [InlineData("????", "<????>")]
    [InlineData("[😀]", "<😀>")]
    [InlineData("[!a]", "<7><é><😀><😁>")]
    [InlineData("[😀-😂]", "<😀><😁>")]
    [InlineData("[😀]*", "<😀><😀[x><😀x>")]
    [InlineData("[😀[:digit:]]", "<7><😀>")]
    [InlineData("[[.😀.]]", "<😀>")]
    [InlineData("[[=😀=]]", "<😀>")]
    [InlineData("😀'['?", "<😀[x>")]
    [InlineData("[😀*", "<[😀x>")]
    public async Task Evaluation_GlobExpansion_CountsSupplementaryCharactersOnce(string pattern, string expected)
    {
        var tree = EndToEndHarness.BuildTree([
            ("/7", ""), ("/é", ""), ("/😀", ""), ("/😁", ""),
            ("/😀x", ""), ("/😁x", ""), ("/😀[x", ""), ("/[😀x", "")
        ]);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + pattern, tree: tree);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_SelectsSupplementaryDirectoryForFileEffects()
    {
        await using var host = EndToEndHarness.BuildTree(
            ["/dir", "/dir/😀", "/dir/😀x"],
            [("/dir/😀/é", "initial"), ("/dir/😀x/é", "initial")]);
        var result = await host.RunAsync(
            "for file in dir/?/?; do printf selected >\"$file\"; done; printf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("selected", host.ReadFile("/dir/😀/é"));
        Assert.Equal("initial", host.ReadFile("/dir/😀x/é"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_KeepsOrderingWithinEachWord()
    {
        await using var host = EndToEndHarness.BuildTree([("/\U00010000", ""), ("/\uE000", "")]);
        var result = await host.RunAsync("printf '<%s>' '\U00010000'* *", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("3CF09080803E3CEE80803E3CF09080803E",
            Convert.ToHexString(host.StandardOutput.Content.ToArray()));
    }

    [Theory]
    [InlineData("$1", "\uE000")]
    [InlineData("$2", "\U00010000")]
    public async Task Evaluation_GlobExpansion_UsesPathOrderForFileSelection(string operand, string expected)
    {
        await using var host = EndToEndHarness.BuildTree([("/\U00010000", ""), ("/\uE000", "")]);
        var result = await host.RunAsync(
            $"set -- *; printf '%s' \"{operand}\" >chosen; printf tail", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(expected, host.ReadFile("/chosen"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("*/", "<dir/><empty/>")]
    [InlineData("*//", "<dir/><empty/>")]
    [InlineData("dir'//'*", "<dir//file><dir//sub>")]
    [InlineData("dir///sub//l*", "<dir///sub//leaf>")]
    [InlineData("dir//*/", "<dir//sub/>")]
    [InlineData("dir/*//leaf", "<dir/sub/leaf>")]
    [InlineData("plain*/", "<plain*/>")]
    [InlineData("dir//*//missing", "<dir//*//missing>")]
    public async Task Evaluation_GlobExpansion_PreservesPathSpellingAndDirectorySelection(
        string pattern, string expected)
    {
        var host = EndToEndHarness.BuildTree(
            ["/dir", "/dir/sub", "/empty"],
            [("/dir/file", ""), ("/dir/sub/leaf", ""), ("/plain", "")]);
        var result = await EvaluationTestHelper.RunAsync("printf '<%s>' " + pattern, tree: host);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_UsesDirectoryMatchesInRedirectedPaths()
    {
        await using var host = EndToEndHarness.BuildTree(["/dir", "/empty"], [("/plain", "")]);
        var result = await host.RunAsync(
            "for item in */; do printf %s \"$item\" >\"${item}chosen\"; done; printf tail",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("tail", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("dir/", host.ReadFile("/dir/chosen"));
        Assert.Equal("empty/", host.ReadFile("/empty/chosen"));
        Assert.False(await EndToEndHarness.FileExistsAsync(host, "/plainchosen"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_ExpandsUnquotedNamerefArrayElementsIndependently()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b"),
            ("/note.md", "n")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; patterns=('*.txt'); declare -n reference=patterns; "
            + "printf '[%s]\\n' ${reference[@]}; printf '<%s>\\n' \"${reference[@]}\"",
            tree: tree);

        Assert.Equal("[a.txt]\n[b.txt]\n<*.txt>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_QuotedClosingBracketRemainsBracketMember()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/]", "closing"),
            ("/a", "a"),
            ("/b", "b"),
            ("/c", "c")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' [a']'b]",
            tree: tree);

        Assert.Equal("<]>\n<a>\n<b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_TreatsInitialClosingBracketAsMember()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/]", "closing"),
            ("/a", "a"),
            ("/b", "b")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' []a] [!]]",
            tree: tree);

        Assert.Equal("<]>\n<a>\n<a>\n<b>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_TreatsConditionalPrefixAsBracketExpression()
    {
        var tree = EndToEndHarness.BuildTree([
            ("/[", "opening"),
            ("/a", "a")
        ]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' [[]",
            tree: tree);

        Assert.Equal("<[>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ParameterRemoval_TreatsInitialClosingBracketAsMember()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "value=taila; printf '<%s>\\n' \"${value%[]a]}\"");

        Assert.Equal("<tail>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_ValidatesFixedSuffixAfterWildcardDirectory()
    {
        var tree = EndToEndHarness.BuildTree(
            ["/d1", "/d2"],
            [("/d1/found", "found")]);
        var result = await EvaluationTestHelper.RunAsync(
            "cd /; printf '<%s>\\n' d*/missing d*/found",
            tree: tree);

        Assert.Equal("<d*/missing>\n<d1/found>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RequestsDirectoryListing()
    {
        // This test verifies that unquoted globs expand against directory entries.
        // With /a.txt and /b.txt present, "echo *.txt" must list both names.
        // If glob expansion is skipped, STDOUT would be "*.txt".
        var tree = EndToEndHarness.BuildTree([
            ("/a.txt", "a"),
            ("/b.txt", "b"),
            ("/note.md", "n")
        ]);
        var script = @"cd /; echo *.txt";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        var parts = result.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Contains("a.txt", parts);
        Assert.Contains("b.txt", parts);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_GlobExpansion_RecursesSegments()
    {
        // This test verifies that glob expansion can match across path segments.
        // With /a/b/c and /a/d/c present, "echo a/*/c" must list both paths.
        // If segment recursion is not supported, STDOUT would be "a/*/c".
        var tree = EndToEndHarness.BuildTree(
            ["/a", "/a/b", "/a/d"],
            [
                ("/a/b/c", "x"),
                ("/a/d/c", "y")
            ]);
        var script = @"cd /; echo a/*/c";
        var result = await EvaluationTestHelper.RunAsync(script, tree: tree);

        var parts = result.StdOut.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, parts.Length);
        Assert.Contains("a/b/c", parts);
        Assert.Contains("a/d/c", parts);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
