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
    public async Task Evaluation_Assignment_AssociativeCompoundKeyWithMissingValue()
    {
        // This test verifies associative compound assignments handle missing values as empty.
        // The script assigns map=(alpha) and echoes map[alpha], which must produce an empty line.
        // If missing values are rejected, the exit code or output would differ.
        var script = @"declare -A map; map=(alpha); echo ${map[alpha]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAssociativeArrayElement()
    {
        // This test verifies that associative array elements expand by key.
        // The script sets map[key]=value and echoes it, which must output "value\n".
        // If associative expansion fails, STDOUT would be empty.
        var script = @"declare -A map; map[key]=value; echo ${map[key]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayAllElements()
    {
        // This test verifies that ${arr[@]} expands to all indexed elements.
        // The script sets arr=(a b) and echoes ${arr[@]}, which must output "a b\n".
        // If expansion fails, STDOUT would be empty or incomplete.
        var script = @"arr=(a b); echo ${arr[@]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStar()
    {
        // This test verifies that ${arr[*]} expands to all indexed elements.
        // The script sets arr=(a b) and echoes ${arr[*]}, which must output "a b\n".
        // If expansion fails, STDOUT would be empty.
        var script = @"arr=(a b); echo ${arr[*]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStarQuoted()
    {
        // This test verifies that quoted ${arr[*]} preserves the joined string.
        // The script sets arr=(a b) and echoes "${arr[*]}", which must output "a b\n".
        // If quoting is mishandled, the output could be split or altered.
        var script = @"arr=(a b); echo ""${arr[*]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a b\n", result.StdOut);
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
    public async Task Evaluation_EchoBuiltin_ExpandsIndexedArrayStarQuotedWithFirstIfsCharacter()
    {
        // This test verifies that quoted ${arr[*]} joins elements with the first IFS character.
        // The script sets IFS=, and must output "a,b,c\n" instead of joining with spaces.
        var script = @"IFS=,; arr=(a b c); echo ""${arr[*]}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a,b,c\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAtUnquoted()
    {
        // This test verifies that unquoted $@ expands to positional parameters.
        // The script sets parameters and echoes $@, which must output "one two\n".
        // If expansion fails, STDOUT would be empty.
        var script = @"set one two; echo $@";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one two\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsAtQuoted()
    {
        // This test verifies that quoted "$@" preserves arguments as separate words.
        // The script sets parameters and echoes "$@", which must output "one two\n".
        // If "$@" collapses incorrectly, the output would differ.
        var script = @"set one two; echo ""$@""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one two\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_UnquotedUnsetScalarDoesNotEmitEmptyArgument()
    {
        // This test verifies that an unquoted unset scalar disappears during word splitting.
        // The script invokes printf with only its format argument, so STDOUT must be "0\n".
        // If an empty argument is emitted, printf would report "1\n" instead.
        var script = @"printf '%s\n' $# $MISSING";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0\n", result.StdOut);
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
    public async Task Evaluation_EchoBuiltin_ExpandsStarUnquoted()
    {
        // This test verifies that unquoted $* expands positional parameters.
        // The script sets parameters and echoes $*, which must output "one two\n".
        // If expansion fails, STDOUT would be empty.
        var script = @"set one two; echo $*";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one two\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsStarQuotedWithFirstIfsCharacter()
    {
        // This test verifies that quoted "$*" joins positional parameters with the first IFS character.
        // The script sets IFS=, and must output "a,b,c\n" instead of joining with spaces.
        var script = @"IFS=,; set -- a b c; echo ""$*""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("a,b,c\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_EchoBuiltin_ExpandsStarQuoted()
    {
        // This test verifies that quoted "$*" joins parameters using IFS.
        // The script sets parameters and echoes "$*", which must output "one two\n".
        // If quoted expansion is mishandled, the output would differ.
        var script = @"set one two; echo ""$*""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("one two\n", result.StdOut);
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
        Assert.Equal("FOO: missing\n", result.StdErr);
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
        Assert.Equal("FOO: missing\n", result.StdErr);
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
