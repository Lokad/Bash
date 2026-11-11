using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationGlobTestsEndToEnd
{

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
