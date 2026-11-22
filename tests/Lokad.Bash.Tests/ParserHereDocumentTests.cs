using System.Text;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ParserHereDocumentTests
{
    private const string TwoDocuments = "{\ncat <<LEFT\nleft\nLEFT\ncat <<RIGHT\nright\nRIGHT\n}\n";

    [Theory]
    [InlineData("cat <<END\n", "END\n")]
    [InlineData("cat <<-END\n", "\tEND\n")]
    [InlineData("cat <<'END'\n", "END")]
    public void ContiguousBodyRetainsItsExactSourceSlice(string header, string closing)
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes(header), 0xff, (byte)'\r', (byte)'\n',
            .. Encoding.ASCII.GetBytes(closing)];
        var script = Parser.Parse(bytes);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(script.Commands));
        var body = Assert.Single(Assert.Single(command.Redirections).Target.Parts).GetUnexpandedValue();

        Assert.True(body.Bytes.Equals((ReadOnlyMemory<byte>)bytes.AsMemory(header.Length, 3)));
    }

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

        var completed = Assert.IsType<TimedCommand>(reader.Complete(provisional, tokenizer.Locations, budget: null));
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
        var tokenizer = new Tokenizer(ShellSource.FromText("cat <<END | cat\nbody\nEND\n"))
        {
            TrackCommandLocations = true
        };
        var provisional = Assert.IsType<PipelineCommand>(Parser.ParsePipeline(tokenizer));
        var left = Assert.IsType<SimpleCommand>(provisional.Left);
        var original = Assert.Single(left.Redirections);
        Assert.Equal("END", Assert.Single(original.Target.Parts).GetUtf8Text());

        Assert.NotNull(tokenizer.HereDocuments);
        var locations = tokenizer.Locations;
        Assert.NotNull(locations);
        Assert.True(locations.TryGet(left, out var sourceLocation));
        var completed = Assert.IsType<PipelineCommand>(tokenizer.HereDocuments.Complete(provisional, locations, budget: null));
        var completedLeft = Assert.IsType<SimpleCommand>(completed.Left);
        Assert.False(locations.TryGet(left, out _));
        Assert.True(locations.TryGet(completedLeft, out var completedLocation));
        Assert.Equal(sourceLocation, completedLocation);
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
    public void CompletingElifBodies_ReusesTheOtherClauses()
    {
        var tokenizer = new Tokenizer(ShellSource.FromText("if false; then :;\n"
            + "elif cat <<CHECK; then cat <<BODY\ncheck\nCHECK\nbody\nBODY\n"
            + "elif true; then :; fi\n"));
        var provisional = Assert.IsType<IfCommand>(Parser.ParsePipeline(tokenizer));
        var reader = Assert.IsType<HereDocumentReader>(tokenizer.HereDocuments);
        var completed = Assert.IsType<IfCommand>(reader.Complete(provisional, tokenizer.Locations, budget: null));

        Assert.Equal(2, completed.ElifClauses.Count);
        Assert.Same(provisional.ElifClauses[1], completed.ElifClauses[1]);
        Assert.Same(provisional.ThenBody, completed.ThenBody);
        AssertBody(provisional.ElifClauses[0].Condition, completed.ElifClauses[0].Condition, "CHECK", "check\n");
        AssertBody(provisional.ElifClauses[0].Body, completed.ElifClauses[0].Body, "BODY", "body\n");

        static void AssertBody(ICommand provisional, ICommand completed, string delimiter, string body)
        {
            var original = Assert.Single(Assert.IsType<SimpleCommand>(provisional).Redirections);
            var replacement = Assert.Single(Assert.IsType<SimpleCommand>(completed).Redirections);
            Assert.Same(original.Target, replacement.HereDocumentDelimiter);
            Assert.Equal(delimiter, Assert.Single(original.Target.Parts).GetUtf8Text());
            Assert.Equal(body, Assert.Single(replacement.Target.Parts).GetUtf8Text());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParserRefundsCompletionBackingWhileTheSyntaxOwnerIsLive(bool streamed)
    {
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        using var syntaxMemory = new MemoryCapacity(owner, 0);
        var context = new ShellScanContext { SyntaxMemory = syntaxMemory };
        var source = Encoding.UTF8.GetBytes(TwoDocuments);
        var command = streamed
            ? Assert.IsType<CommandUnitParseResult.Parsed>(Assert.Single(Parser.TryParseCommandUnits(source, context))).Command
            : Assert.Single(Parser.Parse(source, context).Commands);

        Assert.True(governor.ReservedBytes > 0);
        Assert.True(governor.PeakBytes > governor.ReservedBytes);
        var list = Assert.IsType<ListCommand>(Assert.IsType<GroupCommand>(command).Body);
        Assert.Equal("left\n", Body(list.Left));
        Assert.Equal("right\n", Body(list.Right));
        syntaxMemory.Dispose();
        Assert.Equal(0, governor.ReservedBytes);

        static string Body(ICommand command) =>
            Assert.Single(Assert.Single(Assert.IsType<SimpleCommand>(command).Redirections).Target.Parts).GetUtf8Text();
    }

    [Fact]
    public void RefusedCompletionKeepsProvisionalLocationsAndRefundsScratch()
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(TwoDocuments)) { TrackCommandLocations = true };
        var provisional = Assert.IsType<GroupCommand>(Parser.ParsePipeline(tokenizer));
        var body = Assert.IsType<ListCommand>(provisional.Body);
        var reader = Assert.IsType<HereDocumentReader>(tokenizer.HereDocuments);
        var locations = Assert.IsType<CommandLocations>(tokenizer.Locations);
        var governor = new MemoryGovernor(512);
        using var owner = governor.CreateScope();

        Assert.Throws<MemoryBudgetExceededException>(() => reader.Complete(provisional, locations, owner));
        Assert.True(governor.PeakBytes > 0);
        Assert.Equal(0, governor.ReservedBytes);
        Assert.True(locations.TryGet(provisional, out _));
        Assert.True(locations.TryGet(body.Left, out _));
        Assert.True(locations.TryGet(body.Right, out _));
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
