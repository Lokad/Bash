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
