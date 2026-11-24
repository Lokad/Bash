using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public class ParserTests
{
    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "${v:-value}")]
    [InlineData(true, "$((2+3))")]
    public void Parse_ContiguousWordPartOwnsExactByteBacking(bool quoted, string expansion)
    {
        byte[] expected = [.. Encoding.ASCII.GetBytes(new string('x', 4096) + expansion), 0xff, (byte)'\r'];
        byte[] source = [.. (quoted ? ": \""u8 : ": "u8), .. expected, .. (quoted ? "\"\n"u8 : "\n"u8)];
        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse(source).Commands));
        var part = Assert.Single(command.Words[1].Parts);

        Assert.Equal(quoted ? WordPartKind.DoubleQuoted : WordPartKind.Unquoted, part.Kind);
        var bytes = part.GetUnexpandedValue().Bytes;
        Assert.Equal(expected, bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(bytes, out var backing));
        Assert.NotNull(backing.Array);
        Assert.NotSame(source, backing.Array);
        Assert.Equal(expected.Length, backing.Array.Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Parse_ScalarAssignmentOwnsExactNameBacking(bool append, bool continued)
    {
        var name = new string('x', 513);
        var spelling = continued ? name.Insert(255, "\\\n") : name;
        var operation = append ? "+=" : "=";
        byte[] source = [.. Encoding.ASCII.GetBytes(spelling + operation), 0xff, .. "\" tail\"\n"u8];

        var command = AssertSingleCommand(Parser.Parse(source));
        var assignment = Assert.Single(command.Assignments);
        Assert.Equal(append ? AssignmentOperator.Append : AssignmentOperator.Replace, assignment.Operator);
        Assert.Equal(name, assignment.Target.ToUtf8String());
        Assert.True(MemoryMarshal.TryGetArray(assignment.Target.Bytes, out var backing));
        Assert.NotNull(backing.Array);
        Assert.Equal(name.Length, backing.Array.Length);
        Assert.IsType<WordPart[]>(assignment.Value.Parts);
        Assert.Collection(assignment.Value.Parts,
            part => Assert.Equal(new byte[] { 0xff }, part.GetUnexpandedValue().Bytes.ToArray()),
            part => Assert.Equal(" tail", part.GetUnexpandedValue().ToUtf8String()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssignmentSuffixAdmitsItsPartBacking(bool keepLeadingPart)
    {
        var leading = Assert.IsType<SourceWordPart>(WordPart.FromText(
            WordPartKind.Unquoted, keepLeadingPart ? "x=lead" : "x="));
        var quoted = WordPart.FromText(WordPartKind.DoubleQuoted, "quoted");
        var literal = WordPart.FromText(WordPartKind.SingleQuoted, "literal");
        var original = new Word([leading, quoted, literal]);
        var count = keepLeadingPart ? 3 : 2;
        var bytes = MemoryCapacity.ArrayBackingBytes<WordPart>(count);
        var governor = new MemoryGovernor(bytes);

        using (var owner = governor.CreateScope())
        using (var syntax = new MemoryCapacity(owner, 0))
        {
            using (owner.Reserve(1))
            {
                Assert.Throws<MemoryBudgetExceededException>(() => original.AfterSourcePrefix(leading, 2, syntax));
                Assert.Equal(0, syntax.Bytes);
                Assert.Equal(1, governor.ReservedBytes);
            }

            var suffix = original.AfterSourcePrefix(leading, 2, syntax);
            var parts = Assert.IsType<WordPart[]>(suffix.Parts);
            Assert.Equal(count, parts.Length);
            Assert.Equal(bytes, syntax.Bytes);
            Assert.Same(quoted, parts[^2]);
            Assert.Same(literal, parts[^1]);
            if (keepLeadingPart)
                Assert.Equal("lead", parts[0].GetUnexpandedValue().ToUtf8String());
        }

        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SimpleCommandsShareOnlyTheirAbsentCollections(bool streamed)
    {
        var ordinary = Read("printf ordinary");
        var assignment = Read("value=kept");
        var redirection = Read(">output");
        var mixed = Read("value=kept printf ordinary >output");

        Assert.Empty(ordinary.Assignments);
        Assert.Same(ordinary.Assignments, redirection.Assignments);
        Assert.Empty(assignment.Words);
        Assert.Same(assignment.Words, redirection.Words);
        Assert.Empty(ordinary.Redirections);
        Assert.Same(ordinary.Redirections, assignment.Redirections);

        Assert.Equal("ordinary", WordText(ordinary.Words[1]));
        Assert.Equal("value", Assert.Single(assignment.Assignments).Target.ToUtf8String());
        Assert.Equal("output", WordText(Assert.Single(redirection.Redirections).Target));
        Assert.Single(mixed.Assignments);
        Assert.Equal(2, mixed.Words.Count);
        Assert.Single(mixed.Redirections);
        Assert.NotSame(ordinary.Words, mixed.Words);
        Assert.NotSame(assignment.Assignments, mixed.Assignments);
        Assert.NotSame(redirection.Redirections, mixed.Redirections);

        SimpleCommand Read(string source) => streamed
            ? Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(
                Assert.Single(Parser.TryParseCommandUnits(Encoding.UTF8.GetBytes(source)))).Command)
            : AssertSingleCommand(Parser.Parse(source));
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_EmptyCompoundInitializersShareTheirItemList(bool streamed)
    {
        var empty = ReadItems("items=()");
        var commentOnly = ReadItems("items=( # comment\n)");
        var populated = ReadItems("items=(one two)");

        Assert.Empty(empty);
        Assert.Same(empty, commentOnly);
        Assert.Equal(2, populated.Count);

        IReadOnlyList<CompoundArrayItem> ReadItems(string source)
        {
            var command = streamed
                ? Assert.IsType<SimpleCommand>(Assert.IsType<CommandUnitParseResult.Parsed>(
                    Assert.Single(Parser.TryParseCommandUnits(Encoding.UTF8.GetBytes(source)))).Command)
                : AssertSingleCommand(Parser.Parse(source));
            return Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(Assert.Single(command.Assignments).ArrayValues);
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Parse_ClosedBracketWordsWithoutAssignmentsPreserveALaterAssignment(int count)
    {
        var source = string.Concat(Enumerable.Repeat("x[]\n", count)) + "last[0]=value";
        var last = AssertListOperator(Assert.Single(Parser.Parse(source).Commands), ListOperator.Sequence);
        var assignment = Assert.Single(Assert.IsType<SimpleCommand>(last.Right).Assignments);
        Assert.Equal("last[0]", assignment.Target.ToUtf8String());

        var current = last.Left;
        var ordinaryCount = 1;
        while (current is ListCommand list)
        {
            Assert.Equal("x[]", WordText(Assert.Single(Assert.IsType<SimpleCommand>(list.Right).Words)));
            current = list.Left;
            ordinaryCount++;
        }
        Assert.Equal("x[]", WordText(Assert.Single(Assert.IsType<SimpleCommand>(current).Words)));
        Assert.Equal(count, ordinaryCount);
    }

    [Fact]
    public void Parse_IncompleteAssignmentSelectorReportsTheEndOfInput()
    {
        const string source = "probe[0\nprintf discarded\n";

        Assert.False(Parser.TryParse(source, out _, out var error));
        Assert.Equal(new SyntaxError("Unterminated array subscript.", source.Length), error);
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Parse_ArrayAssignmentKeepsCompactNameAndPhysicalSelector(bool continued, bool append)
    {
        var name = new string('x', 513);
        var spelling = continued ? name.Insert(250, "\\\n") + "\\\n" : name;
        byte[] selector = [.. "[\"part\\\n"u8, 0xff, (byte)'\r', .. "\"]"u8];
        var operation = append ? "+=" : "=";
        byte[] input = [.. Encoding.ASCII.GetBytes(spelling), .. selector,
            .. Encoding.ASCII.GetBytes(operation), .. "'value part'\n"u8];

        var command = AssertSingleCommand(Parser.Parse(input));
        var assignment = Assert.Single(command.Assignments);
        byte[] expectedTarget = [.. Encoding.ASCII.GetBytes(name), .. selector];
        Assert.Empty(command.Words);
        Assert.Equal(append ? AssignmentOperator.Append : AssignmentOperator.Replace, assignment.Operator);
        Assert.Equal(expectedTarget, assignment.Target.Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(assignment.Target.Bytes, out var backing));
        Assert.NotNull(backing.Array);
        Assert.Equal(expectedTarget.Length, backing.Array.Length);
        Assert.Equal("value part", WordText(assignment.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_ArrayAssignmentReusesTheTokenizedValue(bool compound)
    {
        var prefix = compound ? "a=([0]=" : "declare a[0]=";
        var ending = compound ? "' tail')\n" : "' tail'\n";
        byte[] value = [.. Encoding.ASCII.GetBytes(new string('x', 4096)), 0xff, (byte)'\r'];
        byte[] input = [.. Encoding.ASCII.GetBytes(prefix), .. value, .. Encoding.ASCII.GetBytes(ending)];
        var command = AssertSingleCommand(Parser.Parse(input));
        Word original;
        Word rhs;
        if (compound)
        {
            var assignment = Assert.Single(command.Assignments);
            var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CompoundArrayItem>>(assignment.ArrayValues));
            original = item.Word;
            rhs = Assert.IsType<CompoundArrayElement>(item.Element).Value;
            Assert.IsType<WordPart[]>(rhs.Parts);
        }
        else
        {
            var assignment = Assert.Single(command.DeclarationAssignments);
            original = assignment.Key;
            rhs = assignment.Value.Value;
        }

        Assert.Equal(2, rhs.Parts.Count);
        Assert.Equal(value, rhs.Parts[0].GetUnexpandedValue().Bytes.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(original.Parts[0].GetUnexpandedValue().Bytes, out var source));
        Assert.True(MemoryMarshal.TryGetArray(rhs.Parts[0].GetUnexpandedValue().Bytes, out var suffix));
        Assert.NotNull(source.Array);
        Assert.Same(source.Array, suffix.Array);
        Assert.Equal(" tail", rhs.Parts[1].GetUnexpandedValue().ToUtf8String());
        Assert.Same(original.Parts[1], rhs.Parts[1]);
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
        var script = Parser.Parse("echo hello world");

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
        var script = Parser.Parse("A=1 B='2 3' echo hi");

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
        var script = Parser.Parse("cat < input > output");

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
        var script = Parser.Parse("echo hi # c\nwho");

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
        var script = Parser.Parse("echo 'a b' \"c d\"");

        var command = AssertSingleCommand(script);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        AssertParts(command.Words[1], WordPartKind.SingleQuoted, "a b");
        AssertParts(command.Words[2], WordPartKind.DoubleQuoted, "c d");
    }

    [Fact]
    public void Parse_SingleQuotedGlob_IsSingleQuotedWordPart()
    {
        var script = Parser.Parse("rg -g '*.txt' hello /");

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
        var script = Parser.Parse("echo a\\\n b");

        var command = AssertSingleCommand(script);
        Assert.Equal(3, command.Words.Count);
        Assert.Equal("echo", WordText(command.Words[0]));
        Assert.Equal("a", WordText(command.Words[1]));
        Assert.Equal("b", WordText(command.Words[2]));
    }

    [Fact]
    public void Parse_ListSequence_SemicolonBuildsList()
    {
        var script = Parser.Parse("echo a; echo b");

        Assert.Single(script.Commands);
        AssertListOperator(script.Commands[0], ListOperator.Sequence);
    }

    [Fact]
    public void Parse_ListSequence_NewlineBuildsList()
    {
        var script = Parser.Parse("echo a\n echo b");

        Assert.Single(script.Commands);
        AssertListOperator(script.Commands[0], ListOperator.Sequence);
    }

    [Fact]
    public void Parse_ListLeftAssociativity_ForAndOr()
    {
        var script = Parser.Parse("echo a && echo b || echo c");

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
        var script = Parser.Parse("echo a | cat");

        Assert.Single(script.Commands);
        var pipeline = AssertPipelineOperator(script.Commands[0], PipelineOperator.Pipe, false);
        Assert.IsType<SimpleCommand>(pipeline.Left);
        Assert.IsType<SimpleCommand>(pipeline.Right);
    }

    [Fact]
    public void Parse_Pipeline_PipeAndError()
    {
        var script = Parser.Parse("echo a |& cat");

        Assert.Single(script.Commands);
        var pipeline = AssertPipelineOperator(script.Commands[0], PipelineOperator.PipeAndError, false);
        Assert.IsType<SimpleCommand>(pipeline.Left);
        Assert.IsType<SimpleCommand>(pipeline.Right);
    }

    [Fact]
    public void Parse_Pipeline_Negated()
    {
        var script = Parser.Parse("! echo a | cat");

        Assert.Single(script.Commands);
        AssertPipelineOperator(script.Commands[0], PipelineOperator.Pipe, true);
    }

    [Fact]
    public void Parse_Pipeline_LeftAssociativity()
    {
        var script = Parser.Parse("echo a | cat | wc");

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
        var script = Parser.Parse("(echo a)");

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

    [Fact]
    public void Parse_GroupDistinguishesUnquotedBraceArgumentsFromDelimiters()
    {
        var group = AssertCompoundCommand<GroupCommand>(
            Assert.Single(Parser.Parse("{ printf '<%s>' { }\\\n; }").Commands));
        var command = Assert.IsType<SimpleCommand>(group.Body);

        Assert.Equal(["printf", "<%s>", "{", "}"], command.Words.Select(WordText));
        Assert.All(command.Words.Skip(2), word =>
            Assert.Equal(WordPartKind.Unquoted, Assert.Single(word.Parts).Kind));
    }

    [Theory]
    [InlineData("}")]
    [InlineData("{ printf value }")]
    [InlineData("{ printf value };")]
    [InlineData("{ :; }; }")]
    public void Parse_GroupStillRequiresStructuralClosingBrace(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("if (true) >out then :; fi")]
    [InlineData("if (true) | true then :; fi")]
    [InlineData("if (true) && true then :; fi")]
    [InlineData("if (true) 'then' :; fi")]
    [InlineData("{ (true) >out }")]
    public void Parse_CompoundCloserDoesNotSupplyBoundaryAfterLaterWord(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
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
    [InlineData("echo visible ;& echo hidden")]
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
    [InlineData("( printf inner; time )")]
    [InlineData("case x in x) printf inner; time ;; esac")]
    public void Parse_StandalonePipelinePrefix_RequiresARealTerminator(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Fact]
    public void Parse_Subshell_ListBody()
    {
        var script = Parser.Parse("(echo a; echo b)");

        Assert.Single(script.Commands);
        var subshell = AssertCompoundCommand<SubshellCommand>(script.Commands[0]);
        AssertListOperator(subshell.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_Group_ListBody()
    {
        var script = Parser.Parse("{ echo a\n echo b; }");

        Assert.Single(script.Commands);
        var group = AssertCompoundCommand<GroupCommand>(script.Commands[0]);
        AssertListOperator(group.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_If_Basic()
    {
        var script = Parser.Parse("if echo a; then echo b; fi");

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 0, expectElse: false);
        Assert.IsType<SimpleCommand>(ifCommand.Condition);
        Assert.IsType<SimpleCommand>(ifCommand.ThenBody);
    }

    [Fact]
    public void Parse_If_WithElse()
    {
        var script = Parser.Parse("if echo a; then echo b; else echo c; fi");

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 0, expectElse: true);
        Assert.IsType<SimpleCommand>(ifCommand.Condition);
        Assert.IsType<SimpleCommand>(ifCommand.ThenBody);
        Assert.IsType<SimpleCommand>(ifCommand.ElseBody!);
    }

    [Fact]
    public void Parse_If_WithElif()
    {
        var script = Parser.Parse("if echo a; then echo b; elif echo c; then echo d; fi");

        Assert.Single(script.Commands);
        var ifCommand = AssertIfCommand(script.Commands[0], expectedElifCount: 1, expectElse: false);
        var elif = ifCommand.ElifClauses[0];
        Assert.IsType<SimpleCommand>(elif.Condition);
        Assert.IsType<SimpleCommand>(elif.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_CompoundCommands_ShareEmptyCollections(bool streamed)
    {
        var firstIf = Assert.IsType<IfCommand>(ParseOne("if :; then :; fi"));
        var secondIf = Assert.IsType<IfCommand>(ParseOne("if :; then :; else :; fi"));
        Assert.Empty(firstIf.ElifClauses);
        Assert.Same(firstIf.ElifClauses, secondIf.ElifClauses);

        var firstLoop = Assert.IsType<ForCommand>(ParseOne("for x in; do :; done"));
        var secondLoop = Assert.IsType<ForCommand>(ParseOne("for y in\ndo :; done"));
        Assert.Empty(firstLoop.Items);
        Assert.Same(firstLoop.Items, secondLoop.Items);

        var firstCase = Assert.IsType<CaseCommand>(ParseOne("case x in esac"));
        var secondCase = Assert.IsType<CaseCommand>(ParseOne("case y in\nesac"));
        Assert.Empty(firstCase.Items);
        Assert.Same(firstCase.Items, secondCase.Items);

        ICommand ParseOne(string source) => streamed
            ? Assert.IsType<CommandUnitParseResult.Parsed>(
                Assert.Single(Parser.TryParseCommandUnits(Encoding.UTF8.GetBytes(source)))).Command
            : Assert.Single(Parser.Parse(source).Commands);
    }

    [Fact]
    public void Parse_ImplicitForListsShareReadOnlyQuotedPositionalSyntax()
    {
        var prepared = Assert.IsType<ForCommand>(Assert.Single(Parser.Parse("for x; do :; done").Commands));
        var unit = Assert.IsType<CommandUnitParseResult.Parsed>(
            Assert.Single(Parser.TryParseCommandUnits("for y do :; done"u8.ToArray())));
        var streamed = Assert.IsType<ForCommand>(unit.Command);
        var braceBody = Assert.IsType<ForCommand>(Assert.Single(Parser.Parse("for z\n{ :; }").Commands));

        Assert.Same(prepared.Items, streamed.Items);
        Assert.Same(prepared.Items, braceBody.Items);
        var word = Assert.Single(prepared.Items);
        var part = Assert.Single(word.Parts);
        Assert.Equal(WordPartKind.DoubleQuoted, part.Kind);
        Assert.Equal("$@", WordText(word));
        var items = Assert.IsAssignableFrom<IList<Word>>(prepared.Items);
        var parts = Assert.IsAssignableFrom<IList<WordPart>>(word.Parts);
        Assert.Throws<NotSupportedException>(() => items[0] = new Word([]));
        Assert.Throws<NotSupportedException>(() => parts[0] = WordPart.FromText(WordPartKind.SingleQuoted, "replacement"));
    }

    [Fact]
    public void Parse_EmptyCaseArmsShareAnUnlocatedReadOnlyBody()
    {
        var prepared = Parser.Parse("case x in x) ;& y) ;;& z) $missing;; esac");
        var selection = Assert.IsType<CaseCommand>(Assert.Single(prepared.Commands));
        var unit = Assert.IsType<CommandUnitParseResult.Parsed>(
            Assert.Single(Parser.TryParseCommandUnits("case y in y) esac"u8.ToArray())));
        var streamed = Assert.IsType<CaseCommand>(unit.Command);

        var empty = Assert.IsType<SimpleCommand>(selection.Items[0].Body);
        var present = Assert.IsType<SimpleCommand>(selection.Items[2].Body);
        Assert.Same(empty, selection.Items[1].Body);
        Assert.Same(empty, Assert.Single(streamed.Items).Body);
        Assert.Equal(CaseContinuation.FallThrough, selection.Items[0].Continuation);
        Assert.Equal(CaseContinuation.Retest, selection.Items[1].Continuation);
        Assert.Equal(CaseContinuation.Stop, streamed.Items[0].Continuation);
        var preparedLocations = Assert.IsType<CommandLocations>(prepared.Locations);
        var streamedLocations = Assert.IsType<CommandLocations>(unit.Locations);
        Assert.False(preparedLocations.TryGet(empty, out _));
        Assert.False(streamedLocations.TryGet(empty, out _));
        Assert.True(preparedLocations.TryGet(present, out _));
        var word = Assert.Single(present.Words);
        var declarations = Assert.IsAssignableFrom<IDictionary<Word, AssignmentWord>>(empty.DeclarationAssignments);
        Assert.Throws<NotSupportedException>(() => declarations.Add(word, default));
    }

    [Fact]
    public void Parse_While_Basic()
    {
        var script = Parser.Parse("while echo a; do echo b; done");

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<WhileCommand>(script.Commands[0]);
        Assert.IsType<SimpleCommand>(loop.Condition);
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_Until_Basic()
    {
        var script = Parser.Parse("until echo a; do echo b; done");

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<UntilCommand>(script.Commands[0]);
        Assert.IsType<SimpleCommand>(loop.Condition);
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_While_BodyList()
    {
        var script = Parser.Parse("while echo a; do echo b; echo c; done");

        Assert.Single(script.Commands);
        var loop = AssertLoopCommand<WhileCommand>(script.Commands[0]);
        AssertListOperator(loop.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_For_Basic()
    {
        var script = Parser.Parse("for x in a b; do echo $x; done");

        Assert.Single(script.Commands);
        var loop = AssertForCommand(script.Commands[0]);
        Assert.Equal("x", loop.Name);
        Assert.Equal(2, loop.Items.Count);
        Assert.Equal("a", WordText(loop.Items[0]));
        Assert.Equal("b", WordText(loop.Items[1]));
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_For_BodyList()
    {
        var script = Parser.Parse("for name in one; do echo $name; echo ok; done");

        Assert.Single(script.Commands);
        var loop = AssertForCommand(script.Commands[0]);
        Assert.Equal("name", loop.Name);
        AssertListOperator(loop.Body, ListOperator.Sequence);
    }

    [Fact]
    public void Parse_For_WithGlobbedItemsAndIfBody()
    {
        var script = Parser.Parse("for d in */ .*/; do if [ -d \"$d\" ]; then echo \"$d\"; fi; done");

        Assert.NotEmpty(script.Commands);
    }

    [Fact]
    public void Parse_ForWithNestedIfAndFor_BasenameSuffixScenario()
    {
        var script = Parser.Parse("for d in clean/tmp1/sales-snapshots/2019/2019-10-*; do if [ -d \"$d\" ]; then date=$(basename \"$d\"); for f in \"$d\"/*.cfg; do [ -e \"$f\" ] || continue; base=$(basename \"$f\" .cfg); mv \"$f\" \"clean/tmp1/sales-snapshots/2019/${base}_${date}.cfg\"; done; fi; done");

        Assert.NotEmpty(script.Commands);
    }

    [Fact]
    public void Parse_ArithmeticCommand_Basic()
    {
        var script = Parser.Parse("((1+2))");

        Assert.Single(script.Commands);
        var arithmetic = AssertArithmeticCommand(script.Commands[0]);
        Assert.Equal("1+2", arithmetic.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ArithmeticCommand_PreservesWhitespace()
    {
        var script = Parser.Parse("((a + b))");

        Assert.Single(script.Commands);
        var arithmetic = AssertArithmeticCommand(script.Commands[0]);
        Assert.Equal("a + b", arithmetic.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ConditionalCommand_Basic()
    {
        var script = Parser.Parse("[[ -f file ]]");

        Assert.Single(script.Commands);
        var conditional = AssertConditionalCommand(script.Commands[0]);
        Assert.Equal("-f file", conditional.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_ConditionalCommand_PreservesWhitespace()
    {
        var script = Parser.Parse("[[ a == b ]]");

        Assert.Single(script.Commands);
        var conditional = AssertConditionalCommand(script.Commands[0]);
        Assert.Equal("a == b", conditional.Expression.ToUtf8String());
    }

    [Fact]
    public void Parse_FunctionDefinition_NameParenForm()
    {
        var script = Parser.Parse("myfunc () { echo hi; }");

        Assert.Single(script.Commands);
        var function = AssertFunctionDefinition(script.Commands[0]);
        Assert.Equal("myfunc", function.Name.Value.ToUtf8String());
        Assert.IsType<GroupCommand>(function.Body);
    }

    [Fact]
    public void Parse_FunctionDefinition_FunctionKeywordForm()
    {
        var script = Parser.Parse("function other { echo hi; }");

        Assert.Single(script.Commands);
        var function = AssertFunctionDefinition(script.Commands[0]);
        Assert.Equal("other", function.Name.Value.ToUtf8String());
        Assert.IsType<GroupCommand>(function.Body);
    }

    [Theory]
    [InlineData("f() printf wrong")]
    [InlineData("function f printf wrong")]
    [InlineData("function f() x=wrong")]
    [InlineData("function f () >out")]
    [InlineData("f() g(){ :; }")]
    [InlineData("function f function g(){ :; }")]
    [InlineData("f() ! { :; }")]
    [InlineData("function f time { :; }")]
    [InlineData("function f (\n) { :; }")]
    public void Parse_FunctionBodyRequiresACompoundCommand(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
    }

    [Theory]
    [InlineData("function f (printf yes)")]
    [InlineData("function f ( printf yes )")]
    [InlineData("function f (\nprintf yes\n)")]
    [InlineData("function f ( # body\nprintf yes\n)")]
    [InlineData("function f\n(printf yes)")]
    [InlineData("function f () (printf yes)")]
    [InlineData("function f (printf yes) >out")]
    public void Parse_FunctionOptionalHeaderRetainsTheSubshellBody(string source)
    {
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));
        var body = function.Body is RedirectedCommand redirected ? redirected.Body : function.Body;

        Assert.IsType<SubshellCommand>(body);
    }

    [Theory]
    [InlineData("function f { :; }", typeof(GroupCommand))]
    [InlineData("f() ( : )", typeof(SubshellCommand))]
    [InlineData("function f if true; then :; fi", typeof(IfCommand))]
    [InlineData("f() while false; do :; done", typeof(WhileCommand))]
    [InlineData("function f until true; do :; done", typeof(UntilCommand))]
    [InlineData("f() for x in one; do :; done", typeof(ForCommand))]
    [InlineData("function f for ((i=0;i<1;i++)); do :; done", typeof(ArithmeticForCommand))]
    [InlineData("f() case x in x) :;; esac", typeof(CaseCommand))]
    [InlineData("function f ((x=3))", typeof(ArithmeticCommand))]
    [InlineData("f() [[ x == x ]]", typeof(ConditionalCommand))]
    public void Parse_FunctionBodiesKeepAllSupportedCompoundKinds(string source, Type expectedBody)
    {
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(source).Commands));

        Assert.Equal(expectedBody, function.Body.GetType());
    }

    [Fact]
    public void Parse_ArithmeticForCommand_Basic()
    {
        var script = Parser.Parse("for ((i=0;i<3;i++)); do echo $i; done");

        Assert.Single(script.Commands);
        var loop = AssertArithmeticForCommand(script.Commands[0]);
        Assert.Equal("i=0;i<3;i++", loop.Expression.ToUtf8String());
        Assert.IsType<SimpleCommand>(loop.Body);
    }

    [Fact]
    public void Parse_CaseCommand_SinglePattern()
    {
        var script = Parser.Parse("case $x in a) echo a ;; esac");

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
        var script = Parser.Parse("case $x in a|b) echo ok ;; esac");

        Assert.Single(script.Commands);
        var caseCommand = AssertCaseCommand(script.Commands[0]);
        Assert.Single(caseCommand.Items);
        Assert.Equal(2, caseCommand.Items[0].Patterns.Count);
        Assert.Equal("a", WordText(caseCommand.Items[0].Patterns[0]));
        Assert.Equal("b", WordText(caseCommand.Items[0].Patterns[1]));
    }

    [Fact]
    public void Parse_CaseCommand_ParenthesizedPatternsAfterSubjectLine()
    {
        var command = AssertCaseCommand(Assert.Single(Parser.Parse(
            "case $x # comment\n\nin (esac|\"a b\") :;; esac").Commands));

        Assert.Equal("$x", WordText(command.Subject));
        var item = Assert.Single(command.Items);
        Assert.Collection(item.Patterns, first => Assert.Equal("esac", WordText(first)),
            second => Assert.Equal("a b", WordText(second)));
    }

    [Theory]
    [InlineData("for x in a; { :; }", false)]
    [InlineData("for x\n{ :\n}", false)]
    [InlineData("for ((i=0;i<1;i++)) { :; }", true)]
    public void Parse_ForCommand_LowersBraceBodyToExistingLoopBody(string source, bool arithmetic)
    {
        var loop = Assert.Single(Parser.Parse(source).Commands);
        var body = arithmetic ? AssertArithmeticForCommand(loop).Body : AssertForCommand(loop).Body;

        Assert.IsType<SimpleCommand>(body);
    }

    [Theory]
    [InlineData("for x { :; }")]
    [InlineData("for x in a { :; }")]
    [InlineData("for x in a; { : }")]
    [InlineData("for x in a; { }")]
    [InlineData("for ((i=0;i<1;i++)); { : }")]
    [InlineData("case x; in (x) :;; esac")]
    [InlineData("case\nx in (x) :;; esac")]
    [InlineData("case x in (\nx) :;; esac")]
    [InlineData("case x in () :;; esac")]
    public void Parse_ForAndCase_RejectMisplacedTerminators(string source)
    {
        Assert.Throws<SyntaxException>(() => Parser.Parse(source));
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
        var script = Parser.Parse("echo hi >| out");

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.ForceOutput, command.Redirections[0].Kind);
        Assert.Equal("out", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_DuplicateOutput()
    {
        var script = Parser.Parse("echo hi 2>&1");

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
        var script = Parser.Parse("echo hi &> out");

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.OutputAndError, command.Redirections[0].Kind);
        Assert.Equal("out", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_CloseInput()
    {
        var script = Parser.Parse("echo hi 3<&-");

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.CloseInput, command.Redirections[0].Kind);
        Assert.Equal(3, command.Redirections[0].FileDescriptor);
        Assert.Equal("-", WordText(command.Redirections[0].Target));
    }

    [Fact]
    public void Parse_Redirection_AllocateOutput()
    {
        var script = Parser.Parse("echo hi {fd}> out");

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.AllocateOutput, command.Redirections[0].Kind);
        Assert.Equal("fd", ReadVariableName(command.Redirections[0]));
    }

    [Fact]
    public void Parse_Redirection_AllocateInput()
    {
        var script = Parser.Parse("echo hi {in}< input");

        var command = AssertSingleCommand(script);
        Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.AllocateInput, command.Redirections[0].Kind);
        Assert.Equal("in", ReadVariableName(command.Redirections[0]));
    }

    [Theory]
    [InlineData(">out", RedirectionKind.AllocateOutput)]
    [InlineData("<input", RedirectionKind.AllocateInput)]
    [InlineData(">>out", RedirectionKind.OutputAppend)]
    [InlineData("<>out", RedirectionKind.InputOutput)]
    [InlineData(">|out", RedirectionKind.ForceOutput)]
    [InlineData(">&3", RedirectionKind.DuplicateOutput)]
    [InlineData("<&3", RedirectionKind.DuplicateInput)]
    [InlineData(">&-", RedirectionKind.CloseOutput)]
    [InlineData("<&-", RedirectionKind.CloseInput)]
    [InlineData("<<<seed", RedirectionKind.HereString)]
    [InlineData("<<END\nseed\nEND\n", RedirectionKind.HereDocument)]
    [InlineData("<<-END\n\tseed\n\tEND\n", RedirectionKind.HereDocumentStripTabs)]
    public void Parse_AllocatedModesRetainExistingOperatorAndVariableMetadata(string suffix, RedirectionKind kind)
    {
        var command = AssertSingleCommand(Parser.Parse(": {fd}" + suffix));
        Assert.Single(command.Words);
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(kind, redirection.Kind);
        Assert.Equal("fd", redirection.VariableName);
        Assert.Null(redirection.FileDescriptor);
    }

    [Theory]
    [InlineData("{fd} >out")]
    [InlineData("{fd}\t>out")]
    [InlineData("'{fd}'>out")]
    [InlineData("\"{fd}\">out")]
    [InlineData("\\{fd}>out")]
    [InlineData("{fd}&>out")]
    [InlineData("{fd}&>>out")]
    public void Parse_AllocationRequiresAnAdjacentUnquotedNameAndSupportedOperator(string operand)
    {
        var command = AssertSingleCommand(Parser.Parse(": " + operand));
        Assert.Equal(2, command.Words.Count);
        Assert.Empty(Assert.Single(command.Redirections).VariableName);
    }

    [Fact]
    public void Parse_TrailingGroupAllocationUsesTheSameOperatorGrammar()
    {
        var command = Assert.IsType<RedirectedCommand>(Assert.Single(Parser.Parse("{ :; } {fd}>>out").Commands));
        var redirection = Assert.Single(command.Redirections);
        Assert.Equal(RedirectionKind.OutputAppend, redirection.Kind);
        Assert.Equal("fd", redirection.VariableName);
    }

    [Theory]
    [InlineData("cat <(find / -type f)", 4)]
    [InlineData("cat >(sed s/a/b/)", 4)]
    [InlineData("while read f; do echo \"$f\"; done < <(find / -type f)", 35)]
    public void Parse_ProcessSubstitution_ReportsUnsupportedConstruct(string command, int offset)
    {
        var error = Assert.Throws<SyntaxException>(() => Parser.Parse(command));

        Assert.Equal("process substitution is not supported", error.Message);
        Assert.Equal(offset, error.Offset);
    }

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
            builder.Append(part.GetUtf8Text());
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
        Assert.Equal(content, word.Parts[0].GetUtf8Text());
    }
}
