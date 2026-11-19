using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ParserHereDocumentTests
{
    [Theory]
    [InlineData("$(printf 'END')", "$(printf 'END')", true)]
    [InlineData("\"$(printf \"END\")\"", "$(printf END)", false)]
    [InlineData("''$(printf 'END')", "$(printf END)", false)]
    [InlineData("`printf 'END'`", "`printf 'END'`", true)]
    [InlineData("`printf 'E\\\nND'`", "`printf 'END'`", true)]
    [InlineData("${x:-\"END END\"}", "${x:-\"END END\"}", true)]
    [InlineData("${x:-'END'}''", "${x:-END}", false)]
    [InlineData("$((1 + \"2\"))", "$((1 + \"2\"))", true)]
    [InlineData("$[1 + 2]", "$[1 + 2]", true)]
    [InlineData("$'pre\\x2d'$(printf 'END')", "pre-$(printf END)", false)]
    [InlineData("$(printf $'E\\x4eD')", "$(printf 'END')", true)]
    [InlineData("$(printf \"$(printf E\\\nND)\")", "$(printf \"$(printf END)\")", true)]
    [InlineData("$\\\n(printf END)", "$(printf END)", true)]
    public void StructuredDelimiterKeepsSpellingAndBodyPolicy(string spelling, string closing, bool expands)
    {
        var script = Parser.Parse("cat <<" + spelling + "\n$value\n" + closing + "\nprintf tail\n");
        var commands = Assert.IsType<ListCommand>(Assert.Single(script.Commands));
        var command = Assert.IsType<SimpleCommand>(commands.Left);
        var tail = Assert.IsType<SimpleCommand>(commands.Right);
        Assert.Equal("tail", Assert.Single(tail.Words[1].Parts).GetUtf8Text());
        var redirection = Assert.Single(command.Redirections);
        Assert.NotNull(redirection.HereDocumentDelimiter);
        var syntax = Assert.IsType<HereDocumentDelimiterSyntax>(redirection.DelimiterSyntax);
        Assert.Equal(spelling, syntax.Spelling.ToUtf8String());
        Assert.Equal(closing, syntax.Value.ToUtf8String());
        Assert.Equal(expands, syntax.AllowsExpansion);
        var body = Assert.Single(redirection.Target.Parts);
        Assert.Equal(expands ? WordPartKind.HereDocument : WordPartKind.SingleQuoted, body.Kind);
        Assert.Equal("$value\n", body.GetUtf8Text());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JoinedDelimiterKeepsFollowingErrorsAtTheirPhysicalOffset(bool bytes)
    {
        const string Source = "cat <<END\nπ\nE\\\nND\n)";
        SyntaxError? error;
        if (bytes)
        {
            var source = System.Text.Encoding.UTF8.GetBytes(Source);
            Assert.False(Parser.TryParse(source, out _, out error));
            Assert.NotNull(error);
            Assert.Equal(source.Length - 1, error.Offset);
        }
        else
        {
            Assert.False(Parser.TryParse(Source, out _, out error));
            Assert.NotNull(error);
            Assert.Equal(Source.Length - 1, error.Offset);
        }
        Assert.Contains("Unexpected token", error.Message);
    }

    [Theory]
    [InlineData("time cat <<END\nbody\nEND\n", false)]
    [InlineData("time -p cat <<END\nbody\nEND\n", true)]
    public void CompletingTimedBodiesRetainsTheWrapperAndProvisionalDelimiter(string source, bool portable)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source));
        var provisional = Assert.IsType<TimedCommand>(Parser.ParsePipeline(tokenizer));
        var originalCommand = Assert.IsType<SimpleCommand>(provisional.Inner);
        var original = Assert.Single(originalCommand.Redirections);
        var reader = tokenizer.HereDocuments;
        Assert.NotNull(reader);

        var completed = Assert.IsType<TimedCommand>(reader.Complete(provisional));
        var command = Assert.IsType<SimpleCommand>(completed.Inner);
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(portable, completed.Portable);
        Assert.Equal("body\n", Assert.Single(redirection.Target.Parts).GetUtf8Text());
        Assert.Same(original.Target, redirection.HereDocumentDelimiter);
        Assert.Equal("END", Assert.Single(original.Target.Parts).GetUtf8Text());
        Assert.Same(originalCommand.Words, command.Words);
        Assert.Same(originalCommand.Assignments, command.Assignments);
    }

    [Fact]
    public void CompletingBodies_PreservesProvisionalNodesAndUnchangedCollections()
    {
        var tokenizer = new Tokenizer(ShellSource.FromText("cat <<END | cat\nbody\nEND\n"));
        var provisional = Assert.IsType<PipelineCommand>(Parser.ParsePipeline(tokenizer));
        var left = Assert.IsType<SimpleCommand>(provisional.Left);
        var original = Assert.Single(left.Redirections);
        Assert.Equal("END", Assert.Single(original.Target.Parts).GetUtf8Text());

        Assert.NotNull(tokenizer.HereDocuments);
        var completed = Assert.IsType<PipelineCommand>(tokenizer.HereDocuments.Complete(provisional));
        var completedLeft = Assert.IsType<SimpleCommand>(completed.Left);
        var redirection = Assert.Single(completedLeft.Redirections);
        Assert.Equal("body\n", Assert.Single(redirection.Target.Parts).GetUtf8Text());
        Assert.Same(original.Target, redirection.HereDocumentDelimiter);
        Assert.Equal("END", Assert.Single(original.Target.Parts).GetUtf8Text());
        Assert.Same(left.Words, completedLeft.Words);
        Assert.Same(left.Assignments, completedLeft.Assignments);
        Assert.Same(left.DeclarationAssignments, completedLeft.DeclarationAssignments);
        Assert.Same(provisional.Right, completed.Right);
    }

    [Fact]
    public void LongListsWithBodies_CompleteWithoutRecursiveTreeTraversal()
    {
        var source = "f(){ " + string.Concat(Enumerable.Repeat(":; ", 10000)) + "cat <<END; }\nbody\nEND\n";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));
        var list = Assert.IsType<ListCommand>(Assert.IsType<GroupCommand>(function.Body).Body);
        var command = Assert.IsType<SimpleCommand>(list.Right);
        var body = Assert.Single(Assert.Single(command.Redirections).Target.Parts);
        Assert.Equal("body\n", body.GetUtf8Text());
        var count = 1;
        ICommand current = list.Left;
        while (current is ListCommand preceding) { count++; current = preceding.Left; }
        Assert.Equal(10000, count);
    }

    // The parser deliberately rejects incomplete bodies instead of Bash's warning-and-accept behavior.
    [Theory]
    [InlineData("cat <<END | cat")]
    [InlineData("cat <<END; printf after")]
    [InlineData("{ cat; } <<END")]
    [InlineData("cat <<ONE | cat <<TWO\nfirst\nONE\nmissing second delimiter\n")]
    public void MissingBodies_AreNotPublishedAsDelimiterWords(string source) =>
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
}
