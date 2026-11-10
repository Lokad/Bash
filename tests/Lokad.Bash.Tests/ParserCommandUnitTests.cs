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

    [Theory]
    [InlineData("cat <<EOF\nbody")]
    [InlineData("if true; then\nprintf wrong\n")]
    [InlineData("for 1 in value; do :; done")]
    [InlineData("printf A &&")]
    public void SharedGrammarFailuresAreReturnedOnceAfterEarlierCompleteUnits(string invalidUnit)
    {
        var source = ShellSource.FromText("value=changed\n" + invalidUnit).Bytes;
        var wholeScriptError = Assert.Throws<SyntaxException>(() => Parser.Parse(source));

        var results = Parser.TryParseCommandUnits(source).ToArray();

        Assert.Equal(2, results.Length);
        Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(results[0]).Command);
        var failure = Assert.IsType<CommandUnitParseResult.Failed>(results[1]);
        Assert.Equal(wholeScriptError.Message, failure.Error.Message);
        Assert.Equal(wholeScriptError.Offset, failure.Error.Offset);
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
