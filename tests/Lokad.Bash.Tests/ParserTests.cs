using System.Collections.Generic;
using System.Text;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("arr= (one two)")]
    [InlineData("declare arr= (one two)")]
    [InlineData("local arr= (one two)")]
    public void Parse_CompoundAssignment_RequiresAdjacentParenthesis(string command)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(command));
    }

    [Theory]
    [InlineData("values=([one two)")]
    [InlineData("values=([[ value ])")]
    [InlineData("declare -a values=([one two)")]
    [InlineData("local values=([[ value ])")]
    public void Parse_CompoundBracketWordRequiresMatchingBracket(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public void Parse_CompoundBracketWordRetainsOneOrdinaryOperand()
    {
        var assignment = Assert.Single(AssertSingleCommand(Parser.Parse("values=([[ value ]] tail)")).Assignments);
        var items = Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues);

        Assert.Equal(2, items.Count);
        Assert.Null(items[0].Element);
        Assert.Equal("[[ value ]]", WordText(items[0].Word));
        Assert.Equal("tail", WordText(items[1].Word));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Parse_UnmatchedBracketWordsDoNotHideALaterAssignment(int count)
    {
        var source = string.Concat(Enumerable.Repeat("x[\n", count)) + "last[0]=value";
        var last = AssertListOperator(Assert.Single(Parser.Parse(source).Commands), ListOperator.Sequence);
        var assignment = Assert.Single(Assert.IsType<SimpleCommand>(last.Right).Assignments);
        Assert.Equal("last[0]", assignment.Target.ToUtf8String());

        var current = last.Left;
        var ordinaryCount = 1;
        while (current is ListCommand list)
        {
            Assert.Equal("x[", WordText(Assert.Single(Assert.IsType<SimpleCommand>(list.Right).Words)));
            current = list.Left;
            ordinaryCount++;
        }
        Assert.Equal("x[", WordText(Assert.Single(Assert.IsType<SimpleCommand>(current).Words)));
        Assert.Equal(count, ordinaryCount);
    }

    [Theory]
    [InlineData("'[[' value ]]")]
    [InlineData("\\[\\[ value ]]")]
    public void Parse_QuotedConditionalOpener_RemainsCommandWord(string source)
    {
        var command = AssertSingleCommand(Parser.Parse(source));

        Assert.Equal(3, command.Words.Count);
        Assert.Equal("[[", WordText(command.Words[0]));
        Assert.Equal("value", WordText(command.Words[1]));
        Assert.Equal("]]", WordText(command.Words[2]));
    }

    [Theory]
    [InlineData("[[]")]
    [InlineData("[[value")]
    [InlineData("[[value]]")]
    public void Parse_NonStandaloneConditionalOpener_RemainsWord(string input)
    {
        var script = Parser.Parse(input);

        var command = AssertSingleCommand(script);
        Assert.Equal(input, WordText(command.Words[0]));
    }

    [Fact]
    public void Parse_ConditionalDelimitersAfterCommandName_RemainWords()
    {
        var script = Parser.Parse("printf '%s\\n' [[ value ]]");

        var command = AssertSingleCommand(script);
        Assert.Equal(5, command.Words.Count);
        Assert.Equal("[[", WordText(command.Words[2]));
        Assert.Equal("value", WordText(command.Words[3]));
        Assert.Equal("]]", WordText(command.Words[4]));
    }

    [Fact]
    public void Parse_ArrayAssignmentWithEarlyClosingBracket_RemainsWord()
    {
        var script = Parser.Parse("arr[foo]bar]=value");

        var command = AssertSingleCommand(script);
        Assert.Empty(command.Assignments);
        Assert.Equal("arr[foo]bar]=value", WordText(command.Words[0]));
    }

    [Theory]
    [InlineData("map[\"foo]bar\"]=value")]
    [InlineData("arr[indexes[0]]=value")]
    [InlineData("map[${key:-foo]bar}]=value")]
    public void Parse_ArrayAssignmentWithNestedClosingBracket_IsCaptured(string input)
    {
        var script = Parser.Parse(input);

        var command = AssertSingleCommand(script);
        Assert.Single(command.Assignments);
        Assert.Empty(command.Words);
    }

    [Theory]
    [InlineData("files=(a b)\\\n")]
    [InlineData("files=(a b)\\\n\\\n")]
    [InlineData("files=(a b)\\\n;")]
    public void Parse_CompoundAssignmentContinuationAtBoundary_RemainsCompound(string source)
    {
        var script = Parser.Parse(source);

        var command = AssertSingleCommand(script);
        var assignment = Assert.Single(command.Assignments);
        Assert.True(assignment.IsCompoundArrayAssignment);
        var values = Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues);
        Assert.Equal("files", assignment.Target.ToUtf8String());
        Assert.Equal(2, values.Count);
        Assert.Equal("a", WordText(values[0].Word));
        Assert.Equal("b", WordText(values[1].Word));
    }

    [Fact]
    public void Parse_CompoundAssignmentContinuationBeforeWord_PreservesAssignment()
    {
        var script = Parser.Parse("files=(a b)\\\n echo done");

        var command = AssertSingleCommand(script);
        Assert.True(Assert.Single(command.Assignments).IsCompoundArrayAssignment);
        Assert.Equal("echo", WordText(command.Words[0]));
        Assert.Equal("done", WordText(command.Words[1]));
    }

    [Theory]
    [InlineData("arr=(one)tail")]
    [InlineData("arr=(one)\\\ntail")]
    public void Parse_ParenthesizedAssignmentWithSuffix_RemainsScalar(string source)
    {
        var script = Parser.Parse(source);

        var command = AssertSingleCommand(script);
        var assignment = Assert.Single(command.Assignments);
        Assert.False(assignment.IsCompoundArrayAssignment);
        Assert.Equal("(one)tail", WordText(assignment.Value));
    }

    [Fact]
    public void Parse_CarriageReturn_RemainsWordContent()
    {
        var script = Parser.Parse("printf '<%s>' a\rb");

        var command = AssertSingleCommand(script);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("a\rb", WordText(command.Words[2]));
    }

    [Theory]
    [InlineData("arr= (one)")]
    [InlineData("arr=\\\n (one)")]
    public void Parse_ArrayAssignmentRequiresLogicalAdjacency(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("arr=(one  two)tail", "(one two)tail")]
    [InlineData("arr=('one two')\\\ntail", "(one two)tail")]
    public void Parse_ParenthesizedScalarJoinsItemsAndPreservesQuotedWhitespace(string source, string expected)
    {
        var assignment = Assert.Single(AssertSingleCommand(Parser.Parse(source)).Assignments);
        Assert.False(assignment.IsCompoundArrayAssignment);
        Assert.Equal(expected, WordText(assignment.Value));
    }

    [Fact]
    public void Parse_ContinuedAndOperator_UsesLogicalCharacters()
    {
        var script = Parser.Parse("true &\\\n& echo ok");

        AssertListOperator(Assert.Single(script.Commands), ListOperator.And);
    }

    [Fact]
    public void Parse_ContinuedOrOperator_UsesLogicalCharacters()
    {
        var script = Parser.Parse("false |\\\n| echo ok");

        AssertListOperator(Assert.Single(script.Commands), ListOperator.Or);
    }

    [Fact]
    public void Parse_ContinuedPipeAndErrorOperator_UsesLogicalCharacters()
    {
        var script = Parser.Parse("echo a |\\\n& cat");

        AssertPipelineOperator(
            Assert.Single(script.Commands),
            PipelineOperator.PipeAndError,
            false);
    }

    [Theory]
    [InlineData("{\\\necho", "{echo")]
    [InlineData("}\\\ntail", "}tail")]
    public void Parse_ContinuedBraceWithoutLogicalBoundary_RemainsWord(
        string source,
        string expected)
    {
        var script = Parser.Parse(source);

        var command = AssertSingleCommand(script);
        Assert.Equal(expected, WordText(command.Words[0]));
    }

    [Theory]
    [InlineData("(\\\n(1+1))")]
    [InlineData("((1+\\\n1))")]
    public void Parse_ArithmeticCommand_RemovesLogicalContinuations(string source)
    {
        var script = Parser.Parse(source);

        var arithmetic = AssertArithmeticCommand(Assert.Single(script.Commands));
        Assert.Equal("1+1", arithmetic.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ArithmeticCommandRequiresAdjacentClosingParentheses()
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse("((1+1)\\\n)"));
    }

    [Fact]
    public void Parse_DeclareCompoundAssignment_IsCapturedAsArgument()
    {
        var command = AssertSingleCommand(Parser.Parse("declare -A map=([a]=1 [b]='two words')"));

        Assert.Equal(3, command.Words.Count);
        Assert.Empty(command.Assignments);
        var assignment = Assert.Single(command.DeclarationAssignments).Value;
        Assert.Equal(assignment, command.DeclarationAssignments[command.Words[2]]);
        Assert.Equal("map", assignment.Target.ToUtf8String());
        var values = Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues);
        Assert.Equal(2, values.Count);
        var first = Assert.IsType<CompoundArrayElement>(values[0].Element);
        Assert.Equal("a", first.Subscript.ToUtf8String());
        Assert.Equal("1", WordText(first.Value));
        var second = Assert.IsType<CompoundArrayElement>(values[1].Element);
        Assert.Equal("b", second.Subscript.ToUtf8String());
        Assert.Equal("two words", WordText(second.Value));
    }

    [Fact]
    public void Parse_WrappedDeclarationScalar_RemainsOrdinaryWord()
    {
        var command = AssertSingleCommand(Parser.Parse("builtin export value=$source"));

        Assert.Equal(3, command.Words.Count);
        Assert.Empty(command.DeclarationAssignments);
        Assert.Empty(command.Assignments);
        Assert.Equal("value=$source", WordText(command.Words[2]));
    }

    [Fact]
    public void Parse_DeclareCompoundAssignment_PreservesQuotedSubscript()
    {
        var command = AssertSingleCommand(Parser.Parse("declare -A map=([\"$key suffix\"]=value)"));
        var assignment = command.DeclarationAssignments[command.Words[2]];
        var values = Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues);
        var item = Assert.IsType<CompoundArrayElement>(Assert.Single(values).Element);

        Assert.Equal("\"$key suffix\"", item.Subscript.ToUtf8String());
        Assert.Equal("value", WordText(item.Value));
    }

    [Fact]
    public void Parse_DeclareElementAssignment_PreservesAssignmentStructure()
    {
        var command = AssertSingleCommand(Parser.Parse("declare map[\"$key\"]=value"));
        var assignment = command.DeclarationAssignments[command.Words[1]];

        Assert.Equal("map[\"$key\"]", assignment.Target.ToUtf8String());
        Assert.Null(assignment.ArrayValues);
        Assert.Equal("value", WordText(assignment.Value));
    }

    [Fact]
    public void Parse_LocalCompoundAssignment_IsCapturedAsArgument()
    {
        var command = AssertSingleCommand(Parser.Parse("local arr=(one two)"));

        Assert.Equal(2, command.Words.Count);
        Assert.Empty(command.Assignments);
        var assignment = command.DeclarationAssignments[command.Words[1]];
        Assert.Equal("arr", assignment.Target.ToUtf8String());
        var values = Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues);
        Assert.Equal(2, values.Count);
        Assert.Equal("one", WordText(values[0].Word));
        Assert.Equal("two", WordText(values[1].Word));
    }

    [Fact]
    public void Parse_DeclareParenthesizedAssignmentWithSuffix_RemainsScalarArgument()
    {
        var command = AssertSingleCommand(Parser.Parse("declare arr=(one)tail"));
        var assignment = command.DeclarationAssignments[command.Words[1]];

        Assert.Null(assignment.ArrayValues);
        Assert.Equal("(one)tail", WordText(assignment.Value));
        Assert.Equal("arr=(one)tail", WordText(command.Words[1]));
    }

    [Theory]
    [InlineData("echo $\\\n((1+1))")]
    [InlineData("echo $(\\\n(1+1))")]
    [InlineData("echo $((1+\\\n1))")]
    [InlineData("echo $((1+1)\\\n)")]
    public void Parse_ArithmeticExpansion_RemovesLogicalContinuations(string source)
    {
        var command = AssertSingleCommand(Parser.Parse(source));

        Assert.Equal("$((1+1))", WordText(command.Words[1]));
    }

    [Fact]
    public void Parse_ParameterExpansion_RemovesContinuedOpener()
    {
        var command = AssertSingleCommand(Parser.Parse("echo $\\\n{value}"));

        Assert.Equal("${value}", WordText(command.Words[1]));
    }

    [Theory]
    [InlineData("[[\\\n x = x ]]", "x = x")]
    [InlineData("[\\\n[ x = x ]]", "x = x")]
    [InlineData("[[ x = x ]\\\n]", "x = x")]
    [InlineData("[\\\n[ x = x ]\\\n]", "x = x")]
    [InlineData("[[ x = x \\\n ]]", "x = x")]
    [InlineData("[[ \"x\\\ny\" = xy ]]", "\"xy\" = xy")]
    [InlineData("[[ 'x\\\ny' = 'x\\\ny' ]]", "'x\\\ny' = 'x\\\ny'")]
    public void Parse_ConditionalCommand_RemovesUnquotedContinuations(
        string source,
        string expected)
    {
        var script = Parser.Parse(source);

        var conditional = AssertConditionalCommand(Assert.Single(script.Commands));
        Assert.Equal(expected, conditional.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ConditionalCommand_PreservesLeadingCarriageReturn()
    {
        var script = Parser.Parse("[[ \rvalue = \rvalue ]]");

        var conditional = AssertConditionalCommand(Assert.Single(script.Commands));
        Assert.Equal("\rvalue = \rvalue", conditional.Expression.ToUtf8String());
    }

    [Theory]
    [InlineData("[[\\\nvalue")]
    [InlineData("[\\\n[value")]
    public void Parse_ContinuedConditionalOpenerWithoutLogicalBoundary_RemainsWord(string source)
    {
        var script = Parser.Parse(source);

        var command = AssertSingleCommand(script);
        Assert.Equal("[[value", WordText(command.Words[0]));
    }

    [Fact]
    public void Parse_CaseCommand_ContinuedTerminatorUsesLogicalCharacters()
    {
        var script = Parser.Parse("case value in value) echo ok ;\\\n; esac");

        var caseCommand = AssertCaseCommand(Assert.Single(script.Commands));
        Assert.Single(caseCommand.Items);
    }

    [Fact]
    public void Parse_ContinuedAppendOperator_UsesLogicalCharacters()
    {
        var command = AssertSingleCommand(Parser.Parse("echo hi >\\\n> out"));

        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.OutputAppend, redirection.Kind);
        Assert.Equal("out", WordText(redirection.Target));
    }

    [Fact]
    public void Parse_MultiplyContinuedAppendOperator_UsesLogicalCharacters()
    {
        var command = AssertSingleCommand(Parser.Parse("echo hi &\\\n>\\\n> out"));

        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.OutputAndErrorAppend, redirection.Kind);
        Assert.Equal("out", WordText(redirection.Target));
    }

    [Fact]
    public void Parse_SimpleWords_AreCaptured()
    {
        var script = Parser.Parse(Utf8("echo hello world"));

        var command = AssertSingleCommand(script);
        Assert.Empty(command.Assignments);
        Assert.Empty(command.Redirections);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        Assert.Equal("hello", WordText(command.Words[1]));
        Assert.Equal("world", WordText(command.Words[2]));
    }

    [Fact]
    public void Parse_LeadingAssignments_AreCaptured()
    {
        var script = Parser.Parse(Utf8("A=1 B='2 3' echo hi"));

        var command = AssertSingleCommand(script);
        Assert.Equal(2, command.Assignments.Count);
        Assert.Equal("A", command.Assignments[0].Target.ToUtf8String());
        Assert.Equal("1", WordText(command.Assignments[0].Value));
        Assert.Equal("B", command.Assignments[1].Target.ToUtf8String());
        Assert.Equal("2 3", WordText(command.Assignments[1].Value));
        Assert.Equal(2, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        Assert.Equal("hi", WordText(command.Words[1]));
    }

    [Fact]
    public void Parse_Redirections_AreCaptured()
    {
        var script = Parser.Parse(Utf8("cat < input > output"));

        var command = AssertSingleCommand(script);
        Assert.Equal(2, command.Redirections.Count);
        Assert.Equal(RedirectionKind.Input, command.Redirections[0].Kind);
        Assert.Equal("input", WordText(command.Redirections[0].Target));
        Assert.Equal(RedirectionKind.Output, command.Redirections[1].Kind);
        Assert.Equal("output", WordText(command.Redirections[1].Target));
    }

    [Fact]
    public void Parse_Comments_AreIgnored()
    {
        var script = Parser.Parse(Utf8("echo hi # c\nwho"));

        Assert.Single(script.Commands);
        var list = AssertListOperator(script.Commands[0], ListOperator.Sequence);
        var left = Assert.IsType<SimpleCommand>(list.Left);
        Assert.Equal(2, left.Words.Count);
        Assert.Equal("echo", WordText(left.Words[0]));
        Assert.Equal("hi", WordText(left.Words[1]));
        var right = Assert.IsType<SimpleCommand>(list.Right);
        Assert.Single(right.Words);
        Assert.Equal("who", WordText(right.Words[0]));
    }

    [Fact]
    public void Parse_QuotedWords_KeepQuoteKinds()
    {
        var script = Parser.Parse(Utf8("echo 'a b' \"c d\""));

        var command = AssertSingleCommand(script);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        AssertParts(command.Words[1], WordPartKind.SingleQuoted, "a b");
        AssertParts(command.Words[2], WordPartKind.DoubleQuoted, "c d");
    }

    [Fact]
    public void Parse_SingleQuotedGlob_IsSingleQuotedWordPart()
    {
        var script = Parser.Parse(Utf8("rg -g '*.txt' hello /"));

        var command = AssertSingleCommand(script);
        Assert.Equal(5, command.Words.Count);
        Assert.Equal("rg", WordText(command.Words[0]));
        Assert.Equal("-g", WordText(command.Words[1]));
        AssertParts(command.Words[2], WordPartKind.SingleQuoted, "*.txt");
        Assert.Equal("hello", WordText(command.Words[3]));
        Assert.Equal("/", WordText(command.Words[4]));
    }

    [Fact]
    public void Parse_LineContinuations_AreRemoved()
    {
        var script = Parser.Parse(Utf8("echo a\\\n b"));

        var command = AssertSingleCommand(script);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        Assert.Equal("a", WordText(command.Words[1]));
        Assert.Equal("b", WordText(command.Words[2]));
    }

    [Fact]
    public void Parse_ListSequence_SemicolonBuildsList()
    {
        var script = Parser.Parse(Utf8("echo a; echo b"));

        Assert.Single(script.Commands);
        AssertListOperator(script.Commands[0], ListOperator.Sequence);
    }

    [Fact]
    public void Parse_ListSequence_NewlineBuildsList()
    {
        var script = Parser.Parse(Utf8("echo a\n echo b"));

        Assert.Single(script.Commands);
        AssertListOperator(script.Commands[0], ListOperator.Sequence);
    }

    [Fact]
    public void Parse_ListLeftAssociativity_ForAndOr()
    {
        var script = Parser.Parse(Utf8("echo a && echo b || echo c"));

        Assert.Single(script.Commands);
        var root = AssertListOperator(script.Commands[0], ListOperator.Or);
        var left = AssertListOperator(root.Left, ListOperator.And);
        Assert.IsType<SimpleCommand>(left.Left);
        Assert.IsType<SimpleCommand>(left.Right);
        Assert.IsType<SimpleCommand>(root.Right);
    }

    [Fact]
    public void Parse_Pipeline_BasicPipe()
    {
        var script = Parser.Parse(Utf8("echo a | cat"));

        Assert.Single(script.Commands);
        var pipeline = AssertPipelineOperator(script.Commands[0], PipelineOperator.Pipe, false);
        Assert.IsType<SimpleCommand>(pipeline.Left);
        Assert.IsType<SimpleCommand>(pipeline.Right);
    }

    [Fact]
    public void Parse_Pipeline_PipeAndError()
    {
        var script = Parser.Parse(Utf8("echo a |& cat"));

        Assert.Single(script.Commands);
        var pipeline = AssertPipelineOperator(script.Commands[0], PipelineOperator.PipeAndError, false);
        Assert.IsType<SimpleCommand>(pipeline.Left);
        Assert.IsType<SimpleCommand>(pipeline.Right);
    }

    [Fact]
    public void Parse_Pipeline_Negated()
    {
        var script = Parser.Parse(Utf8("! echo a | cat"));

        Assert.Single(script.Commands);
        AssertPipelineOperator(script.Commands[0], PipelineOperator.Pipe, true);
    }

    [Fact]
    public void Parse_Pipeline_LeftAssociativity()
    {
        var script = Parser.Parse(Utf8("echo a | cat | wc"));

        Assert.Single(script.Commands);
        var root = AssertPipelineOperator(script.Commands[0], PipelineOperator.Pipe, false);
        var left = AssertPipelineOperator(root.Left, PipelineOperator.Pipe, false);
        Assert.IsType<SimpleCommand>(left.Left);
        Assert.IsType<SimpleCommand>(left.Right);
        Assert.IsType<SimpleCommand>(root.Right);
    }

    [Fact]
    public void Parse_Subshell_WrapsCommand()
    {
        var script = Parser.Parse(Utf8("(echo a)"));

        Assert.Single(script.Commands);
        var subshell = AssertCompoundCommand<SubshellCommand>(script.Commands[0]);
        var body = Assert.IsType<SimpleCommand>(subshell.Body);
        Assert.Equal("echo", WordText(body.Words[0]));
    }

    [Theory]
    [InlineData("{ echo a; }")]
    [InlineData("{\n\n echo a;\n\n}")]
    [InlineData("{\\\n echo a; }")]
    [InlineData("{ echo a; }\\\n")]
    public void Parse_Group_WrapsCommand(string source)
    {
        var script = Parser.Parse(source);

        Assert.Single(script.Commands);
        var group = AssertCompoundCommand<GroupCommand>(script.Commands[0]);
        var body = Assert.IsType<SimpleCommand>(group.Body);
        Assert.Equal("echo", WordText(body.Words[0]));
    }

    [Theory]
    [InlineData("{ ; echo ok; }")]
    [InlineData("( ; echo ok )")]
    [InlineData("{ echo a; ; echo b; }")]
    [InlineData("; echo ok")]
    [InlineData("echo a; ; echo b")]
    [InlineData("echo $(; echo ok)")]
    [InlineData("for value in item; ; do echo ok; done")]
    [InlineData("for ((i=0;i<1;i++)); ; do echo ok; done")]
    [InlineData("case value in ; esac")]
    [InlineData("case value in value) ; esac")]
    [InlineData("case value in value) ; ;; esac")]
    public void Parse_RejectsEmptySemicolonCommand(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData(";; echo hidden")]
    [InlineData("echo visible ;; echo hidden")]
    [InlineData("echo visible ;;& echo hidden")]
    [InlineData("echo visible ) echo hidden")]
    public void Parse_RejectsUnconsumedTopLevelTokens(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("true &&; if true; then true; fi")]
    [InlineData("printf x |; while true; do break; done")]
    public void Parse_SemicolonCannotContinueOperator(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("!\nfalse")]
    [InlineData("! # independent command follows\nfalse")]
    [InlineData("!; false")]
    public void Parse_StandaloneNegation_DoesNotConsumeNextCommand(string source)
    {
        var script = Parser.Parse(source);
        var sequence = AssertListOperator(Assert.Single(script.Commands), ListOperator.Sequence);
        var negation = Assert.IsType<NegatedCommand>(sequence.Left);
        Assert.Empty(Assert.IsType<SimpleCommand>(negation.Inner).Words);
        Assert.Equal("false", WordText(Assert.IsType<SimpleCommand>(sequence.Right).Words[0]));
    }

    [Theory]
    [InlineData("( true; ! )")]
    [InlineData("{ true; ! }")]
    public void Parse_StandaloneNegation_RequiresARealTerminator(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public void Parse_Subshell_ListBody()
    {
        var script = Parser.Parse(Utf8("(echo a; echo b)"));

        Assert.Single(script.Commands);
        var subshell = AssertCompoundCommand<SubshellCommand>(script.Commands[0]);
        AssertListOperator(subshell.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_Group_ListBody()
    {
        var script = Parser.Parse(Utf8("{ echo a\n echo b; }"));

        Assert.Single(script.Commands);
        var group = AssertCompoundCommand<GroupCommand>(script.Commands[0]);
        AssertListOperator(group.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_If_Basic()
    {
        var script = Parser.Parse(Utf8("if echo a; then echo b; fi"));

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 0, expectElse: false);
        Assert.IsType<SimpleCommand>(ifCommand.Condition);
        Assert.IsType<SimpleCommand>(ifCommand.ThenBody);
    }

    [Fact]
    public void Parse_If_WithElse()
    {
        var script = Parser.Parse(Utf8("if echo a; then echo b; else echo c; fi"));

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 0, expectElse: true);
        Assert.IsType<SimpleCommand>(ifCommand.Condition);
        Assert.IsType<SimpleCommand>(ifCommand.ThenBody);
        Assert.IsType<SimpleCommand>(ifCommand.ElseBody!);
    }

    [Fact]
    public void Parse_If_WithElif()
    {
        var script = Parser.Parse(Utf8("if echo a; then echo b; elif echo c; then echo d; fi"));

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 1, expectElse: false);
        var elif = ifCommand.ElifClauses[0];
        Assert.IsType<SimpleCommand>(elif.Condition);
        Assert.IsType<SimpleCommand>(elif.Body);
    }

    [Fact]
    public void Parse_While_Basic()
    {
        var script = Parser.Parse(Utf8("while echo a; do echo b; done"));

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<WhileCommand>(script.Commands[0]);
        Assert.IsType<SimpleCommand>(loop.Condition);
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_Until_Basic()
    {
        var script = Parser.Parse(Utf8("until echo a; do echo b; done"));

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<UntilCommand>(script.Commands[0]);
        Assert.IsType<SimpleCommand>(loop.Condition);
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_While_BodyList()
    {
        var script = Parser.Parse(Utf8("while echo a; do echo b; echo c; done"));

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<WhileCommand>(script.Commands[0]);
        AssertListOperator(loop.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_For_Basic()
    {
        var script = Parser.Parse(Utf8("for x in a b; do echo $x; done"));

        Assert.Single(script.Commands);
        var loop = AssertForCommand(script.Commands[0]);
        Assert.Equal("x", Text(loop.Name));
        Assert.Equal(2, loop.Items.Count);
        Assert.Equal("a", WordText(loop.Items[0]));
        Assert.Equal("b", WordText(loop.Items[1]));
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_For_BodyList()
    {
        var script = Parser.Parse(Utf8("for name in one; do echo $name; echo ok; done"));

        Assert.Single(script.Commands);
        var loop = AssertForCommand(script.Commands[0]);
        Assert.Equal("name", Text(loop.Name));
        AssertListOperator(loop.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_For_WithGlobbedItemsAndIfBody()
    {
        var script = Parser.Parse(Utf8("for d in */ .*/; do if [ -d \"$d\" ]; then echo \"$d\"; fi; done"));

        Assert.NotEmpty(script.Commands);
    }

    [Fact]
    public void Parse_ForWithNestedIfAndFor_BasenameSuffixScenario()
    {
        var script = Parser.Parse(Utf8("for d in clean/tmp1/sales-snapshots/2019/2019-10-*; do if [ -d \"$d\" ]; then date=$(basename \"$d\"); for f in \"$d\"/*.cfg; do [ -e \"$f\" ] || continue; base=$(basename \"$f\" .cfg); mv \"$f\" \"clean/tmp1/sales-snapshots/2019/${base}_${date}.cfg\"; done; fi; done"));

        Assert.NotEmpty(script.Commands);
    }

    [Fact]
    public void Parse_ArithmeticCommand_Basic()
    {
        var script = Parser.Parse(Utf8("((1+2))"));

        Assert.Single(script.Commands);
        var arithmetic = AssertArithmeticCommand(script.Commands[0]);
        Assert.Equal("1+2", arithmetic.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ArithmeticCommand_PreservesWhitespace()
    {
        var script = Parser.Parse(Utf8("((a + b))"));

        Assert.Single(script.Commands);
        var arithmetic = AssertArithmeticCommand(script.Commands[0]);
        Assert.Equal("a + b", arithmetic.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ConditionalCommand_Basic()
    {
        var script = Parser.Parse(Utf8("[[ -f file ]]"));

        Assert.Single(script.Commands);
        var conditional = AssertConditionalCommand(script.Commands[0]);
        Assert.Equal("-f file", conditional.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ConditionalCommand_PreservesWhitespace()
    {
        var script = Parser.Parse(Utf8("[[ a == b ]]"));

        Assert.Single(script.Commands);
        var conditional = AssertConditionalCommand(script.Commands[0]);
        Assert.Equal("a == b", conditional.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_FunctionDefinition_NameParenForm()
    {
        var script = Parser.Parse(Utf8("myfunc () { echo hi; }"));

        Assert.Single(script.Commands);
        var function = AssertFunctionDefinition(script.Commands[0]);
        Assert.Equal("myfunc", Text(function.Name.Value.ToUtf8String()));
        Assert.IsType<GroupCommand>(function.Body);
    }

    [Fact]
    public void Parse_FunctionDefinition_FunctionKeywordForm()
    {
        var script = Parser.Parse(Utf8("function other { echo hi; }"));

        Assert.Single(script.Commands);
        var function = AssertFunctionDefinition(script.Commands[0]);
        Assert.Equal("other", Text(function.Name.Value.ToUtf8String()));
        Assert.IsType<GroupCommand>(function.Body);
    }

    [Fact]
    public void Parse_ArithmeticForCommand_Basic()
    {
        var script = Parser.Parse(Utf8("for ((i=0;i<3;i++)); do echo $i; done"));

        Assert.Single(script.Commands);
        var loop = AssertArithmeticForCommand(script.Commands[0]);
        Assert.Equal("i=0;i<3;i++", loop.Expression.ToUtf8String());
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_CaseCommand_SinglePattern()
    {
        var script = Parser.Parse(Utf8("case $x in a) echo a ;; esac"));

        Assert.Single(script.Commands);
        var caseCommand = AssertCaseCommand(script.Commands[0]);
        Assert.Equal("$x", WordText(caseCommand.Subject));
        Assert.Single(caseCommand.Items);
        Assert.Single(caseCommand.Items[0].Patterns);
        Assert.Equal("a", WordText(caseCommand.Items[0].Patterns[0]));
        Assert.IsType<SimpleCommand>(caseCommand.Items[0].Body);
    }

    [Fact]
    public void Parse_CaseCommand_MultiplePatterns()
    {
        var script = Parser.Parse(Utf8("case $x in a|b) echo ok ;; esac"));

        Assert.Single(script.Commands);
        var caseCommand = AssertCaseCommand(script.Commands[0]);
        Assert.Single(caseCommand.Items);
        Assert.Equal(2, caseCommand.Items[0].Patterns.Count);
        Assert.Equal("a", WordText(caseCommand.Items[0].Patterns[0]));
        Assert.Equal("b", WordText(caseCommand.Items[0].Patterns[1]));
    }

    [Theory]
    [InlineData("case value in value)")]
    [InlineData("case value in value) ;;")]
    [InlineData("case value in value) next) ;; esac")]
    [InlineData("case value in value) true && ;; esac")]
    [InlineData("case value in value) true | ;; esac")]
    public void Parse_CaseCommand_RejectsIncompleteItems(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public void Parse_Redirection_ForceOutput()
    {
        var script = Parser.Parse(Utf8("echo hi >| out"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.ForceOutput, command.Redirections[0].Kind);
        Assert.Equal("out", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_DuplicateOutput()
    {
        var script = Parser.Parse(Utf8("echo hi 2>&1"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.DuplicateOutput, command.Redirections[0].Kind);
        Assert.Equal(2, command.Redirections[0].FileDescriptor);
        Assert.Equal("1", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_PreservesNumericOptionValue()
    {
        var command = AssertSingleCommand(Parser.Parse(
            "build-script /script.txt --timeout 600 > /tmp/run.log"));

        Assert.Equal(new[] { "build-script", "/script.txt", "--timeout", "600" },
            command.Words.Select(WordText));
        var redirection = Assert.Single(command.Redirections);
        Assert.Null(redirection.FileDescriptor);
        Assert.Equal("/tmp/run.log", WordText(redirection.Target));
    }

    [Theory]
    [InlineData("600 >out", "600")]
    [InlineData("600\t>out", "600")]
    [InlineData("600\\\n >out", "600")]
    [InlineData("600 \\\n>out", "600")]
    [InlineData("6\\\n00 >out", "600")]
    [InlineData("'600'>out", "600")]
    [InlineData("\"600\">out", "600")]
    [InlineData("\\600>out", "600")]
    [InlineData("2147483648 >out", "2147483648")]
    public void Parse_Redirection_PreservesSeparatedOrQuotedNumber(string suffix, string expected)
    {
        var command = AssertSingleCommand(Parser.Parse("echo " + suffix));

        Assert.Equal(new[] { "echo", expected }, command.Words.Select(WordText));
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.Output, redirection.Kind);
        Assert.Null(redirection.FileDescriptor);
        Assert.Equal("out", WordText(redirection.Target));
    }

    [Theory]
    [InlineData("2>out", 2)]
    [InlineData("2\\\n>out", 2)]
    [InlineData("2\\\n\\\n>out", 2)]
    [InlineData("1\\\n2>out", 12)]
    public void Parse_Redirection_RecognizesLogicallyAdjacentDescriptor(string suffix, int descriptor)
    {
        var command = AssertSingleCommand(Parser.Parse("echo " + suffix));

        Assert.Equal("echo", WordText(Assert.Single(command.Words)));
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.Output, redirection.Kind);
        Assert.Equal(descriptor, redirection.FileDescriptor);
        Assert.Equal("out", WordText(redirection.Target));
    }

    [Theory]
    [InlineData("(echo ok) 2 >out")]
    [InlineData("{ echo ok; } 2\\\n >out")]
    public void Parse_Redirection_RejectsSeparatedDescriptorAfterCompoundCommand(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public void Parse_Redirection_OutputAndError()
    {
        var script = Parser.Parse(Utf8("echo hi &> out"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.OutputAndError, command.Redirections[0].Kind);
        Assert.Equal("out", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_CloseInput()
    {
        var script = Parser.Parse(Utf8("echo hi 3<&-"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.CloseInput, command.Redirections[0].Kind);
        Assert.Equal(3, command.Redirections[0].FileDescriptor);
        Assert.Equal("-", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_AllocateOutput()
    {
        var script = Parser.Parse(Utf8("echo hi {fd}> out"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.AllocateOutput, command.Redirections[0].Kind);
        Assert.Equal("fd", Text(ReadVariableName(command.Redirections[0])));
    }

    [Fact]
    public void Parse_Redirection_AllocateInput()
    {
        var script = Parser.Parse(Utf8("echo hi {in}< input"));

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.AllocateInput, command.Redirections[0].Kind);
        Assert.Equal("in", Text(ReadVariableName(command.Redirections[0])));
    }

    [Theory]
    [InlineData("cat <(find / -type f)", 4)]
    [InlineData("cat >(sed s/a/b/)", 4)]
    [InlineData("while read f; do echo \"$f\"; done < <(find / -type f)", 35)]
    public void Parse_ProcessSubstitution_ReportsUnsupportedConstruct(string command, int offset)
    {
        var error = Assert.Throws<SyntaxException>(() => Parser.Parse(Utf8(command)));

        Assert.Equal("process substitution is not supported", error.Message);
        Assert.Equal(offset, error.Offset);
    }

    /// <summary>
    /// Passes through a string value for parser inputs.
    /// </summary>
    private static string Utf8(string value) => value;

    /// <summary>
    /// Asserts the script has a single command and returns it as a simple command.
    /// </summary>
    private static SimpleCommand AssertSingleCommand(ParsedScript script)
    {
        Assert.Single(script.Commands);
        return Assert.IsType<SimpleCommand>(script.Commands[0]);
    }

    /// <summary>
    /// Asserts a list command and verifies its operator.
    /// </summary>
    private static ListCommand AssertListOperator(ICommand command, ListOperator expectedOperator)
    {
        var list = Assert.IsType<ListCommand>(command);
        Assert.Equal(expectedOperator, list.Operator);
        return list;
    }

    /// <summary>
    /// Asserts a pipeline command and verifies operator/negation flags.
    /// </summary>
    private static PipelineCommand AssertPipelineOperator(
        ICommand command,
        PipelineOperator expectedOperator,
        bool expectedNegated)
    {
        var pipeline = Assert.IsType<PipelineCommand>(command);
        Assert.Equal(expectedOperator, pipeline.Operator);
        Assert.Equal(expectedNegated, pipeline.Negated);
        return pipeline;
    }

    /// <summary>
    /// Asserts a compound command has the expected type.
    /// </summary>
    private static T AssertCompoundCommand<T>(ICommand command) where T : ICommand
    {
        return Assert.IsType<T>(command);
    }

    /// <summary>
    /// Asserts an if command structure and returns it for further inspection.
    /// </summary>
    private static IfCommand AssertIfCommand(ICommand command, int expectedElifCount, bool expectElse)
    {
        var ifCommand = Assert.IsType<IfCommand>(command);
        Assert.Equal(expectedElifCount, ifCommand.ElifClauses.Count);
        if (expectElse)
        {
            Assert.NotNull(ifCommand.ElseBody);
        }
        else
        {
            Assert.Null(ifCommand.ElseBody);
        }

        return ifCommand;
    }

    /// <summary>
    /// Asserts a loop command has the expected type.
    /// </summary>
    private static T AssertLoopCommand<T>(ICommand command) where T : ICommand
    {
        return Assert.IsType<T>(command);
    }

    /// <summary>
    /// Asserts a for-in loop command.
    /// </summary>
    private static ForCommand AssertForCommand(ICommand command)
    {
        return Assert.IsType<ForCommand>(command);
    }

    /// <summary>
    /// Asserts an arithmetic command.
    /// </summary>
    private static ArithmeticCommand AssertArithmeticCommand(ICommand command)
    {
        return Assert.IsType<ArithmeticCommand>(command);
    }

    /// <summary>
    /// Asserts a conditional command.
    /// </summary>
    private static ConditionalCommand AssertConditionalCommand(ICommand command)
    {
        return Assert.IsType<ConditionalCommand>(command);
    }

    /// <summary>
    /// Asserts a function definition command.
    /// </summary>
    private static FunctionDefinition AssertFunctionDefinition(ICommand command)
    {
        return Assert.IsType<FunctionDefinition>(command);
    }

    /// <summary>
    /// Asserts an arithmetic for-loop command.
    /// </summary>
    private static ArithmeticForCommand AssertArithmeticForCommand(ICommand command)
    {
        return Assert.IsType<ArithmeticForCommand>(command);
    }

    /// <summary>
    /// Asserts a case command.
    /// </summary>
    private static CaseCommand AssertCaseCommand(ICommand command)
    {
        return Assert.IsType<CaseCommand>(command);
    }

    /// <summary>
    /// Reads the variable name from a redirection for assertion helpers.
    /// </summary>
    private static string ReadVariableName(Redirection redirection)
    {
        return redirection.VariableName;
    }

    /// <summary>
    /// Flattens a word into text by concatenating its parts.
    /// </summary>
    private static string WordText(Word word)
    {
        var builder = new StringBuilder();
        foreach (var part in word.Parts)
        {
            builder.Append(Text(part.GetUtf8Text()));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Asserts a single word part and compares its kind and text.
    /// </summary>
    private static void AssertParts(Word word, WordPartKind kind, string content)
    {
        Assert.Single(word.Parts);
        Assert.Equal(kind, word.Parts[0].Kind);
        Assert.Equal(content, Text(word.Parts[0].GetUtf8Text()));
    }

    /// <summary>
    /// Passes through a string value for assertions.
    /// </summary>
    private static string Text(string content) => content;
}
