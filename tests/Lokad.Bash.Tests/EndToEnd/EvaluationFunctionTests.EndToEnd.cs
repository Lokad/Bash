using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Tree;
namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationFunctionTestsEndToEnd
{
    [Theory]
    [InlineData("x=global; f() { x=inner eval \"unset x; export x=body\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { x=inner eval \"unset x; readonly x=after\"; }; x=outer f; printf \"%s\" \"$x\"", "after")]
    [InlineData("x=global; f() { local x=local; unset x; x=inner eval \"export x=body\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { local x=local; unset x; g() { export x=body; }; x=inner g; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { x=inner eval \"unset x; local x=after; export x\"; }; x=outer f; printf \"%s\" \"$x\"", "after")]
    [InlineData("x=global; f() { x=inner eval \"export x; guard=temp eval \\\"unset x\\\"; x=after\"; }; x=outer f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { x=inner eval \"guard=temp eval \\\"unset x; export x=body\\\"\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    public async Task Evaluation_AttributePromotion_CanStartANewPrefixLifetime(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash: direct attribute builtins own their prefixes; eval/source in a function forward eligible promotions.
    [Theory]
    [InlineData("x=global; f() { x=middle export x=body; }; x=outer f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { x=middle readonly x=body; }; x=outer f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { x=inner eval \"export x=body\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { x=inner eval \"readonly x=body\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { x=middle eval \"export x=body\"; }; f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { x=inner eval \"export x=body; unset x\"; }; x=outer f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; x=middle eval \"export x=body\"; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { x=middle eval \"g() { export x=body; }; g\"; }; x=outer f; printf \"%s\" \"$x\"", "body")]
    [InlineData("declare -n ref=target; target=global; f() { ref=inner export ref=body; }; ref=outer f; printf \"%s\" \"$target\"", "body")]
    public async Task Evaluation_AttributePromotion_RespectsTheOwningPrefixScope(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    // Bash: recreating an unset local does not revive its inherited function-prefix lifetime.
    [Theory]
    [InlineData("x=global; f() { local x=local; unset x; export x=after; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { local x=local; export x; unset x; x=after; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { local x=local; unset x; local x=after; export x; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { x=inner eval \"local x=local; unset x; export x=after\"; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { local x=local; guard=mid eval \"unset x; export x=after\"; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { local x=local; declare -n ref=x; unset ref; export x=after; }; x=temp f; printf \"%s\" \"$x\"", "global")]
    [InlineData("x=global; f() { local x=local; export x=body; }; x=temp f; printf \"%s\" \"$x\"", "body")]
    [InlineData("x=global; f() { local x=local; x=mid unset x; export x=after; }; x=temp f; printf \"%s\" \"$x\"", "after")]
    [InlineData("x=global; f() { local x=local; g() { unset x; export x=after; }; g; }; x=temp f; printf \"%s\" \"$x\"", "after")]
    public async Task Evaluation_LocalPromotion_EndsWhenItsBindingIsUnset(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("x=outer; f() { y=changed; return 7; }; x=temp f; status=$?; printf '%s:%s:%s' \"$x\" \"$y\" \"$status\"", "outer:changed:7")]
    [InlineData("x=outer; f() { guard=temp eval 'export x=body'; }; x=temp f; printf '%s' \"$x\"", "body")]
    [InlineData("x=outer; f() { (export x=body); }; x=temp f; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f() { value=$(export x=body); }; x=temp f; printf '%s' \"$x\"", "outer")]
    [InlineData("x=outer; f() { export x=body | :; }; x=temp f; printf '%s' \"$x\"", "outer")]
    [InlineData("set -- a b; f() { set -- inner; }; guard=temp f arg; printf '%s:%s' \"$#\" \"$1\"", "2:a")]
    [InlineData("arr=(old); guard=temp eval 'arr[0]=new'; printf '%s' \"${arr[0]}\"", "new")]
    [InlineData("arr=(old); arr=temp eval 'arr[0]=new'; printf '%s' \"${arr[0]}\"", "old")]
    public async Task Evaluation_CommandPrefixes_PreserveEffectsAndNestedIsolation(string script, string expectedOutput)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_NestedFunction_UsesDynamicLocalScope()
    {
        var script = @"value=global; inner() { printf 'inner=<%s>\n' ""$value""; value=changed; }; outer() { local value=outer; inner; printf 'outer=<%s>\n' ""$value""; }; outer; printf 'global=<%s>\n' ""$value""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inner=<outer>\nouter=<changed>\nglobal=<global>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionAssignmentPrefixPreservesUnrelatedSideEffects()
    {
        var script = @"x=outer; f() { x=inner; y=set; }; x=temporary f; printf '<%s>:<%s>\n' ""$x"" ""${y-missing}""";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("<outer>:<set>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionCommandScopeAndNestedLoopPreserveFlowKinds()
    {
        var script = "value=outer; flow() { "
            + "for item in skipped; do continue; printf 'unreachable-continue\\n'; done; "
            + "for item in visible; do printf 'item=<%s>:value=<%s>\\n' \"$item\" \"$value\"; "
            + "break; printf 'unreachable-break\\n'; done; "
            + "for item in returned; do return 7; done; printf 'unreachable\\n'; }; "
            + "value=prefix flow; status=$?; "
            + "printf 'status=<%s>:after=<%s>\\n' \"$status\" \"$value\"";

        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("item=<visible>:value=<prefix>\nstatus=<7>:after=<outer>\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }


    [Fact]
    public async Task Evaluation_FunctionInvocation_AssignmentPrefixDoesNotAffectArgumentExpansion()
    {
        var script = @"x=outer; f() { echo arg:$1 env:$x; }; x=temp f $x; echo after:$x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("arg:outer env:temp\nafter:outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_PreservesPreparationAndBodyEffects()
    {
        var script = @"unset prepared executed; f() { executed=$prepared; }; X=${prepared:=temporary} f; echo ${prepared-unset}/$executed";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("temporary/temporary\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_AssignmentPrefixPreservesDirectoryVariablesChangedByBody()
    {
        var script = @"f() { cd /target; }; x=temp f; pwd; echo $PWD";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("/target\n/target\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ExplicitAttributesPromotePrefixedNames()
    {
        var script = @"x=outer; y=outer; f() { export x=body; readonly y=body; }; x=temp y=temp f; echo $x/$y";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("body/body\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ExplicitAttributesPromoteResolvedNamerefTargets()
    {
        var script = @"declare -n export_ref=export_target; declare -n readonly_ref=readonly_target; export_target=outer; readonly_target=outer; f() { export export_ref=exported; readonly readonly_ref=locked; }; export_ref=temp readonly_ref=temp f; echo $export_target/$readonly_target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("exported/locked\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_AttributesPreservePrefixedNamerefTargets()
    {
        var script = @"declare -n export_ref=export_target; declare -n readonly_ref=readonly_target; export_target=outer; readonly_target=outer; f() { export export_ref; readonly readonly_ref; }; export_ref=temp readonly_ref=temp f; echo $export_target/$readonly_target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("temp/temp\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_NestedFunctionInvocation_ExplicitExportPromotesOuterPrefixedName()
    {
        var script = @"x=outer; outer() { inner() { export x=body; }; inner; }; x=temp outer; echo $x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("body\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_UnsetCancelsExplicitExportPromotion()
    {
        var script = @"x=outer; f() { export x; unset x; }; x=temp f; echo ${x-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ReassignmentAfterUnsetPromotesFinalValue()
    {
        var script = @"x=outer; f() { export x; unset x; x=after; }; x=temp f; echo ${x-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_NestedFunctionInvocation_CanceledInnerPromotionDoesNotReachOuterPrefix()
    {
        var script = @"x=global; inner() { export x; unset x; }; outer() { x=middle inner; echo $x; }; x=outer outer; echo $x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\nglobal\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ExportedLocalDoesNotEscapeUnrelatedPrefix()
    {
        var script = @"x=global; f() { local x=local; export x=body; }; y=temp f; echo $x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("global\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_DeclareAndTypesetLocalsDoNotEscapeUnrelatedPrefix()
    {
        var script = @"x=global; y=global; f() { declare x=local; typeset y=local; }; prefix=temp f; echo $x/$y";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("global/global\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_DeclareRestoresArrayAndNamerefBindings()
    {
        var script = @"arr=(global); target=global; declare -n ref=target; f() { declare -a arr; arr[0]=local; declare -n ref=other; }; f; echo ${arr[0]}/$ref";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("global/global\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_RedeclaringLocalArraysPreservesTheirContents()
    {
        var script = @"f() { declare -a arr; declare -A map; arr[0]=indexed; map[key]=associative; declare -a arr; declare -A map; echo ${arr[0]}/${map[key]}; }; f";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("indexed/associative\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_DeclarePromotesItsOwnPrefixIntoLocalScope()
    {
        var script = @"x=global; f() { x=temp declare x=local; echo $x; }; f; echo $x";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("local\nglobal\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_LocalShadowsNamerefWithoutLeakingTarget()
    {
        var script = @"target=global; declare -n ref=target; f() { local ref=local; echo $ref/$target; }; guard=temp f; echo $ref/$target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("local/global\nglobal/global\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_DeclareShadowsReadonlyNamerefTarget()
    {
        var script = @"readonly target=global; declare -n ref=target; f() { declare ref=local; echo $ref/$target; }; guard=temp f; echo $ref/$target";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("local/global\nglobal/global\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
    [Fact]
    public async Task Evaluation_FunctionDefinition_DoesNotEmitOperations()
    {
        // This test verifies that defining a function does not execute its body.
        // The script defines a function that would echo "hi" if run; a correct implementation must
        // produce no output, no STDERR, and exit with code 0.
        // If the body runs during definition, STDOUT would contain "hi\n".
        var script = @"foo() { echo hi; }";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionDefinition_AllowsFollowingCommands()
    {
        // This test verifies that a function definition does not block subsequent commands.
        // The script defines a function and then echoes "after"; a correct run must emit "after\n",
        // leave STDERR empty, and exit 0.
        // If evaluation stops after the definition, STDOUT would be empty.
        var script = @"foo() { echo hi; }; echo after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("after\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionDefinition_NegationInvertsExitStatus()
    {
        // This test verifies that "!" negates the exit status of a function definition command.
        // The script "! foo() { echo hi; }" should produce no output and return exit code 1.
        // If negation is ignored, the exit code would remain 0.
        var script = @"! foo() { echo hi; }";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ExecutesBodyCommands()
    {
        // This test verifies that invoking a function executes its body in order.
        // The script defines foo to echo "first" and "second", then calls foo and echoes "third".
        // A correct run must emit "first\nsecond\nthird\n" with no STDERR and exit code 0.
        // If the function body is skipped or reordered, STDOUT will not match.
        var script = @"foo() { echo first; echo second; }; foo; echo third";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("first\nsecond\nthird\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_UsesLastBodyExitStatus()
    {
        // This test verifies that a function's exit status is the status of its last command.
        // The script defines foo to run "true; false", then uses "foo && echo after".
        // A correct implementation must skip "echo after", emit no output, and return exit code 1.
        // If the function status is not the last command, "after\n" would appear.
        var script = @"foo() { true; false; }; foo && echo after";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReturnBuiltin_ExitsFunctionWithStatus()
    {
        // This test verifies that "return <status>" exits a function immediately with that status.
        // The script echoes "start", returns 7, and then tries to echo "after" inside the function.
        // A correct run must emit only "start\n", no STDERR, and exit with code 7.
        // If "return" does not stop the function, STDOUT would include "after\n".
        var script = @"foo() { echo start; return 7; echo after; }; foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("start\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_ReturnBuiltin_UsesLastExitStatusWhenOmitted()
    {
        // This test verifies that "return" without an argument uses the last command's exit status.
        // The script runs "false" then "return" inside the function, so the function must exit 1
        // with no output and no STDERR.
        // If the last status is ignored, the exit code would be 0.
        var script = @"foo() { false; return; echo after; }; foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_SetsAndRestoresPositionalParameters()
    {
        // This test verifies that function calls receive their own positional parameters and restore
        // the caller's parameters afterward.
        // The script sets outer parameters, calls foo with inner parameters, echoes inner args inside
        // the function, and then echoes $1 afterward. A correct run must emit:
        // "inner1\ninner2\nouter1\n".
        // If parameters are not restored, the final echo would print "inner1" instead.
        var script = @"set outer1 outer2; foo() { echo $1; shift; echo $1; }; foo inner1 inner2; echo $1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("inner1\ninner2\nouter1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_FunctionInvocation_ReturnRestoresPositionalParameters()
    {
        // This test verifies that returning from a function restores positional parameters.
        // The script sets outer parameters, calls foo with a different parameter, returns immediately,
        // and then echoes $1. A correct run must emit "outer\n" and exit with code 0.
        // If restoration fails, STDOUT would show the inner argument instead.
        var script = @"set outer; foo() { return 0; echo after; }; foo inner; echo $1";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_AssignsAndClearsVariable()
    {
        // This test verifies that "local" assigns within a function and clears afterward.
        // The script sets a local variable, echoes it inside the function, then echoes a default
        // value outside. A correct run must emit "baz\nunset\n".
        // If the local variable leaks, the second line would be "baz".
        var script = @"foo() { local bar=baz; echo $bar; }; foo; echo ${bar:-unset}";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("baz\nunset\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_RestoresPreviousValue()
    {
        // This test verifies that "local" restores a previous value on function exit.
        // The script sets bar=old, then uses local bar=new inside the function and echoes it;
        // afterward it echoes bar again. A correct run must emit "new\nold\n".
        // If restoration fails, the final line would be "new\n".
        var script = @"bar=old; foo() { local bar=new; echo $bar; }; foo; echo $bar";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("new\nold\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_LocalBuiltin_OutsideFunctionReturnsFailure()
    {
        var script = @"local foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("local: can only be used in a function\n", result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CallerBuiltin_ReportsFunctionName()
    {
        // This test verifies that "caller" reports the current function name.
        // The script defines foo and calls caller inside it, so STDOUT must be "0 foo\n".
        // If the function context is missing, caller would fail or emit different output.
        var script = @"foo() { caller; }; foo";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("0 foo\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_CallerBuiltin_OutsideFunctionReturnsFailure()
    {
        // This test verifies that "caller" fails when not inside a function.
        // The script "caller" must emit no output and return exit code 1.
        // If it reports a name, STDOUT would be non-empty.
        var script = @"caller";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(1, result.ExitCode);
    }
}
