using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class DeclarationListingTestsEndToEnd
{
    // Compare only script-owned names: native Bash also lists host/environment variables.
    private const string Projection = " | fixture-bindings";
    private const string Bindings = "probe_z='two words'; declare probe_u; declare -ax probe_a=([2]='a b'); declare -Ar probe_m=([key]='m n'); declare -n probe_ref=probe_z; export probe_export=exported; readonly probe_readonly=read;";

    [Theory]
    [InlineData("declare", "probe_a=([2]=\"a b\")\nprobe_export=exported\nprobe_m=([key]=\"m n\" )\nprobe_readonly=read\nprobe_ref=probe_z\nprobe_z='two words'\n", 0)]
    [InlineData("declare -p", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\ndeclare -Ar probe_m=([key]=\"m n\" )\ndeclare -r probe_readonly=\"read\"\ndeclare -n probe_ref=\"probe_z\"\ndeclare -- probe_u\ndeclare -- probe_z=\"two words\"\n", 0)]
    [InlineData("declare -a", "declare -ax probe_a=([2]=\"a b\")\n", 0)]
    [InlineData("declare -A", "declare -Ar probe_m=([key]=\"m n\" )\n", 0)]
    [InlineData("declare -x", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\n", 0)]
    [InlineData("declare +x", "probe_a=([2]=\"a b\")\nprobe_export=exported\nprobe_m=([key]=\"m n\" )\nprobe_readonly=read\nprobe_ref=probe_z\nprobe_z='two words'\n", 0)]
    [InlineData("declare -r", "declare -Ar probe_m=([key]=\"m n\" )\ndeclare -r probe_readonly=\"read\"\n", 0)]
    [InlineData("declare -n", "declare -n probe_ref=\"probe_z\"\n", 0)]
    [InlineData("declare -ax", "declare -ax probe_a=([2]=\"a b\")\n", 0)]
    [InlineData("declare -ar", "", 1)]
    [InlineData("declare -aA", "", 1)]
    [InlineData("declare +p", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\ndeclare -Ar probe_m=([key]=\"m n\" )\ndeclare -r probe_readonly=\"read\"\ndeclare -n probe_ref=\"probe_z\"\ndeclare -- probe_u\ndeclare -- probe_z=\"two words\"\n", 0)]
    [InlineData("declare +a", "probe_a=([2]=\"a b\")\nprobe_export=exported\nprobe_m=([key]=\"m n\" )\nprobe_readonly=read\nprobe_ref=probe_z\nprobe_z='two words'\n", 0)]
    [InlineData("declare -px", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\n", 0)]
    [InlineData("export", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\n", 0)]
    [InlineData("export -p", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\n", 0)]
    [InlineData("export -n", "declare -ax probe_a=([2]=\"a b\")\ndeclare -x probe_export=\"exported\"\n", 0)]
    [InlineData("export -a", "declare -ax probe_a=([2]=\"a b\")\n", 0)]
    [InlineData("export -A", "", 1)]
    [InlineData("readonly", "declare -Ar probe_m=([key]=\"m n\" )\ndeclare -r probe_readonly=\"read\"\n", 0)]
    [InlineData("readonly -p", "declare -Ar probe_m=([key]=\"m n\" )\ndeclare -r probe_readonly=\"read\"\n", 0)]
    [InlineData("readonly -a", "", 1)]
    [InlineData("readonly -A", "declare -Ar probe_m=([key]=\"m n\" )\n", 0)]
    public Task Evaluation_DeclarationListings_FilterRawAttributes(string command, string expectedOutput, int expectedStatus) =>
        AssertResultAsync(Bindings + command + Projection, expectedOutput, expectedStatus);

    [Theory]
    [InlineData("set")]
    [InlineData("builtin set")]
    [InlineData("command set")]
    public Task OperandFreeSetListsVisibleAssignmentsInByteNameOrder(string command) =>
        AssertResultAsync(Bindings + command + Projection,
            "probe_a=([2]=\"a b\")\nprobe_export=exported\nprobe_m=([key]=\"m n\" )\n"
            + "probe_readonly=read\nprobe_ref=probe_z\nprobe_z='two words'\n", 0);

    [Fact]
    public Task OperandFreeSetListsTheSavedBindingBehindItsCommandPrefix() =>
        AssertResultAsync("probe_x=outer; probe_hidden=temporary probe_x=inner set" + Projection
            + "; printf '<%s:%s>' \"$probe_x\" \"${probe_hidden-absent}\"", "probe_x=outer\n<outer:absent>", 0);

    [Theory]
    [InlineData("set")]
    [InlineData("declare")]
    [InlineData("declare +F")]
    public Task AssignmentListingsRenderFunctionBodiesWithoutAttributeRows(string command) =>
        AssertResultAsync("probe_f(){ printf '%s' 'two words'; }; export -f probe_f; readonly -f probe_f; capture=$("
            + command + "); printf 'probe_f %s\\n' \"${capture##*probe_f }\"; declare -Fp probe_f",
            "probe_f () \n{ \n    printf '%s' 'two words'\n}\ndeclare -frx probe_f\n", 0);

    [Fact]
    public async Task OperandFreeSetReportsClosedOutputAndFailsUnderErrexit()
    {
        var result = await EvaluationTestHelper.RunAsync("set -e; set >&-; printf unreachable");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("set: write error: Bad file descriptor\n", result.StdErr);
    }

    [Theory]
    [InlineData("f(){ local a=value; local -F >&-; }; f", "local", 0)]
    [InlineData("f(){ local a=value; local -p a >&-; }; f", "line 1: local", 1)]
    [InlineData("declare -a a=([7]=tail); declare -a >&-", "declare", 0)]
    [InlineData("probe_f(){ :; }; declare -F >&-", "declare", 0)]
    [InlineData("probe_f(){ :; }; declare -F probe_f >&-", "line 1: declare", 1)]
    [InlineData("a=value; declare >&-", "declare", 1)]
    public async Task ClosedStdoutKeepsTheListingDiagnosticAndSelectsStatusByRole(
        string script, string diagnosticLabel, int status)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(status, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(diagnosticLabel + ": write error: Bad file descriptor\n", result.StdErr);
    }

    [Theory]
    [InlineData("declare -p a missing b absent 1>&-",
        "missing: not found\n|absent: not found\n|write error: Bad file descriptor\n", false)]
    [InlineData("declare -fp a missing b absent 2>&1 1>&-",
        "missing: not found\n|absent: not found\n|write error: Bad file descriptor\n", true)]
    [InlineData("declare -F a missing b absent 1>&-",
        "write error: Bad file descriptor\n|write error: Bad file descriptor\n", false)]
    [InlineData("declare -f a b=value b absent 1>&-",
        "write error: Bad file descriptor\n|cannot use `-f' to make functions\n", false)]
    [InlineData("declare -p missing absent 1>&-",
        "missing: not found\n|absent: not found\n", false)]
    public async Task NamedListingsKeepCheckingOperandsWhenTheShellClosesStdout(
        string command, string messages, bool remapped)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=one; b=two; a(){ :; }; b(){ :; }\n" + command + "\nprintf '<%s:%s>' \"$?\" \"$_\"");
        var diagnostic = string.Concat(messages.Split('|').Select(message => "line 2: declare: " + message));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(remapped ? diagnostic + "<1:absent>" : "<1:absent>", result.StdOut);
        Assert.Equal(remapped ? string.Empty : diagnostic, result.StdErr);
    }

    [Fact]
    public async Task OperandFreeDeclarationWithClosedStdoutContinuesUnderErrexit()
    {
        var result = await EvaluationTestHelper.RunAsync(
            "set -e; a=value; declare -p >&-; declare -p a");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -- a=\"value\"\n", result.StdOut);
        Assert.Equal("declare: write error: Bad file descriptor\n", result.StdErr);
    }

    [Fact]
    public Task Evaluation_AssignmentListing_RetainsDeterministicUtf8Rendering()
    {
        // K: reuse the established UTF-8 quote policy, not Git Bash's locale-dependent emoji octal form.
        return AssertResultAsync("probe_x='élève 😀'; declare | fixture-bindings", "probe_x='élève 😀'\n", 0);
    }

    private static async Task AssertResultAsync(string script, string expectedOutput, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
    }
}
