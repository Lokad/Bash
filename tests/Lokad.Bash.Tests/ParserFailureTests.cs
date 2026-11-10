using System.Runtime.ExceptionServices;
using System.Text;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ParserFailureTests
{
    [Theory]
    [InlineData("printf A &&")]
    [InlineData("printf A |")]
    [InlineData("'unterminated")]
    [InlineData("\"unterminated")]
    [InlineData("cat <<EOF\nbody")]
    [InlineData("if true; then\nprintf wrong\n")]
    [InlineData("for 1 in value; do :; done")]
    [InlineData("case x in x) :")]
    [InlineData("f(){ :")]
    [InlineData("value=( [unterminated )")]
    [InlineData("printf $(printf A &&)")]
    [InlineData("printf $((1+")]
    [InlineData("printf ${value:-$(printf A &&)}")]
    [InlineData("999999999999999999999>file")]
    [InlineData("printf <(printf A)")]
    public void ExpectedFailuresDoNotThrowInsideTryApis(string invalidUnit)
    {
        var source = "printf 'é'\n" + invalidUnit;
        var bytes = Encoding.UTF8.GetBytes(source);
        var thread = Environment.CurrentManagedThreadId;
        var exceptions = new List<Exception>();
        AppDomain.CurrentDomain.FirstChanceException += RecordException;
        try
        {
            Assert.False(Parser.TryParse(source, out var textScript, out var textError));
            Assert.Null(textScript);
            Assert.False(Parser.TryParse(bytes, out var byteScript, out var byteError));
            Assert.Null(byteScript);

            var failure = Assert.IsType<SyntaxError>(byteError);
            var textFailure = Assert.IsType<SyntaxError>(textError);
            Assert.Equal(failure.Message, textFailure.Message);
            Assert.Equal(Encoding.UTF8.GetCharCount(bytes.AsSpan(0, failure.Offset)), textFailure.Offset);
            var units = Parser.TryParseCommandUnits(bytes).ToArray();
            Assert.Equal(2, units.Length);
            Assert.IsType<CommandUnitParseResult.Parsed>(units[0]);
            Assert.Equal(failure, Assert.IsType<CommandUnitParseResult.Failed>(units[1]).Error);

            Assert.Null(ShellCommandParser.TryParse(source));
            Assert.Null(ShellCommandParser.TryParsePipeline(source));
            Assert.Null(ShellCommandParser.TryParseChain(source));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= RecordException;
        }
        Assert.Empty(exceptions);

        // The throwing facade preserves the same diagnostic and UTF-16 location.
        var facadeError = Assert.Throws<SyntaxException>(() => Parser.Parse(source));
        Assert.False(Parser.TryParse(source, out _, out var expected));
        Assert.Equal(expected.Message, facadeError.Message);
        Assert.Equal(expected.Offset, facadeError.Offset);
        Assert.Equal(expected, facadeError.Error);

        void RecordException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == thread)
                exceptions.Add(args.Exception);
        }
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("tokens")]
    [InlineData("commands")]
    [InlineData("subscripts")]
    public void ParsingLimitsAreFailuresWithoutInternalExceptions(string limit)
    {
        var source = limit switch
        {
            "bytes" => new string(' ', Parser.MaximumScriptBytes + 1),
            "tokens" => string.Concat(Enumerable.Repeat("''", ShellScanContext.MaximumSyntaxUnits + 1)),
            "commands" => string.Concat(Enumerable.Repeat("{ ", ShellScanContext.MaximumCommandDepth)) + ":;" +
                string.Concat(Enumerable.Repeat(" };", ShellScanContext.MaximumCommandDepth)),
            "subscripts" => "x[" + new string('[', ShellScanContext.MaximumSyntaxUnits) + "0]=1",
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        var thread = Environment.CurrentManagedThreadId;
        var exceptions = new List<Exception>();
        AppDomain.CurrentDomain.FirstChanceException += RecordException;
        try
        {
            Assert.False(Parser.TryParse(source, out var script, out var error));
            Assert.Null(script);
            Assert.Contains("limit", error.Message);
            var failure = Assert.IsType<CommandUnitParseResult.Failed>(
                Assert.Single(Parser.TryParseCommandUnits(Encoding.UTF8.GetBytes(source))));
            Assert.Equal(error, failure.Error);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= RecordException;
        }
        Assert.Empty(exceptions);

        void RecordException(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == thread)
                exceptions.Add(args.Exception);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment\n")]
    [InlineData("printf 'é'\ncat <<EOF\nbody\nEOF\n")]
    public void SuccessfulTryParsingPublishesSyntaxWithoutAnError(string source)
    {
        Assert.True(Parser.TryParse(source, out var script, out var error));
        Assert.NotNull(script);
        Assert.Null(error);
        Assert.True(Parser.TryParse(Encoding.UTF8.GetBytes(source), out var byteScript, out var byteError));
        Assert.NotNull(byteScript);
        Assert.Null(byteError);
        Assert.Equal(script.Commands.Count, byteScript.Commands.Count);
    }
}
