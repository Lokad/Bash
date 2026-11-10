using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public class ShellCommandParserTests
{
    [Theory]
    [InlineData("cat ${missing:-$(echo nope)}")]
    [InlineData("cat \"${missing:-$(echo nope)}\"")]
    [InlineData("cat \"${missing:-$(echo nope}\"")]
    [InlineData("cat \"${outer:-${missing:-$(echo nope)}}\"")]
    [InlineData("declare value=\"${missing:-$(echo nope)}\"")]
    [InlineData("declare values[${missing:-$(echo nope)}]=value")]
    [InlineData("declare -a values=(${missing:-$(echo nope)})")]
    public void TryParse_NestedCommandSubstitution_ReturnsNull(string command)
    {
        var result = ShellCommandParser.TryParse(command);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_NestedParameterExpansionWithoutCommandSubstitution_ReturnsCommand()
    {
        var result = ShellCommandParser.TryParse("cat \"${outer:-${missing:-fallback}}\"");

        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(new[] { "${outer:-${missing:-fallback}}" }, result.Value.Arguments);
    }

    [Theory]
    [InlineData("export arr=($(printf hidden))")]
    [InlineData("declare -a values=(\"${missing:-$(echo hidden)}\")")]
    [InlineData("declare -a values=([$(echo hidden)]=value)")]
    [InlineData("declare -a values=(\"$((1 + $(echo hidden)))\")")]
    [InlineData("declare arr=($(echo hidden))tail")]
    public void TryParse_CompoundDeclaration_DoesNotFlattenOnlyItsTarget(string script)
    {
        Assert.Null(ShellCommandParser.TryParse(script));
        Assert.Null(ShellCommandParser.TryParsePipeline(script + " | cat"));
        var chain = ShellCommandParser.TryParseChain(script);
        Assert.NotNull(chain);
        var segment = Assert.Single(chain);
        Assert.Equal(script, segment.Text);
        Assert.Null(segment.Stages);
    }

    [Fact]
    public void TryParse_DeclarationAssignment_ReturnsUnquotedArgument()
    {
        var result = ShellCommandParser.TryParse("declare value='two words'");
        Assert.NotNull(result);
        Assert.Equal("declare", result.Value.Name);
        Assert.Equal(new[] { "value=two words" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_DeclarationElementAssignment_ReturnsUnquotedSubscript()
    {
        var result = ShellCommandParser.TryParse("declare map[\"a b\"]=value");
        Assert.NotNull(result);
        Assert.Equal("declare", result.Value.Name);
        Assert.Equal(new[] { "map[a b]=value" }, result.Value.Arguments);
    }

    [Theory]
    [InlineData("declare -a values=(one 'two words')", "values=(one two words)")]
    [InlineData("declare -a arr=(one two)", "arr=(one two)")]
    [InlineData("declare -a values=()", "values=()")]
    [InlineData("declare -a values+=([2]='two words')", "values+=([2]=two words)")]
    [InlineData("declare -a values=('$source' \"$source\" '$(echo hidden)')", "values=($source $source $(echo hidden))")]
    public void TryParse_CompoundDeclarationAssignment_ReturnsUnquotedArgument(string script, string argument)
    {
        var expected = new[] { "-a", argument };
        var result = ShellCommandParser.TryParse(script);
        Assert.NotNull(result);
        Assert.Equal("declare", result.Value.Name);
        Assert.Equal(expected, result.Value.Arguments);

        var pipeline = ShellCommandParser.TryParsePipeline(script + " | cat");
        Assert.NotNull(pipeline);
        Assert.Equal(2, pipeline.Count);
        Assert.Equal("declare", pipeline[0].Name);
        Assert.Equal(expected, pipeline[0].Arguments);
        Assert.Equal("cat", pipeline[1].Name);

        var chain = ShellCommandParser.TryParseChain(script + "; true");
        Assert.NotNull(chain);
        Assert.Equal(2, chain.Count);
        Assert.Equal(script, chain[0].Text);
        var stages = chain[0].Stages;
        Assert.NotNull(stages);
        Assert.Equal(expected, Assert.Single(stages).Arguments);
    }

    [Theory]
    [InlineData("declare arr=(one)tail", "arr=(one)tail")]
    [InlineData("local arr+=(one)tail", "arr+=(one)tail")]
    public void TryParse_DeclarationScalarSuffix_PreservesTheWholeWord(string script, string argument)
    {
        var result = ShellCommandParser.TryParse(script);
        Assert.NotNull(result);
        Assert.Equal(new[] { argument }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_DeclarationAssignmentWithCommandSubstitution_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("declare value=$(echo nope)");
        Assert.Null(result);
    }

    [Theory]
    [InlineData("declare values[$(echo nope)]=value")]
    [InlineData("declare -a values[$(echo nope)]=(value)")]
    public void TryParse_DeclarationAssignmentTargetWithCommandSubstitution_ReturnsNull(string command)
    {
        var result = ShellCommandParser.TryParse(command);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_SimpleCommand_ReturnsNameAndArgs()
    {
        var result = ShellCommandParser.TryParse("ls -la /");
        Assert.NotNull(result);
        Assert.Equal("ls", result.Value.Name);
        Assert.Equal(new[] { "-la", "/" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_SingleQuotedArg_Unquoted()
    {
        var result = ShellCommandParser.TryParse("ls '/docs'");
        Assert.NotNull(result);
        Assert.Equal("ls", result.Value.Name);
        Assert.Equal(new[] { "/docs" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_DoubleQuotedArg_Unquoted()
    {
        var result = ShellCommandParser.TryParse("cat \"/some/path\"");
        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(new[] { "/some/path" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_EscapedArg_Unquoted()
    {
        var result = ShellCommandParser.TryParse(@"cat a\""b");
        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(new[] { "a\"b" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_MixedQuoting_Unquoted()
    {
        // 'a'"b" → ab
        var result = ShellCommandParser.TryParse("echo 'a'\"b\"");
        Assert.NotNull(result);
        Assert.Equal("echo", result.Value.Name);
        Assert.Equal(new[] { "ab" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_Pipeline_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls | cat");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Sequence_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls; cat");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_CommandSubstitution_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls $(echo foo)");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_LegacyBacktickCommandSubstitution_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls `echo foo`");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_CommandSubstitutionInsideBracedParameter_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls ${missing:-$(echo foo)}");
        Assert.Null(result);
    }

    // Display contract: headers sharing a physical body region cannot be split into independent slices.
    [Theory]
    [InlineData("cat <<END; printf after\nbody\nEND", 0)]
    [InlineData("cat <<ONE && cat <<TWO\nfirst\nONE\nsecond\nTWO", 0)]
    [InlineData("false <<ONE || cat <<TWO\nfirst\nONE\nsecond\nTWO", 0)]
    [InlineData("cat <<END &&\nbody\nEND\ncat", 0)]
    [InlineData("cat <<END;\nbody\nEND", 1)]
    [InlineData("cat <<END # ; fake\nbody\nEND", 1)]
    [InlineData("cat <<END | cat\nbody\nEND", 2)]
    [InlineData("{ cat; } <<END; true\nbody\nEND", 0)]
    [InlineData("f(){ cat <<END; }; f\nbody\nEND", 0)]
    [InlineData("cat <<END; true && false; printf after\nbody\nEND", 0)]
    [InlineData("cat <<END; for x in a; do cat; done\nbody\nEND", 0)]
    [InlineData("cat <<ONE; cat <<TWO; true\nfirst\nONE\nsecond\nTWO", 0)]
    [InlineData("cat <<END; true\nbody\rvalue\nEND", 0)]
    public void TryParseChain_SharedHereDocumentRegionRetainsAllHeadersAndBodies(string region, int stageCount)
    {
        var result = ShellCommandParser.TryParseChain("ls before; " + region + "\nls after");

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("ls before", result[0].Text);
        Assert.Equal(region, result[1].Text);
        Assert.Equal("ls after", result[2].Text);
        var stages = result[1].Stages;
        if (stageCount == 0) Assert.Null(stages);
        else
        {
            Assert.NotNull(stages);
            Assert.Equal(stageCount, stages.Count);
        }
        Assert.Single(Lokad.Bash.Syntax.Parser.Parse(result[1].Text).Commands);
    }

    [Theory]
    [InlineData("cat <<END &&\nbody\nEND\n")]
    [InlineData("cat <<END; ; true\nbody\nEND\n")]
    [InlineData("cat <<ONE; cat <<TWO\nfirst\nONE\nmissing second\n")]
    public void TryParseChain_IncompleteSharedRegionRejectsTheWholeChain(string source) =>
        Assert.Null(ShellCommandParser.TryParseChain("ls before; " + source));

    [Theory]
    [InlineData("cat \"$((1 + $(echo nope)))\"")]
    [InlineData("cat \"${value:-$((1 + $(echo nope)))}\"")]
    [InlineData("declare values[$((1 + $(echo nope)))]=value")]
    public void TryParse_CommandSubstitutionInsideArithmetic_ReturnsNull(string command)
    {
        Assert.Null(ShellCommandParser.TryParse(command));
    }

    [Fact]
    public void TryParse_ArithmeticWithoutCommandSubstitution_ReturnsCommand()
    {
        var result = ShellCommandParser.TryParse("cat \"$((1 + 2))\"");

        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(["$((1 + 2))"], result.Value.Arguments);
    }

    [Fact]
    public void TryParse_LegacyBacktickCommandSubstitutionInsideBracedParameter_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("ls ${missing:-`echo foo`}");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_Subshell_ReturnsNull()
    {
        var result = ShellCommandParser.TryParse("(ls /)");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_RgWithGlobs_ReturnsNameAndArgs()
    {
        var result = ShellCommandParser.TryParse("rg -n --glob '*.txt' 'workspace-[^/\"\\s]*/' /");
        Assert.NotNull(result);
        Assert.Equal("rg", result.Value.Name);
        Assert.Equal(new[] { "-n", "--glob", "*.txt", "workspace-[^/\"\\s]*/", "/" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_RgWithDoubleQuotedPattern_ReturnsNameAndArgs()
    {
        // rg -n "/workspace-[^\"'[:space:]]+" / --glob '!**/*.cfg'
        var result = ShellCommandParser.TryParse(
            "rg -n \"/workspace-[^\\\"'[:space:]]+\" / --glob '!**/*.cfg'");
        Assert.NotNull(result);
        Assert.Equal("rg", result.Value.Name);
        var args = result.Value.Arguments;
        Assert.Equal("-n", args[0]);
        Assert.Equal("/workspace-[^\"'[:space:]]+", args[1]);
        Assert.Equal("/", args[2]);
        Assert.Equal("--glob", args[3]);
        Assert.Equal("!**/*.cfg", args[4]);
    }

    [Fact]
    public void TryParse_ErrorRedirect_IgnoresRedirection()
    {
        var result = ShellCommandParser.TryParse("cat foo.tsv 2> /dev/null");
        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(new[] { "foo.tsv" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParse_OutputRedirect_IgnoresRedirection()
    {
        var result = ShellCommandParser.TryParse("cat foo.tsv > out.txt");
        Assert.NotNull(result);
        Assert.Equal("cat", result.Value.Name);
        Assert.Equal(new[] { "foo.tsv" }, result.Value.Arguments);
    }

    [Fact]
    public void TryParsePipeline_SimpleCommand_ReturnsSingleStage()
    {
        var result = ShellCommandParser.TryParsePipeline("ls -la /");
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ls", result[0].Name);
        Assert.Equal(new[] { "-la", "/" }, result[0].Arguments);
    }

    [Fact]
    public void TryParsePipeline_TwoStagePipe_ReturnsBothStages()
    {
        var result = ShellCommandParser.TryParsePipeline("cat foo.tsv | sort");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat", result[0].Name);
        Assert.Equal(new[] { "foo.tsv" }, result[0].Arguments);
        Assert.Equal("sort", result[1].Name);
        Assert.Empty(result[1].Arguments);
    }

    [Fact]
    public void TryParsePipeline_ThreeStagePipe_ReturnsAllStagesInOrder()
    {
        var result = ShellCommandParser.TryParsePipeline("cat foo.tsv | sort | head -10");
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("cat", result[0].Name);
        Assert.Equal("sort", result[1].Name);
        Assert.Equal("head", result[2].Name);
        Assert.Equal(new[] { "-10" }, result[2].Arguments);
    }

    [Fact]
    public void TryParsePipeline_Sequence_ReturnsNull()
    {
        var result = ShellCommandParser.TryParsePipeline("ls; cat");
        Assert.Null(result);
    }

    [Fact]
    public void TryParsePipeline_CommandSubstitutionInStage_ReturnsNull()
    {
        var result = ShellCommandParser.TryParsePipeline("cat foo.tsv | sort $(echo x)");
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_NoArgs_ReturnsEmptyArguments()
    {
        var result = ShellCommandParser.TryParse("ls");
        Assert.NotNull(result);
        Assert.Equal("ls", result.Value.Name);
        Assert.Empty(result.Value.Arguments);
    }

    [Fact]
    public void TryParseChain_SingleCommand_ReturnsOneSegment()
    {
        var result = ShellCommandParser.TryParseChain("ls -la");
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("ls -la", result[0].Text);
        Assert.NotNull(result[0].Stages);
        Assert.Equal("ls", result[0].Stages![0].Name);
    }

    [Fact]
    public void TryParseChain_SemicolonSeparated_ReturnsBothSegmentsWithCleanText()
    {
        var result = ShellCommandParser.TryParseChain("cat a ; cat b");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat a", result[0].Text);
        Assert.Equal("cat", result[0].Stages![0].Name);
        Assert.Equal(new[] { "a" }, result[0].Stages![0].Arguments);
        Assert.Equal("cat b", result[1].Text);
        Assert.Equal(new[] { "b" }, result[1].Stages![0].Arguments);
    }

    [Fact]
    public void TryParseChain_AndOperator_ReturnsBothSegments()
    {
        var result = ShellCommandParser.TryParseChain("foo && echo \"boo\"");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("foo", result[0].Text);
        Assert.Equal("echo \"boo\"", result[1].Text);
        Assert.Equal("echo", result[1].Stages![0].Name);
    }

    [Fact]
    public void TryParseChain_OrOperator_ReturnsBothSegments()
    {
        var result = ShellCommandParser.TryParseChain("foo || echo \"boo\"");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("foo", result[0].Text);
        Assert.Equal("echo \"boo\"", result[1].Text);
    }

    [Fact]
    public void TryParseChain_NewlineSeparated_ReturnsBothSegments()
    {
        var result = ShellCommandParser.TryParseChain("cat a\ncat b");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat a", result[0].Text);
        Assert.Equal("cat b", result[1].Text);
    }

    [Fact]
    public void TryParseChain_ThreeSegmentsMixedOperators_ReturnsAllInOrder()
    {
        var result = ShellCommandParser.TryParseChain("cat a ; print \"bar\" && qux");
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        Assert.Equal("cat a", result[0].Text);
        Assert.Equal("print \"bar\"", result[1].Text);
        Assert.Equal("qux", result[2].Text);
        Assert.Equal("qux", result[2].Stages![0].Name);
    }

    [Fact]
    public void TryParseChain_SegmentWithPipe_FlattensPipelineStages()
    {
        var result = ShellCommandParser.TryParseChain("cat a | sort ; cat b");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat a | sort", result[0].Text);
        Assert.Equal(2, result[0].Stages!.Count);
        Assert.Equal("cat", result[0].Stages![0].Name);
        Assert.Equal("sort", result[0].Stages![1].Name);
    }

    [Fact]
    public void TryParseChain_SubshellSegment_HasTextButNullStages()
    {
        var result = ShellCommandParser.TryParseChain("cat a ; (ls /)");
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat a", result[0].Text);
        Assert.Equal("(ls /)", result[1].Text);
        Assert.Null(result[1].Stages);
    }

    [Fact]
    public void TryParseChain_TrailingSemicolon_IgnoresEmptyTrailingSegment()
    {
        var result = ShellCommandParser.TryParseChain("cat a ;");
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("cat a", result[0].Text);
    }

    [Fact]
    public void TryParseChain_CommandSubstitutionSyntaxError_ReturnsNull()
    {
        var result = ShellCommandParser.TryParseChain("cat a ; (");
        Assert.Null(result);
    }

    [Theory]
    [InlineData("cat \"nested $(printf '%s' \"a;b\")\"", false)]
    [InlineData("cat `echo a; echo b`", false)]
    [InlineData("cat ${value:-a;b}", true)]
    [InlineData("cat file#name", true)]
    [InlineData("cat a{b", true)]
    [InlineData("cat a\\;b", true)]
    [InlineData("{ cat a; cat b; }", false)]
    [InlineData("case x in x) cat a ;; esac", false)]
    [InlineData("if true; then cat a; fi", false)]
    [InlineData("f() { cat a; cat b; }", false)]
    public void TryParseChain_UsesGrammarBoundariesAndPreservesSource(string first, bool hasStages)
    {
        var result = ShellCommandParser.TryParseChain(first + "; ls /");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(first, result[0].Text);
        Assert.Equal(hasStages, result[0].Stages is not null);
        Assert.Equal("ls /", result[1].Text);
        var stages = result[1].Stages;
        Assert.NotNull(stages);
        Assert.Equal("ls", Assert.Single(stages).Name);
    }

    [Fact]
    public void TryParseChain_CommentDelimitersDoNotCreateCommands()
    {
        const string First = "cat a # ; } ) \"";
        var result = ShellCommandParser.TryParseChain(First + "\nls /");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(First, result[0].Text);
        var stages = result[0].Stages;
        Assert.NotNull(stages);
        Assert.Equal(["a"], Assert.Single(stages).Arguments);
        Assert.Equal("ls /", result[1].Text);
    }

    [Theory]
    [InlineData("cat <<'EOF'\n); cat fake; $(unterminated\nEOF")]
    [InlineData("cat <<A <<-'B'\nfirst; (\nA\n\t# ; }\n\tB")]
    public void TryParseChain_HereDocumentBodiesStayWithTheirCommand(string first)
    {
        var result = ShellCommandParser.TryParseChain(first + "\nls /");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(first, result[0].Text);
        var stages = result[0].Stages;
        Assert.NotNull(stages);
        Assert.Equal("cat", Assert.Single(stages).Name);
        Assert.Equal("ls /", result[1].Text);
    }

    [Theory]
    [InlineData("cat a &&")]
    [InlineData("cat a &&; ls /")]
    [InlineData("cat a |")]
    [InlineData("( cat a }")]
    [InlineData("cat a\\")]
    [InlineData("cat <<EOF\nmissing delimiter")]
    [InlineData("cat a; )")]
    [InlineData("; cat a")]
    [InlineData("cat a; ; cat b")]
    [InlineData("cat a\n; cat b")]
    [InlineData("cat a ;; cat b")]
    public void TryParseChain_MalformedSourceIsNotPartiallyRecognized(string source)
    {
        Assert.Null(ShellCommandParser.TryParseChain(source));
    }

    [Theory]
    [InlineData("echo visible ;; echo hidden")]
    [InlineData("echo visible ) echo hidden")]
    public void TryParse_RejectsUnconsumedTrailingSource(string source)
    {
        Assert.Null(ShellCommandParser.TryParse(source));
        Assert.Null(ShellCommandParser.TryParsePipeline(source));
    }

    [Fact]
    public void TryParseChain_ContinuedPipelineKeepsCompoundSource()
    {
        const string first = "printf value |\nwhile IFS= read -r line; do printf '%s' \"$line\"; done";
        var result = ShellCommandParser.TryParseChain(first + "; echo next");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(first, result[0].Text);
        Assert.Null(result[0].Stages);
        Assert.Equal("echo next", result[1].Text);
    }

    [Fact]
    public void TryParseChain_StandaloneCommentsProduceNoSegments()
    {
        var result = ShellCommandParser.TryParseChain("# ; ) \"\n# cat fake");

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("cat a\rcat b", "cat", "a\rcat", 1)]
    [InlineData("\rcat a", "\rcat", "a", 1)]
    [InlineData("cat a\r\ncat b", "cat", "a\r", 2)]
    public void TryParseChain_CarriageReturnRemainsData(
        string source, string name, string firstArgument, int count)
    {
        var result = ShellCommandParser.TryParseChain(source);

        Assert.NotNull(result);
        Assert.Equal(count, result.Count);
        Assert.Equal(source.Split('\n')[0], result[0].Text);
        var stages = result[0].Stages;
        Assert.NotNull(stages);
        var stage = Assert.Single(stages);
        Assert.Equal(name, stage.Name);
        Assert.Equal(firstArgument, stage.Arguments[0]);
    }

    [Fact]
    public void TryParseChain_QuotedCarriageReturnRemainsArgumentData()
    {
        var result = ShellCommandParser.TryParseChain("cat 'a\rb'; ls /");

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("cat 'a\rb'", result[0].Text);
        var stages = result[0].Stages;
        Assert.NotNull(stages);
        Assert.Equal(["a\rb"], Assert.Single(stages).Arguments);
    }
}
