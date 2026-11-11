using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class CircularDeclarationTestsEndToEnd
{
    // Bash 5.2.21 (Linux) and 5.2.37 (Git Bash): lookup warnings precede fallback creation, not RHS expansion.
    [Theory]
    [InlineData("declare -n first=second second=first; declare -g first=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare first=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -gx first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; 'declare' -g first=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; operand=first=value; declare -g \"$operand\"; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g first=$(printf 'rhs\\n' >&2; printf value); printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "rhs\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -ga first=($(printf 'rhs\\n' >&2; printf value)); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<0> value=<value>\n", "rhs\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -gA first=([key]=value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[key]}\"", "status=<0> value=<value>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g first=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local first=local; declare -ga first=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"; }; f", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local first=local; declare -g first=value; printf 'status=<%s> value=<%s>\\n' \"$?\" \"$first\"; }; f", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { declare first=value; printf 'status=<%s> value=<%s>\\n' \"$?\" \"$first\"; }; f", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare() { printf '<%s>\\n' \"$@\"; }; declare -g first=value", "<-g>\n<first=value>\n", "", 0)]
    [InlineData("declare -n first=second second=first; declare() { printf '<%s>\\n' \"$@\"; }; declare -ga first=(value); printf 'value=<%s>\\n' \"${first[0]}\"", "<-ga>\n<first>\nvalue=<value>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -gZ first=value; printf 'status=<%s>\\n' \"$?\"", "status=<2>\n", "declare: -Z: invalid option\n", 0)]
    [InlineData("declare -n first=second second=first; declare -gaZ first=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<2> value=<value>\n", "warning: first: circular name reference\ndeclare: -Z: invalid option\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g first+=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -ga first+=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<0> value=<value>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=third third=first; declare -g second=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: second: circular name reference\nwarning: second: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { printf 'before-local\\n' >&2; local first=local; printf 'before-global\\n' >&2; declare -g first=value; printf 'after-global\\n' >&2; printf '<%s>\\n' \"$first\"; }; f", "<value>\n", "before-local\nwarning: first: circular name reference\nwarning: first: circular name reference\nbefore-global\nafter-global\n", 0)]
    [InlineData("declare -n first=second second=first; f() { printf 'before-local\\n' >&2; local first=local; printf 'before-global\\n' >&2; declare -ga first=(value); printf 'after-global\\n' >&2; printf '<%s>\\n' \"${first[0]}\"; }; f", "<value>\n", "before-local\nwarning: first: circular name reference\nwarning: first: circular name reference\nbefore-global\nafter-global\n", 0)]
    [InlineData("first=global; f() { local -n first=second second=first; printf 'before\\n' >&2; declare first=value; printf 'status=<%s>\\n' \"$?\"; }; f; printf 'global=<%s>\\n' \"$first\"", "status=<0>\nglobal=<global>\n", "before\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local -a first=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"; }; f", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -ga first=value; printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<0> value=<value>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -ga first; printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]-unset}\"", "status=<0> value=<unset>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; export first=value; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "warning: first: circular name reference\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; readonly first=value; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "warning: first: circular name reference\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -nr first=second; declare -n second=first; declare -ga first=(value); printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local first; printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first-unset}\"; }; f", "status=<0> value=<unset>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local -n first=target; printf 'status=<%s>\\n' \"$?\"; }; f", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g first=($(printf 'rhs\\n' >&2; printf value)); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"", "status=<0> value=<value>\n", "rhs\nwarning: first: circular name reference\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; f() { local -a first=($(printf 'rhs\\n' >&2; printf value)); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"; }; f", "status=<0> value=<value>\n", "rhs\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; export first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; readonly first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; export -n first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; export -n first=value; printf 'status=<%s>\\n' \"$?\"", "status=<1>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare +n first; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -g +n first=value; printf 'status=<%s>\\n' \"$?\"", "status=<0>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare -ga first[0]=(value); printf 'status=<%s>\\n' \"$?\"", "", "warning: first: circular name reference\nfirst[0]: cannot assign list to array member\n", 1)]
    [InlineData("declare -n first=second second=first; f() { local first=(value); printf 'status=<%s> value=<%s>\\n' \"$?\" \"${first[0]}\"; }; f", "status=<0> value=<value>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; declare first=value after=kept; printf 'status=<%s> after=<%s>\\n' \"$?\" \"$after\"", "status=<0> after=<kept>\n", "warning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    [InlineData("declare -n first=second second=first; readonly first=value after=kept; printf 'status=<%s> after=<%s>\\n' \"$?\" \"$after\"", "status=<1> after=<kept>\n", "warning: first: circular name reference\nwarning: first: circular name reference\nwarning: first: circular name reference\n", 0)]
    public async Task Evaluation_CircularDeclarations_PreserveFallbackAndWarningStages(
        string script, string expectedOutput, string expectedError, int expectedExitCode)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
        Assert.Equal(expectedExitCode, result.ExitCode);
    }

    [Theory]
    [InlineData("declare -n first=second second=first; declare -gx first=value", false)]
    [InlineData("declare -nr first=second; declare -n second=first; declare -ga first=(value)", true)]
    public async Task Evaluation_CircularDeclarations_RetainOrReplaceTheWholeBinding(string script, bool replaced)
    {
        var evaluation = new ShellEvaluation(Parser.Parse(script));
        var host = EndToEndHarness.WithMemoryBudget(EvaluationTestHelper.BuildDefaultTree(), new Lokad.Bash.Memory.MemoryGovernor(TestMemoryBudget.DefaultBytes));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new Engine().RunAsync(evaluation, host, timeout.Token);

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal(ShellValue.FromText("first"), evaluation.Namerefs["second"]);
        Assert.DoesNotContain("first", evaluation.ReadonlyVariables);
        Assert.DoesNotContain("first", evaluation.ExportedVariables);
        Assert.False(evaluation.Variables.ContainsKey("value"));
        if (replaced)
        {
            Assert.False(evaluation.Namerefs.ContainsKey("first"));
            Assert.Equal([ShellValue.FromText("value")], evaluation.IndexedArrays["first"]);
        }
        else
        {
            Assert.Equal(ShellValue.FromText("second"), evaluation.Namerefs["first"]);
            Assert.False(evaluation.IndexedArrays.ContainsKey("first"));
        }
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal(replaced
                ? "warning: first: circular name reference\n"
                : "warning: first: circular name reference\nwarning: first: circular name reference\n",
            System.Text.Encoding.UTF8.GetString(host.StandardError.Content.Span));
    }
}
