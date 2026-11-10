using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticCommandScannerTests
{
    [Theory]
    [InlineData("((x=(1+2)))", "x=(1+2)")]
    [InlineData("((x=((1+2))))", "x=((1+2))")]
    [InlineData("((x=(1)+(2)))", "x=(1)+(2)")]
    [InlineData("((1 || (x=(1+2))))", "1 || (x=(1+2))")]
    [InlineData("((x='))'))", "x='))'")]
    [InlineData("((x=\"))\"))", "x=\"))\"")]
    [InlineData("((x=$(printf \"%s\" \"))\")))", "x=$(printf \"%s\" \"))\")")]
    [InlineData("((x=${missing:-\")}))\"}))", "x=${missing:-\")}))\"}")]
    [InlineData("((x=$((1+(2)))))", "x=$((1+(2)))")]
    [InlineData("((x=1\\)))", "x=1\\)")]
    [InlineData("(\\\n(x=(1+\\\n2)))", "x=(1+2)")]
    [InlineData("((x=(1+2)\\\n))", "x=(1+2)")]
    [InlineData("((x='a\\\nb'))", "x='a\\\nb'")]
    [InlineData("((x=\"a\\\nb\"))", "x=\"ab\"")]
    [InlineData("((x=${missing:-1))", "x=${missing:-1")]
    [InlineData("((x=${a:-${b:-1))", "x=${a:-${b:-1")]
    [InlineData("((x=(${missing:-1)))", "x=(${missing:-1)")]
    [InlineData("((x=\"${a:-))}\"))", "x=\"${a:-))}\"")]
    [InlineData("((x=${a:-\"))\"}))", "x=${a:-\"))\"}")]
    public void Tokenizer_PreservesTheWholeExpressionAndPhysicalRange(string command, string expression)
    {
        var source = " \t" + command + "; tail";
        var tokenizer = new Tokenizer(ShellSource.FromText(source));

        var token = tokenizer.NextToken();

        Assert.Equal(TokenKind.ArithmeticCommand, token.Kind);
        Assert.Equal(expression, token.CommandExpression.ToUtf8String());
        Assert.Equal(command, source[token.Source]);
        Assert.Equal(2, token.Source.Start.Value);
        Assert.Equal(2 + command.Length, token.Source.End.Value);
        Assert.Equal(TokenKind.Semicolon, tokenizer.NextToken().Kind);
        Assert.Equal("tail", source[tokenizer.NextToken().Source]);
        Assert.Equal(TokenKind.EndOfInput, tokenizer.NextToken().Kind);
    }

    [Theory]
    [InlineData("((1)\\\n)")]
    [InlineData("((x=(1+2))\\\n)")]
    [InlineData("((1) + 2))")]
    [InlineData("((1); printf swallowed; ((2))")]
    [InlineData("((x='))))")]
    [InlineData("((x=(1+2))")]
    [InlineData("((x=$(printf &&)))")]
    [InlineData("false && ((x=${a:-))})); printf after")]
    [InlineData("false && ((x=${a:-${b:-))}})); printf after")]
    [InlineData("false && ((x=${a:-1)\\\n)); printf after")]
    [InlineData("false && ((x=${a:-1)2)); printf after")]
    [InlineData("false && ((x=${a:-((1})); printf after")]
    [InlineData("false && ((x=${a:-$((1)); printf after")]
    [InlineData("false && ((x=${a:-$(printf &&)})); printf after")]
    [InlineData("false && ((x=\"${a:-1\")); printf after")]
    public void Parser_RejectsMalformedBoundaries(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("((printf one); printf two)")]
    [InlineData("((printf one); ((1)))")]
    [InlineData("((printf ')'); printf two)")]
    [InlineData("((x=${missing:-)}))")]
    [InlineData("((x=${missing:-(}))")]
    public void Tokenizer_LeavesNestedSubshellPrefixesToTheParser(string source)
    {
        var tokenizer = new Tokenizer(ShellSource.FromText(source));

        Assert.Equal(TokenKind.OpenParen, tokenizer.NextToken().Kind);
        Assert.Equal(1, tokenizer.Position);
        var parsed = Parser.Parse(source);
        Assert.IsType<SubshellCommand>(Assert.Single(parsed.Commands));
    }

    [Fact]
    public void Scanner_SharesTheExpansionDepthBudgetAndReleasesFailedScans()
    {
        var context = new ShellScanContext();
        var depth = ShellSourceCursor.MaximumNestedExpansionDepth;
        Assert.Equal(ShellScanStatus.Complete, Scan(Build(depth - 1)));
        Assert.Equal(ShellScanStatus.Invalid, Scan(Build(depth)));

        for (var index = 0; index < depth + 1; index++)
            Assert.Equal(ShellScanStatus.Invalid, Scan("((x=$(printf &&)))"));
        Assert.Equal(ShellScanStatus.Complete, Scan("((1))"));

        ShellScanStatus Scan(string source) =>
            ArithmeticExpansionScanner.ScanCommand(context, ShellSource.FromText(source), 0, out _, out _);

        static string Build(int count) => "((" + string.Concat(Enumerable.Repeat("$((", count))
            + "1" + string.Concat(Enumerable.Repeat("))", count)) + "))";
    }

    [Fact]
    public void Scanner_TraversesPlainParenthesesWithoutRecursiveGrammarCalls()
    {
        var expression = new string('(', 10_000) + "1" + new string(')', 10_000);
        var source = "((" + expression + "))tail";

        Assert.Equal(ShellScanStatus.Complete,
            ArithmeticExpansionScanner.ScanCommand(new ShellScanContext(), ShellSource.FromText(source), 0, out var end, out var captured));
        Assert.Equal(expression, captured.ToUtf8String());
        Assert.Equal("tail", source[(end + 1)..]);
    }
}
