using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationEnvironmentTestsEndToEnd
{
    [Theory]
    [InlineData("a=(outer tail); a=temp export a=body; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "body:tail")]
    [InlineData("a=(outer tail); a=temp readonly a=body; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "body:tail")]
    [InlineData("a=(outer tail); a=temp export a; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "temp:tail")]
    [InlineData("a=(outer tail); f() { local a=local; export a; }; a=temp f; printf '%s:%s' \"${a[0]}\" \"${a[1]}\"", "local:tail")]
    [InlineData("a=(outer tail); f() { a=(first second third); export a; }; a=temp f; printf '%s:%s:%s' \"${a[0]}\" \"${a[1]}\" \"${a[2]}\"", "first:second:third")]
    [InlineData("declare -A a; a[key]=tail; a=temp export a=body; printf '%s:%s' \"${a[0]}\" \"${a[key]}\"", "body:tail")]
    [InlineData("x=outer; x=temp readonly x=body; sh -c 'printf \"%s\" \"$x\"'", "body")]
    public async Task Evaluation_AttributePromotion_PreservesCallerArraysAndExplicitAttributes(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_EvalPreservesPreparationAndBodyEffects()
    {
        var script = @"unset prepared executed; X=${prepared:=temporary} eval 'executed=$prepared'; echo ${prepared-unset}/$executed";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("temporary/temporary\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_EvalCommitsChangesToPreparedVariables()
    {
        var script = @"unset value; X=${value:=prepared} eval 'value=executed'; echo $value";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("executed\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_ThroughNamerefDoesNotLeakResolvedTarget()
    {
        var script = @"declare -n ref=target; target=outer; ref=temp eval 'seen=$ref'; echo $target/$seen";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer/temp\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_ExportBuiltin_MarksExistingVariable()
    {
        // This test verifies that export marks an existing variable without changing its value.
        // The script assigns FOO=bar, exports it, and echoes $FOO; STDOUT must be "bar\n".
        // If export alters the value, STDOUT would differ.
        var script = @"FOO=bar; export FOO; echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_AssignsAndMarksVariable()
    {
        // This test verifies that export can assign a value.
        // The script "export foo=bar; echo $foo" must output "bar\n".
        // If export does not assign, STDOUT would be empty.
        var script = @"export foo=bar; echo $foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_MissingVariableRemainsUnset()
    {
        var script = @"export missing; echo ${missing-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ExportBuiltin_PreservesUnsetAndEmptyAcrossChildCommands()
    {
        var script = @"
export LK_EXPORT_PROBE;
printf 'unset-parent=<%s>\n' ""${LK_EXPORT_PROBE-unset}"";
sh -c 'printf ""unset-child=<%s>\n"" ""${LK_EXPORT_PROBE-unset}""';
LK_EXPORT_PROBE=;
printf 'empty-parent=<%s>\n' ""${LK_EXPORT_PROBE-unset}"";
sh -c 'printf ""empty-child=<%s>\n"" ""${LK_EXPORT_PROBE-unset}""';
LK_EXPORT_PROBE=value;
sh -c 'printf ""assigned-child=<%s>\n"" ""${LK_EXPORT_PROBE-unset}""';
unset LK_EXPORT_PROBE;
LK_EXPORT_PROBE=private;
sh -c 'printf ""unexported-child=<%s>\n"" ""${LK_EXPORT_PROBE-unset}""';".Replace("\r\n", "\n");
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset-parent=<unset>\nunset-child=<unset>\nempty-parent=<>\nempty-child=<>\n"
            + "assigned-child=<value>\nunexported-child=<unset>\n", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandInvocation_IncludesExportedEnvironment()
    {
        // This test verifies that exported variables remain available to command invocation.
        // The script exports FOO and echoes it via command; STDOUT must be "bar\n".
        // If the export is lost, STDOUT would be empty.
        var script = @"export FOO=bar; command echo $FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_DoesNotAffectSameCommandExpansion()
    {
        // Bash expands arguments before exposing prefix bindings to the command; the prefix never persists.
        var script = @"FOO=bar command echo $FOO; echo ${FOO-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("\nunset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("FOO=bar command echo $FOO", "\n")]
    [InlineData("FOO=bar sh -c 'printf \"%s\\n\" \"$FOO\"'", "bar\n")]
    public async Task Evaluation_CommandInvocation_SeparatesExpansionAndExecutionBindings(string script, string expected)
    {
        // Bash 5.2.37: caller expansion sees no FOO, while the child receives its prefix environment.
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expected, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandWordsExpandBeforeRedirections()
    {
        var script = @"unset value; echo argument:${value:=word} > /${value:=redirection}; echo $value; cat /word";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("word\nargument:word\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FatalCommandWordExpansionSkipsRedirections()
    {
        var script = @"echo ${MISSING:?word-failed} > /unexpected";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/unexpected"));
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FatalRedirectionExpansionPreservesExpansionExitCode()
    {
        var script = @"echo value > ""${MISSING:?redirection-failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("MISSING: redirection-failed\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("echo \"${MISSING:?word-failed}\" 2>/errors", 1, "stderr")]
    [InlineData("read \"${MISSING:?word-failed}\" 2>/errors", 1, "stderr")]
    // The platform enables pipefail; the matching native probe does so explicitly.
    [InlineData("echo \"${MISSING:?word-failed}\" 2>/errors | cat", 1, "stderr")]
    [InlineData("echo $(echo \"${MISSING:?word-failed}\" 2>/errors) >/dev/null", 0, "stderr")]
    [InlineData("echo \"${MISSING:?word-failed}\" 2>&1", 1, "stderr")]
    [InlineData("{ echo \"${MISSING:?word-failed}\"; } 2>/errors", 1, "file")]
    [InlineData("(echo \"${MISSING:?word-failed}\") 2>/errors", 1, "file")]
    [InlineData("{ echo \"${MISSING:?word-failed}\"; } 2>&1", 1, "stdout")]
    [InlineData("X=${MISSING:?word-failed} 2>/errors", 1, "stderr")]
    [InlineData("X=${MISSING:?word-failed} 2>/dev/null", 1, "stderr")]
    [InlineData("X=${MISSING:?word-failed} 2>&1", 1, "stderr")]
    [InlineData("{ X=${MISSING:?word-failed} 2>/dev/null; } 2>/errors", 1, "file")]
    [InlineData("{ X=${MISSING:?word-failed} 2>/dev/null; } 2>&1", 1, "stdout")]
    public async Task FatalExpansionUsesOnlyActiveRedirections(string script, int exitCode, string destination)
    {
        var result = await EvaluationTestHelper.RunAsync(script);
        const string error = "MISSING: word-failed\n";

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(destination == "stdout" ? error : string.Empty, result.StdOut);
        Assert.Equal(destination == "stderr" ? error : string.Empty, result.StdErr);
        if (destination == "file")
            Assert.Equal(error, await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
        else
            Assert.False(await EndToEndHarness.FileExistsAsync(result.Host, "/errors"));
    }

    [Fact]
    public async Task Evaluation_FatalAssignmentOnlyExpansionDoesNotApplyAssignments()
    {
        var script = @"X=outer; Y=outer; X=${MISSING:?assignment-failed} Y=later || true; printf '%s/%s\n' ""$X"" ""$Y""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Empty(result.StdOut);
        Assert.Equal("MISSING: assignment-failed\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandWordsExpandBeforeFailingRedirection()
    {
        var script = @"unset value; echo ${value:=expanded} > /missing/output; echo ${value-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("expanded\n", result.StdOut);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandNameExpansionRunsOnce()
    {
        var script = @"$(echo expansion-side-effect >&2; echo echo) hello";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hello\n", result.StdOut);
        Assert.Equal("expansion-side-effect\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Pipeline_CommandExpansionSideEffectsStayIsolated()
    {
        var script = @"unset argument left right; a=${left:=L} echo ${argument:=A} | b=${right:=R} echo ${argument-unset}/${left-unset}/${right-unset}; echo ${argument-unset}/${left-unset}/${right-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset/unset/unset\nunset/unset/unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_EvalUsesTemporaryScope()
    {
        var script = @"FOO=outer; FOO=bar eval 'echo in:$FOO'; echo after:$FOO";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("in:bar\nafter:outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_ExportThroughNamerefDoesNotLeakResolvedTarget()
    {
        var script = @"declare -n ref=target; target=outer; ref=temp export ref=inner; echo $target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_CommandSubstitutionPreservesArrayScope()
    {
        var script = @"arr=(outer); arr=(temp) value=$(echo ${arr[0]}) eval 'echo $value'; echo ${arr[0]}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("(temp)\nouter\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionPreservesNamerefScope()
    {
        var script = @"declare -n ref=target; target=outer; value=$(ref=inner; echo $target); echo $value/$target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inner/outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CommandSubstitutionPreservesReadonlyScope()
    {
        var script = @"readonly locked=outer; value=$(locked=inner; echo $locked); echo $value/$locked";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/outer\n", result.StdOut);
        Assert.Equal("locked: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_UnsetDoesNotRemovePrefixedVariable()
    {
        var script = @"FOO=outer; FOO=bar unset FOO; echo ${FOO-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_ExpandsValueBeforeCommandBuiltinFailure()
    {
        var script = @"X=$(echo side >&2) command -x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("side\ncommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_ExpandsRemainingWordsBeforeCommandBuiltinFailure()
    {
        var script = @"X=$(echo assignment >&2) command -x ""$(echo word >&2)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("word\nassignment\ncommand: -x: invalid option\ncommand: usage: command [-pVv] command [arg ...]\n", result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SkipsValueAfterBuiltinArgumentExpansionFailure()
    {
        var script = @"X=$(echo builtin-assignment-side-effect >&2) test ""${MISSING:?word failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.DoesNotContain("builtin-assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SkipsValueAfterIgnoredBuiltinArgumentExpansionFailure()
    {
        var script = @"X=$(echo assignment-side-effect >&2) pwd ""${MISSING:?word failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.DoesNotContain("assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FatalExpansion_SkipsRemainingWordExpansions()
    {
        var script = @"X=$(echo assignment-side-effect >&2) pwd ""${MISSING:?word failed}$(echo same-word-side-effect >&2)"" ""$(echo later-word-side-effect >&2)""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.DoesNotContain("assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("same-word-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("later-word-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SkipsValueAfterExternalArgumentExpansionFailure()
    {
        var script = @"X=$(echo external-assignment-side-effect >&2) missing-command ""${MISSING:?word failed}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.DoesNotContain("external-assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_AssignmentPrefix_SkipsValuesAfterAssignmentExpansionFailure()
    {
        var script = @"X=${MISSING:?assignment-failed} Y=$(echo later-assignment-side-effect >&2) true";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.DoesNotContain("later-assignment-side-effect", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_UnsetBuiltin_RemovesExportMarker()
    {
        // This test verifies that unsetting an exported variable removes it from the shell state.
        // The script exports foo, unsets it, and echoes a default; STDOUT must be "unset\n".
        // If unset fails, the output would be "bar\n".
        var script = @"export foo=bar; unset foo; echo ${foo:-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("unset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_AssignsAndMarksVariable()
    {
        // This test verifies that readonly assigns a value and preserves it.
        // The script "readonly foo=bar; echo $foo" must output "bar\n".
        // If readonly assignment fails, STDOUT would be empty.
        var script = @"readonly foo=bar; echo $foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_PreventsUnset()
    {
        // Native Bash diagnoses the rejected write; omit its script-path/line prefix.
        var script = @"readonly foo=bar; unset foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("unset: foo: cannot unset: readonly variable\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReadonlyBuiltin_PreventsReadAssignment()
    {
        var script = @"readonly foo; read foo; printf '%s:<%s>\n' ""$?"" ""${foo:-unset}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("1:<unset>\n", result.StdOut);
        Assert.Equal("foo: readonly variable\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
