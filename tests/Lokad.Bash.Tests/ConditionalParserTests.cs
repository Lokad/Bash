using Lokad.Bash.Syntax;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
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
    [InlineData("<")]
    [InlineData("-n >")]
    [InlineData("a == <")]
    [InlineData("a < >")]
    [InlineData("1<2")]
    [InlineData("1\\\n2<3")]
    [InlineData("a =~ a<b")]
    [InlineData("a =~ [;b]")]
    [InlineData("a =~ ([)])")]
    [InlineData("a =~ (b c")]
    public void InvalidStructureFailsDuringParsing(string expression)
    {
        Assert.False(ConditionalParser.TryParse(ShellSource.FromText(expression), out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void StructureRetainsPrecedenceAndOperandQuoteParts()
    {
        Assert.True(ConditionalParser.TryParse(ShellSource.FromText("$x == \"$p\" || ! -n a && x++ -eq 0"),
            out var expression, out _, out var error));
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
    [InlineData("a<b")]
    [InlineData("1 < 2")]
    [InlineData("'1'<2")]
    [InlineData("2147483648>1")]
    public void OrderOperatorsRetainOrdinaryWordOperands(string expression)
    {
        Assert.True(ConditionalParser.TryParse(ShellSource.FromText(expression), out var parsed, out _, out var error));
        Assert.Null(error);
        Assert.IsType<ConditionalExpression.Binary>(parsed);
    }

    [Theory]
    [InlineData("a =~ && b")]
    [InlineData("(a =~ )")]
    public void EmptyRegexOperandCanPrecedeConditionalPunctuation(string expression)
    {
        Assert.True(ConditionalParser.TryParse(ShellSource.FromText(expression), out _, out _, out var error));
        Assert.Null(error);
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

    [Theory]
    [InlineData("[[ ']]' =~ (]]) ]]", "(]])")]
    [InlineData("[[ ']]' =~ (x|]]) ]]", "(x|]])")]
    [InlineData("[[ '#' =~ (#) ]]", "(#)")]
    [InlineData("[[ # inert ]] $( '\n'#' =~ (#) ]]", "(#)")]
    [InlineData("[[ ']]' =~ (]\\\n]) ]]", "(]])")]
    [InlineData("[[ 'a # b' =~ ((a # b)) ]]", "((a # b))")]
    [InlineData("[[ 'a ]] b' =~ (a ]] b) ]]", "(a ]] b)")]
    [InlineData("[[ 'a\nb' =~ (a\nb) ]]", "(a\nb)")]
    [InlineData("[[ 'a&b' =~ (a&b) ]]", "(a&b)")]
    public void RegexContextOwnsItsGroupsCommentsAndClosingBrackets(string source, string expected)
    {
        Assert.True(Parser.TryParse(source, out var script, out var error), error?.Message);
        var command = Assert.IsType<ConditionalCommand>(Assert.Single(script.Commands));
        var expression = Assert.IsType<ConditionalExpression.Binary>(command.Condition);
        Assert.Equal(BinaryPredicateKind.RegexMatch, expression.Predicate.Operator);
        var part = Assert.IsType<SourceWordPart>(Assert.Single(expression.Right.Parts));
        Assert.Equal(expected, part.Source.ToUtf8String());
    }

    [Theory]
    [InlineData("x == x # ]] $( '\n", "x == x \n")]
    [InlineData("x == x # comment\n&& y == y", "x == x \n&& y == y")]
    [InlineData("x == x # trailing without LF", "x == x ")]
    [InlineData("x\\\n\\\ny == xy", "xy == xy")]
    [InlineData("\"x\\\n\\\ny\" == xy", "\"xy\" == xy")]
    [InlineData("$\\\n\\\n'x' == x", "$'x' == x")]
    [InlineData("'x\\\ny' == 'x\\\ny'", "'x\\\ny' == 'x\\\ny'")]
    public void CallerSourceStaysPhysicalWhileReplaySpellingRemovesOnlyItsLexicalEdits(string source, string expected)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        ConditionalCommand? command = null;
        Assert.Null(Record.Exception(() => command = new ConditionalCommand(ShellSource.FromBytes(bytes))));
        Assert.NotNull(command);
        Assert.True(command.Expression.Bytes.Equals(bytes.AsMemory()));
        Assert.Equal(source, command.Expression.ToUtf8String());
        var definition = new FunctionDefinition(new FunctionName(ShellValue.FromText("f"), FunctionNameSyntax.Plain), command);
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(definition, output));
        Assert.Contains("[[ " + expected + " ]]", Utf8Text.Decode(output.ToMemory()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[[ x == x # ignored\n&& ; ]]", ";")]
    [InlineData("[[ x == x &\\\n& ; ]]", ";")]
    [InlineData("[[ 'β' == 'β' # ignored\n&& ; ]]", ";")]
    [InlineData("[[ x == x && ]\\\n]", "]")]
    [InlineData("[[ -n > ]]", ">")]
    [InlineData("[[ 'β' =~ a;b ]]", ";")]
    public void ConditionalErrorsUsePhysicalByteOffsets(string source, string marker)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(source);
        Assert.False(Parser.TryParse(bytes, out _, out var error));
        Assert.Equal(bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(marker)), error.Offset);
    }

    [Fact]
    public void NestedConditionalSubstitutionsShareTheStructuralDepthGuard()
    {
        var source = "printf x";
        for (var index = 0; index < 70; index++) source = "[[ $(" + source + ") ]]";
        Assert.False(Parser.TryParse(source, out _, out var error));
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
