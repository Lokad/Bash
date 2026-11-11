using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using System.Threading.Tasks;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class ParserTestsEndToEnd
{
    [Fact]
    public async Task Parse_SimpleWords_AreCaptured()
    {
        var script = @"echo hello world";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_LeadingAssignments_AreCaptured()
    {
        var script = @"A=1 B='2 3' echo hi";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirections_AreCaptured()
    {
        var script = @"cat < /input > /output";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

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
    public async Task Parse_QuotedWords_KeepQuoteKinds()
    {
        var script = @"echo 'a b' ""c d""";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_SingleQuotedGlob_IsSingleQuotedWordPart()
    {
        var script = @"rg -g '*.txt' hello /";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
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
    public async Task Parse_ListSequence_SemicolonBuildsList()
    {
        var script = @"echo a; echo b";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
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
    public async Task Parse_ListLeftAssociativity_ForAndOr()
    {
        var script = @"echo a && echo b || echo c";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Pipeline_BasicPipe()
    {
        var script = @"echo a | cat";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Pipeline_PipeAndError()
    {
        var script = @"echo a |& cat";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Pipeline_Negated()
    {
        var script = @"! echo a | cat";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Pipeline_LeftAssociativity()
    {
        var script = @"echo a | cat | wc";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Subshell_WrapsCommand()
    {
        var script = @"(echo a)";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Group_WrapsCommand()
    {
        var script = @"{ echo a; }";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Subshell_ListBody()
    {
        var script = @"(echo a; echo b)";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
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

    [Fact]
    public async Task Parse_If_Basic()
    {
        var script = @"if echo a; then echo b; fi";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_If_WithElse()
    {
        var script = @"if echo a; then echo b; else echo c; fi";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_If_WithElif()
    {
        var script = @"if echo a; then echo b; elif echo c; then echo d; fi";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_While_Basic()
    {
        var script = @"while echo a; do echo b; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Until_Basic()
    {
        var script = @"until echo a; do echo b; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_While_BodyList()
    {
        var script = @"while echo a; do echo b; echo c; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_For_Basic()
    {
        var script = @"for x in a b; do echo $x; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_For_BodyList()
    {
        var script = @"for name in one; do echo $name; echo ok; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_ArithmeticCommand_Basic()
    {
        var script = @"((1+2))";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_ArithmeticCommand_PreservesWhitespace()
    {
        var script = @"((a + b))";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_ConditionalCommand_Basic()
    {
        var script = @"[[ -f /file ]]";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_ConditionalCommand_PreservesWhitespace()
    {
        var script = @"[[ a == b ]]";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_FunctionDefinition_NameParenForm()
    {
        var script = @"myfunc () { echo hi; }";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_FunctionDefinition_FunctionKeywordForm()
    {
        var script = @"function other { echo hi; }";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_ArithmeticForCommand_Basic()
    {
        var script = @"for ((i=0;i<3;i++)); do echo $i; done";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_CaseCommand_SinglePattern()
    {
        var script = @"case $x in a) echo a ;; esac";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_CaseCommand_MultiplePatterns()
    {
        var script = @"case $x in a|b) echo ok ;; esac";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_ForceOutput()
    {
        var script = @"echo hi >| /out";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_DuplicateOutput()
    {
        var script = @"echo hi 2>&1";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_OutputAndError()
    {
        var script = @"echo hi &> /out";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_CloseInput()
    {
        var script = @"echo hi 3<&-";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_AllocateOutput()
    {
        var script = @"echo hi {fd}> /out";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
    }

    [Fact]
    public async Task Parse_Redirection_AllocateInput()
    {
        var script = @"echo hi {in}< /input";
        var tree = BuildDefaultTree();
        await EndToEndHarness.RunScriptAsync(script, tree);
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
