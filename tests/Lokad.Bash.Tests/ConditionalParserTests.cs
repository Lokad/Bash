using Lokad.Bash.Syntax;
using static Lokad.Bash.Evaluation.ShellPredicates;

namespace Lokad.Bash.Tests;

public sealed class ConditionalParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("-n")]
    [InlineData("!")]
    [InlineData("( -n a")]
    [InlineData("\"unterminated")]
    [InlineData("a '==' a")]
    [InlineData("'-n' a")]
    [InlineData("a &&")]
    public void InvalidStructureFailsDuringParsing(string expression)
    {
        Assert.False(ConditionalParser.TryParse(ShellSource.FromText(expression), out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void StructureRetainsPrecedenceAndOperandQuoteParts()
    {
        Assert.True(ConditionalParser.TryParse(ShellSource.FromText("$x == \"$p\" || ! -n a && x++ -eq 0"),
            out var expression, out var error));
        Assert.Null(error);
        var or = Assert.IsType<ConditionalExpression.Logical>(expression);
        Assert.False(or.And);
        var equality = Assert.IsType<ConditionalExpression.Binary>(or.Left);
        Assert.Equal(BinaryPredicateKind.StringEqual, equality.Predicate.Operator);
        Assert.Equal(WordPartKind.DoubleQuoted, Assert.IsType<SourceWordPart>(Assert.Single(equality.Right.Parts)).Kind);
        var and = Assert.IsType<ConditionalExpression.Logical>(or.Right);
        Assert.True(and.And);
        Assert.IsType<ConditionalExpression.Negated>(and.Left);
        Assert.Equal(BinaryPredicateKind.NumericEqual, Assert.IsType<ConditionalExpression.Binary>(and.Right).Predicate.Operator);
    }

    [Theory]
    [InlineData("[[ a==b ]]", "a==b")]
    [InlineData("[[ ']]' ]]", "']]'")]
    [InlineData("[[ $(printf ']]') ]]", "$(printf ']]')")]
    [InlineData("[[ $'\\377' ]]", "$'\\377'")]
    public void OperandSyntaxIsNotSplitOrDecoded(string source, string expression)
    {
        var command = Assert.IsType<ConditionalCommand>(Assert.Single(Parser.Parse(source).Commands));
        Assert.Equal(expression, command.Expression.ToUtf8String());
        Assert.IsType<ConditionalExpression.NonEmpty>(command.Condition);
    }

    [Theory]
    [InlineData("[[ 1 =~ [[:digit:]] ]]", "[[:digit:]]")]
    [InlineData("[[ ab =~ (a|b)+ ]]", "(a|b)+")]
    [InlineData("[[ ')' =~ [[:alpha:]()] ]]", "[[:alpha:]()]")]
    [InlineData("[[ ']' =~ []] ]]", "[]]")]
    public void RegexWordsKeepTheirBracketsAndGrouping(string source, string pattern)
    {
        var command = Assert.IsType<ConditionalCommand>(Assert.Single(Parser.Parse(source).Commands));
        var binary = Assert.IsType<ConditionalExpression.Binary>(command.Condition);
        Assert.Equal(BinaryPredicateKind.RegexMatch, binary.Predicate.Operator);
        Assert.Equal(pattern, Assert.IsType<SourceWordPart>(Assert.Single(binary.Right.Parts)).Source.ToUtf8String());
    }
}
