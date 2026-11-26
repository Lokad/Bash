using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationArrayAndParameterExpansionTestsEndToEnd
{
    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayElement()
    {
        // This test verifies that indexed array elements expand via ${arr[index]}.
        // The script initializes arr and echoes arr[1], so STDOUT must be "one\n".
        // If array indexing fails, the output would be empty.
        var script = @"arr=(zero one); echo ${arr[1]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssignsIndexedArrayElement()
    {
        // This test verifies that indexed array assignments store values.
        // The script assigns arr[1]=one and echoes it, which must output "one\n".
        // If the assignment is ignored, STDOUT would be empty.
        var script = @"arr[1]=one; echo ${arr[1]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_PreservesSparseMaximumIndexedPosition()
    {
        const string MaximumIndex = "9223372036854775807";
        var script = $"declare -a arr; arr[{MaximumIndex}]=edge; echo ${{arr[{MaximumIndex}]}}";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("edge\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssignsAssociativeArrayElement()
    {
        // This test verifies that associative array assignments store values by key.
        // The script assigns map[key]=value and echoes it, so STDOUT must be "value\n".
        // If associative arrays are not supported, the output would be empty.
        var script = @"declare -A map; map[key]=value; echo ${map[key]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_CompoundArrayAssignment()
    {
        // This test verifies compound array assignments populate indexed arrays in order.
        // The script assigns arr=(a b) and echoes arr[1], which must output "b\n".
        // If compound assignment fails, STDOUT would be empty or incorrect.
        var script = @"arr=(a b); echo ${arr[1]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_CompoundArrayAssignmentWithIndices()
    {
        // This test verifies compound array assignments with explicit indices.
        // The script assigns arr=([2]=two [0]=zero) and echoes arr[2], which must output "two\n".
        // If indices are ignored, the output would be empty or wrong.
        var script = @"arr=([2]=two [0]=zero); echo ${arr[2]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("two\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Assignment_AssociativeCompoundKeyValuePairs()
    {
        // This test verifies associative compound assignments consume key/value pairs.
        // The script assigns map=(one 1 two 2) and echoes map[two], which must output "2\n".
        // If pairs are not parsed, the output would be empty.
        var script = @"declare -A map; map=(one 1 two 2); echo ${map[two]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AssociativeCompoundKeyWithOmittedValueExpandsAsPresentAndEmpty()
    {
        var script = """
            declare -A map; map=(alpha)
            printf '<%s>' "${map[alpha]-missing}" "${map[beta]-missing}"
            """;
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<><missing>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("arr=('first second' '' third); printf '<%s>' ${arr[@]}")]
    [InlineData("arr=('first second' '' third); printf '<%s>' ${arr[*]}")]
    [InlineData("set 'first second' '' third; printf '<%s>' $@")]
    [InlineData("set 'first second' '' third; printf '<%s>' $*")]
    public async Task UnquotedAggregateExpansionSplitsWordsAndDropsEmptyFields(string script)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<first><second><third>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("arr=('first second' '' third); printf '<%s>' \"${arr[*]}\"", "<first second  third>")]
    [InlineData("IFS=',:'; arr=('first second' '' third); printf '<%s>' \"${arr[*]}\"", "<first second,,third>")]
    [InlineData("set 'first second' '' third; printf '<%s>' \"$*\"", "<first second  third>")]
    [InlineData("IFS=',:'; set -- 'first second' '' third; printf '<%s>' \"$*\"", "<first second,,third>")]
    public async Task QuotedStarJoinsFieldsWithTheFirstIfsCharacter(string script, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ForLoop_QuotedIndexedArrayAtPreservesElements()
    {
        // This test verifies that quoted ${arr[@]} expands each element as a separate loop word.
        // The first element contains a space, so collapsing the expansion would print one combined line.
        var script = @"arr=(""a b"" c); for x in ""${arr[@]}""; do echo ""[$x]""; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("[a b]\n[c]\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task QuotedPositionalAtPreservesFieldsAndEmptyArguments()
    {
        var script = "set 'first second' '' third; printf '<%s>' \"$@\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<first second><><third>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task UnquotedUnsetScalarDoesNotEmitAnEmptyArgument()
    {
        var script = "unset MISSING; printf '<%s>' before $MISSING after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<before><after>", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ForLoop_QuotedAtPreservesPositionalParameters()
    {
        // This test verifies that quoted "$@" expands each positional parameter as a separate loop word.
        // The first parameter contains a space, so collapsing the expansion would print one combined line.
        var script = @"set -- ""a b"" c; for x in ""$@""; do echo ""[$x]""; done";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("[a b]\n[c]\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsDefaultWhenUnset()
    {
        // This test verifies that ${FOO:-fallback} uses the default when FOO is unset.
        // The script echoes the expansion, which must output "fallback\n".
        // If the default is ignored, the output would be empty.
        var script = @"echo ${FOO:-fallback}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("fallback\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsDefaultWhenEmpty()
    {
        // This test verifies that ${FOO:-fallback} uses the default when FOO is empty.
        // The script sets FOO= and echoes the expansion, which must output "fallback\n".
        // If empty values are treated as set, the output would be empty.
        var script = @"FOO=; echo ${FOO:-fallback}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("fallback\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAlternateWhenSet()
    {
        // This test verifies that ${FOO:+alt} expands to alt when FOO is set.
        // The script sets FOO=bar and echoes the expansion, which must output "alt\n".
        // If alternate expansion fails, the output would be empty.
        var script = @"FOO=bar; echo ${FOO:+alt}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("alt\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsDefaultWhenUnset()
    {
        // This test verifies that ${FOO:=bar} assigns the default when FOO is unset.
        // The script echoes the expansion and then $FOO, which must output "bar\nbar\n".
        // If assignment is skipped, the second line would be empty.
        var script = @"echo ${FOO:=bar}; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\nbar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsDefaultWhenEmpty()
    {
        // This test verifies that ${FOO:=bar} assigns the default when FOO is empty.
        // The script sets FOO= and echoes the expansion and $FOO, which must output "bar\nbar\n".
        // If empty values are not treated as unset, the output would be two empty lines.
        var script = @"FOO=; echo ${FOO:=bar}; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\nbar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ErrorOnUnsetWithMessage()
    {
        // Fatal expansion reports its diagnostic and stops the noninteractive script.
        var script = @"echo ${FOO:?missing}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: FOO: missing\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_NoAssignWhenEmptyWithoutColon()
    {
        // This test verifies that ${FOO=bar} does not assign when FOO is set but empty.
        // The script sets FOO= and echoes the expansion and $FOO; both lines should be empty.
        // If assignment occurs, the output would contain "bar".
        var script = @"FOO=; echo ${FOO=bar}; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\n\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssignsWhenUnsetWithoutColon()
    {
        // This test verifies that ${FOO=bar} assigns when FOO is unset.
        // The script echoes the expansion and $FOO, which must output "bar\nbar\n".
        // If assignment is skipped, the second line would be empty.
        var script = @"echo ${FOO=bar}; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\nbar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_NoErrorWhenEmptyWithoutColon()
    {
        // This test verifies that ${FOO?missing} does not error when FOO is set (even if empty).
        // The script sets FOO= and echoes the expansion, which must produce an empty line and exit 0.
        // If errors are raised for empty values, the exit code would be 2.
        var script = @"FOO=; echo ${FOO?missing}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ErrorWhenUnsetWithoutColon()
    {
        // Fatal expansion reports its diagnostic and stops the noninteractive script.
        var script = @"echo ${FOO?missing}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("line 1: FOO: missing\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_RemovesShortestSuffix()
    {
        // This test verifies that ${var%.*} removes the shortest suffix matching .*.
        // For "file.tar.gz", this removes ".gz" leaving "file.tar".
        var script = @"F=file.tar.gz; echo ${F%.*}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("file.tar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_RemovesLongestSuffix()
    {
        // This test verifies that ${var%%.*} removes the longest suffix matching .*.
        // For "file.tar.gz", this removes ".tar.gz" leaving "file".
        var script = @"F=file.tar.gz; echo ${F%%.*}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("file\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_RemovesShortestPrefix()
    {
        // This test verifies that ${var#*.} removes the shortest prefix matching *..
        // For "file.tar.gz", this removes "file." leaving "tar.gz".
        var script = @"F=file.tar.gz; echo ${F#*.}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("tar.gz\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_RemovesLongestPrefix()
    {
        // This test verifies that ${var##*.} removes the longest prefix matching *..
        // For "file.tar.gz", this removes "file.tar." leaving "gz".
        var script = @"F=file.tar.gz; echo ${F##*.}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("gz\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_PatternRemovalNoMatchLeavesValueUnchanged()
    {
        // This test verifies that pattern removal leaves the value unchanged when no match is found.
        var script = @"F=file; echo ${F%.*}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("file\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_AssociativeKeyContainingHashStillUsesMinusOperator()
    {
        // This test verifies operator parsing ignores '#' inside associative array keys.
        var script = @"declare -A map; echo ${map[a#b]-fallback}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("fallback\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
