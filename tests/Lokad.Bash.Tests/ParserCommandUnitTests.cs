using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ParserCommandUnitTests
{
    [Fact]
    public void CommandUnitsRetainTheWholeSourceSizeLimit()
    {
        using var commands = Parser.TryParseCommandUnits(new byte[Parser.MaximumScriptBytes + 1]).GetEnumerator();

        Assert.True(commands.MoveNext());
        var error = Assert.IsType<CommandUnitParseResult.Failed>(commands.Current).Error;

        Assert.Equal(Parser.ScriptSizeLimitMessage, error.Message);
        Assert.False(commands.MoveNext());
    }

    [Theory]
    [InlineData("value=changed\n'unterminated")]
    [InlineData("value=changed;\n'unterminated")]
    public void LaterLexicalFailureDoesNotPreventPublishingACompleteUnit(string source)
    {
        using var commands = Parser.TryParseCommandUnits(ShellSource.FromText(source).Bytes).GetEnumerator();

        Assert.True(commands.MoveNext());
        Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(commands.Current).Command);
        Assert.True(commands.MoveNext());
        Assert.IsType<CommandUnitParseResult.Failed>(commands.Current);
        Assert.False(commands.MoveNext());
    }

    [Fact]
    public void SameLineSyntaxFailureDoesNotPublishPartialCommands()
    {
        using var commands = Parser.TryParseCommandUnits("value=changed; )\n"u8.ToArray()).GetEnumerator();

        Assert.True(commands.MoveNext());
        Assert.IsType<CommandUnitParseResult.Failed>(commands.Current);
        Assert.False(commands.MoveNext());
    }

    [Fact]
    public void LaterUnitErrorsRetainAbsoluteByteOffsetsAndFullScriptValidation()
    {
        var source = "printf 'é'\n)"u8.ToArray();
        var wholeScriptError = Assert.Throws<SyntaxException>(() => Parser.Parse(source));
        using var commands = Parser.TryParseCommandUnits(source).GetEnumerator();
        Assert.True(commands.MoveNext());

        Assert.True(commands.MoveNext());
        var unitError = Assert.IsType<CommandUnitParseResult.Failed>(commands.Current).Error;

        Assert.Equal(source.Length - 1, unitError.Offset);
        Assert.Equal(wholeScriptError.Offset, unitError.Offset);
        Assert.Equal(wholeScriptError.Message, unitError.Message);
        Assert.False(commands.MoveNext());
    }

    [Theory]
    [InlineData(": >'a\nb'\n", 1, 2, 2)]
    [InlineData(": 2>'a\nb'\n", 1, 2, 1)]
    [InlineData(": {out}>'a\nb'\n", 1, 2, 1)]
    [InlineData(": >'a\nb' 'later\nword'\n", 3, 3, 2)]
    public void MultilineRedirectionsKeepTheLastCommandWordLine(
        string source, int lastWordLine, int endLine, int diagnosticLine)
    {
        var bytes = ShellSource.FromText(source).Bytes;
        var script = Parser.Parse(bytes);
        var unit = Assert.IsType<CommandUnitParseResult.Parsed>(Assert.Single(Parser.TryParseCommandUnits(bytes)));

        AssertLocation(Assert.Single(script.Commands), script.Locations);
        AssertLocation(unit.Command, unit.Locations);

        void AssertLocation(ICommand command, CommandLocations? locations)
        {
            Assert.NotNull(locations);
            Assert.True(locations.TryGet(command, out var location));
            Assert.Equal(lastWordLine, location.LastWordLine);
            Assert.Equal(endLine, location.EndLine);
            Assert.Equal(diagnosticLine, location.RedirectionDiagnosticLine);
        }
    }

    [Fact]
    public void LaterHereDocumentsDoNotChangeEarlierCompletedUnits()
    {
        using var commands = Parser.TryParseCommandUnits(
            "cat <<EOF\nfirst\nEOF\ncat <<EOF\nsecond\nEOF\n"u8.ToArray()).GetEnumerator();
        Assert.True(commands.MoveNext());
        var first = Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(commands.Current).Command);
        var firstBody = Assert.Single(Assert.Single(first.Redirections).Target.Parts);
        Assert.Equal("first\n", firstBody.GetUtf8Text());

        Assert.True(commands.MoveNext());
        var second = Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(commands.Current).Command);
        Assert.Equal("second\n", Assert.Single(Assert.Single(second.Redirections).Target.Parts).GetUtf8Text());
        Assert.Equal("first\n", firstBody.GetUtf8Text());
        Assert.False(commands.MoveNext());
    }
}
