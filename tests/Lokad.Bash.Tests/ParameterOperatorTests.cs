using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ParameterOperatorTests
{
    [Theory]
    [InlineData("(exit 7) | (exit 4)\n{ cat; } <<DOC; printf same\n${name\nDOC\nprintf '<%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"",
        0, "<1:1>", "line 4: ${name\n: bad substitution\n", null, null)]
    [InlineData("cat <<DOC; printf '<%s>' \"$?\"\n${x.y}\nDOC\nprintf next",
        0, "<1>next", "line 1: ${x.y}\n: bad substitution\n", null, null)]
    [InlineData("{ cat; } <<DOC; printf skipped\nlead ${items[1]:-value\nDOC\nprintf '<%s>' \"$?\"",
        0, "<1>", "line 3: bad substitution: no closing `}' in lead ${items[1]:-value\n\n", null, null)]
    [InlineData("s=prefix\n{ cat; } <<DOC; printf skipped\nfirst\r\n$s ${name\r\nDOC\nprintf '<%s>' \"$?\"",
        0, "<1>", "line 5: first\r\n$s ${name\r\n: bad substitution\n", null, null)]
    [InlineData("set -e; trap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" >&2' EXIT\n"
        + "{ cat; } 2>errors <<DOC || printf skipped\n${name\nDOC\nprintf next",
        1, "", "", "line 4: ${name\n: bad substitution\n<exit:1:1>", null)]
    [InlineData("set -e; trap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" >&2' EXIT\n"
        + "[[ x ]] 2>errors <<DOC || printf skipped\n${name:?message \"$(printf nested)\"}\nDOC\nprintf next",
        1, "", "", "line 2: name: message nested\n<exit:1:1>", null)]
    [InlineData("source ./fixture; printf caller",
        1, "", "./fixture: line 4: name: message\n", null,
        "# preface\n{ cat; } <<DOC; printf skipped\n${name:?message}\nDOC\nprintf loaded\n")]
    [InlineData("v=$(\nprintf inside\n{ cat; } <<DOC; printf skipped\n${name\nDOC\nprintf more\n);"
        + " printf '<%s:%s>' \"$?\" \"$v\"",
        0, "<1:inside>", "line 10: ${name\n: bad substitution\n", null, null)]
    [InlineData("unset value\nprintf '<%s:%s>' \"${value@}\" \"${value@ZZ}\"; printf after",
        0, "<:>after", "", null, null)]
    [InlineData("value=([0]=zero); i=1\n{ cat; } <<DOC; printf same\n${value[i++]@Z}\nDOC\nprintf '<%s>' \"$i\"",
        0, "\nsame<2>", "", null, null)]
    [InlineData("value=\ntrap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\"' EXIT\n(exit 7) | (exit 4)\n"
        + "printf '%s' \"${value@Z}\"; printf skipped\nprintf next",
        1, "<exit:1:1>", "line 4: ${value@Z}: bad substitution\n", null, null)]
    [InlineData("value=set\nprintf '%s' \"lead${value@Z}tail\"",
        1, "", "line 2: lead${value@Z}tail: bad substitution\n", null, null)]
    [InlineData("value=set\nprintf '%s' \"lead\"${value@Z}tail",
        1, "", "line 2: \"lead\"${value@Z}tail: bad substitution\n", null, null)]
    [InlineData("value=set\nprintf '%s' \"${value@$(printf touched >&2; printf Z)}\"",
        1, "", "line 2: ${value@$(printf touched 1>&2; printf Z)}: bad substitution\n", null, null)]
    [InlineData("value=set\ntrap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" >&2' EXIT\n"
        + "printf '%s' \"${value@Z}\" | cat\nprintf '<next:%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"",
        0, "<next:1:1 0>", "line 3: ${value@Z}: bad substitution\n<exit:1:1><exit:0:0>", null, null)]
    [InlineData("value=set\nset -e\ntrap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" >&2' EXIT\n"
        + "name=ok 2>errors <<DOC\n${value@Z}\nDOC\nprintf next",
        1, "", "", "line 4: ${value@Z}\n: bad substitution\n<exit:1:1>", null)]
    [InlineData("value=outer\nset -e\ntrap 'printf \"<exit:%s:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" \"$value\" >&2' EXIT\n"
        + "f() {\nlocal value=inner\nprintf '%s' \"${value@Z}\"\n}\nf 2>errors\nprintf next",
        1, "", "", "line 6: ${value@Z}: bad substitution\n<exit:1:1:inner>", null)]
    [InlineData("set -e\ntrap 'printf \"<exit:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\" >&2' EXIT\n"
        + ": 2>errors <<DOC\n${value:?stop}\nDOC\nprintf next",
        1, "", "", "line 3: value: stop\n<exit:1:1>", null)]
    public async Task ParameterDiagnosticsFollowSelectionAndTheReachedReader(
        string source, int status, string output, string diagnostic, string? redirectedDiagnostic, string? sourcedInput)
    {
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(1024 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            if (sourcedInput != null)
                host.AddFile("/fixture", sourcedInput);
            var result = prepared
                ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, CancellationToken.None)
                : await host.RunAsync(source, CancellationToken.None);

            Assert.Equal(status, result.ExitCode);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(diagnostic, host.StandardError.TextContent);
            if (redirectedDiagnostic != null)
                Assert.Equal(redirectedDiagnostic, host.ReadFile("/errors"));
            Assert.Equal(0, host.OpenFileCount);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("a=(); i=0; saved=prior\nsaved=${a[@]:=$((++i))}; printf skipped\nprintf '[%s:%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$saved\"",
        0, "[2:2:1:prior]", "line 2: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0; saved=prior\nsaved=\"${a[@]:=$((++i))}\" true; printf skipped\nprintf '[%s:%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$saved\"",
        0, "[2:2:1:prior]", "line 2: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\nf() { local PIPESTATUS=(8 9); printf '%s' \"${a[*]:=$((++i))}\"; }; f; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[2:0:1]", "line 2: a[*]: bad array subscript\n")]
    [InlineData("a=(); i=0\nf() { local -; set +e; printf '%s' \"${a[@]:=$((++i))}\"; }; set -e\neval 'f; printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"'",
        0, "[1:1:1]", "line 2: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\nset -e\nsource ./fixture || printf alternative\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[1:1:1][0:0:1]", "./fixture: line 1: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\nprintf '%s' \"${a[@]:=$((++i))}\" | cat\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[2:2 0:0]", "line 2: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\neval 'printf \"%s\" \"${a[@]:=$((++i))}\" | cat\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"\n{ printf \"%s\" \"${a[@]:=$((++i))}\"; } | cat\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"'",
        0, "[1:1 0:0][2:2 0:0]", "line 5: a[@]: bad array subscript\nline 7: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\nsaved=$(printf prior; printf '%s' \"${a[@]:=$((++i))}\"\nprintf skipped)\nprintf '[%s:%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$saved\"",
        0, "[1:1:0:prior]", "line 3: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\ntrap 'printf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' EXIT\nset -e\n! printf '%s' \"${a[@]:=$((++i))}\"\nprintf skipped",
        1, "[1:0:1]", "line 4: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\ntrap 'printf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' EXIT\nset -e\n(exit 7) && :\nprintf '%s' \"${a[@]:=$((++i))}\"",
        7, "[7:7:1]", "line 5: a[@]: bad array subscript\n")]
    [InlineData("a=(); i=0\ntrap 'printf \"%s\" \"${a[@]:=$((++i))}\" | cat\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' EXIT\nexit 7",
        7, "[7:7 0:0]", "line 1: a[@]: bad array subscript\n")]
    public async Task RejectedIndexedAggregateDefaultUsesTheReachedReadersStatus(string script, int status, string output, string error)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            host.AddFile("/fixture", "printf '%s' \"${a[@]:=$((++i))}\"; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"\n");
            var actual = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(status, actual);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("readonly v=\"\"; i=0\nsaved=prior\nsaved=${v:=$((++i))}; printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"; printf \"|%s\" \"$saved\"",
        "[2:2:1]|prior", "line 3: v: readonly variable\n")]
    [InlineData("a=(); i=0\nprintf \"%s\" \"${a[-2]:=$((++i))}\"; printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        "[2:2:1]", "line 2: a: bad array subscript\nline 2: a[-2]: bad array subscript\n")]
    [InlineData("declare -A a=(); key=\"\"; i=0\nprintf \"%s\" \"${a[$key]:=$((++i))}\"; printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        "[2:2:1]", "line 2: a: bad array subscript\nline 2: a[$key]: bad array subscript\n")]
    [InlineData("a=(); i=0\nprintf \"<%s>\" \"${a[-2]:=$((++i, a[2]=8, i))}\"\nprintf \"|%s\" \"${a[1]}\"; printf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        "<1>|1[0:0:1]", "line 2: a: bad array subscript\n")]
    [InlineData("readonly v=; i=0; declare -n r=v\n: \"${r:=$((++i))}\"; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        "[2:2:1]", "line 2: v: readonly variable\n")]
    public async Task DefaultAssignmentSelectsTheWriteTargetAfterItsRightHandSide(string script, string output, string error)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var result = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, result);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData(false, "+")]
    [InlineData(false, ":+")]
    [InlineData(true, ":+")]
    public async Task SelectedAggregateAlternateUsesConstantWorkspace(bool associative, string operation)
    {
        Assert.Equal(await MeasureAsync(1), await MeasureAsync(1024));

        async Task<long> MeasureAsync(int count)
        {
            var entries = Enumerable.Range(0, count).Select(index => associative ? $"[key{index}]=value" : "value");
            var setup = associative ? "declare -A a=(" : "a=(";
            var script = setup + string.Join(' ', entries) + "); printf begin; printf '%s' \"${a[@]" + operation + "selected}\"";
            var evaluation = new Eval(Parser.Parse(script));
            var governor = new MemoryGovernor(4 * 1024 * 1024);
            using var owner = governor.CreateScope();
            evaluation.Memory.Budget = owner;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var marker = Assert.IsType<WriteOperation>(await evaluation.GetNextStep(deadline.Token));
            Assert.Equal("begin", ShellValue.FromBytes(marker.Content).ToUtf8String());
            var before = evaluation.Memory.Workspace.Bytes;
            marker.SetResult(DescriptorWriteResult.Open);

            var output = Assert.IsType<WriteOperation>(await evaluation.GetNextStep(deadline.Token));
            Assert.Equal("selected", ShellValue.FromBytes(output.Content).ToUtf8String());
            var additional = evaluation.Memory.Workspace.Bytes - before;
            output.SetResult(DescriptorWriteResult.Open);

            Assert.Null(await evaluation.GetNextStep(deadline.Token));
            await evaluation.Completion;
            Assert.Equal(0, evaluation.LastExitCode);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
            return additional;
        }
    }

    [Theory]
    [InlineData("a=('' ''); IFS=:; printf '<%s>' \"${a[*]:+selected}\"", "<selected>")]
    [InlineData("a=('' ''); IFS=; printf '<%s>' \"${a[*]:+selected}\"", "<>")]
    [InlineData("a=([3]=one [10]='' [90]=last); printf '<%s>' \"${a[@]:-$(printf skipped >&2)}\"", "<one><><last>")]
    [InlineData("a=(''); i=0; printf '<%s>' \"${a[@]:-$((i++, a[0]=4, i))}\"; printf '|%s:%s' \"$i\" \"${a[0]}\"", "<1>|1:4")]
    public async Task ConditionalAggregateOperatorsRetainFieldShapeAndSelectedOperandEffects(string script, string output)
    {
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("printf '%s' \"${a[]}\"", "${a[]}: bad substitution\n")]
    [InlineData("printf '%s' \"${a[1}\"", "bad substitution: no closing `}' in \"${a[1}\"\n")]
    [InlineData("printf '%s' ${a[1}", "${a[1}: bad substitution\n")]
    [InlineData("printf '%s' \"${a.dot}\"", "${a.dot}: bad substitution\n")]
    [InlineData("printf '%s' \"${saved:-${a[1}}\"", "bad substitution: no closing `}' in ${saved:-${a[1}}\n")]
    public async Task MalformedBracedParametersPublishStatusBeforeRecoveringTheNextUnit(string command, string error)
    {
        var script = "a=(old); saved=prior\n(exit 7) | true\n" + command
            + "; printf skipped\nprintf '[%s:%s]' \"$?\" \"${PIPESTATUS[*]}\"";

        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("[1:1]", host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task IncompleteQuotedSubscriptStopsItsWordAfterEarlierArguments()
    {
        const string script = "printf '%s' \"$(printf earlier >&2)\" \"$(printf skipped >&2)${a[1}\"\nprintf '%s' \"$?\"";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", host.StandardOutput.TextContent);
        Assert.Equal("earlierbad substitution: no closing `}' in \"$(printf skipped 1>&2)${a[1}\"\n",
            host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("${a[i++}]", 1, "line 2: i++}: syntax error: invalid arithmetic operator (error token is \"}\")\n")]
    [InlineData("\"${a[i++}]\"", 0, "bad substitution: no closing `}' in \"${a[i++}]\"\n")]
    [InlineData("${a[i++}]suffix", 0, "${a[i++}]suffix: bad substitution\n")]
    [InlineData("${saved:-${a[i++}]}", 0, "bad substitution: no closing `}' in ${saved:-${a[i++}]}\n")]
    public async Task LateSubscriptFailureKeepsOnlyTheReachedSelectorEffects(string word, int index, string error)
    {
        var script = "a=(one two); i=0; saved=prior\n"
            + "printf '%s' " + word + "; printf skipped\n"
            + "printf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"";

        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal($"[1:1:{index}]", host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("printf '%s' ${m[x}]}", "known", "")]
    [InlineData("printf '%s' \"${missing:-${m[x}]}}\"", "known", "")]
    [InlineData("printf '%s' \"${saved:-${#m[x}]}}\"", "prior}", "")]
    [InlineData("cat <<EOF\n${m[x}]}\nEOF\n", "known\n", "")]
    [InlineData("printf '%s' ${m[$(printf selector >&2; printf x)}]", "",
        "selector${m[$(printf selector 1>&2; printf x)}]: bad substitution\n")]
    [InlineData("printf '%s' ${m[x}]:-$(printf skipped >&2)", "",
        "bad substitution: no closing `}' in ${m[x}]:-$(printf skipped 1>&2)\n")]
    public async Task LateAssociativeBracketPreservesBraceOwnershipAndReadOrder(string command, string output, string error)
    {
        var script = "declare -A m; m['x}']=known; saved=prior\n" + command + "\nprintf keep\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal(output + "keep", host.StandardOutput.TextContent);
            Assert.Equal(error, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private const string MarkedSelectorCapture = "$(printf reached >&2; printf ok)";

    [Theory]
    [InlineData("value=\"${map[}" + MarkedSelectorCapture + "tail]:-fallback}\"")]
    [InlineData("value=\"${map[}" + MarkedSelectorCapture + "\"\"tail]:-fallback}\"")]
    [InlineData("value=\"${outer:-${map[}" + MarkedSelectorCapture + "tail]:-fallback}}\"")]
    public async Task AClosedLateSelectorConsumesItsCaptureExactlyOnce(string assignment)
    {
        var source = "declare -A map\nmap['}oktail']=selected\n" + assignment
            + "\nprintf '<%s:%s>' \"$?\" \"$value\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
                : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("<0:selected>", host.StandardOutput.TextContent);
            Assert.Equal("reached", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task ABracketInTheNextWordCannotCompleteAQuotedSelector()
    {
        const string source = "printf '%s' \"${map[}" + MarkedSelectorCapture
            + "\" 'tail]:-fallback}'\nprintf '<%s>' \"$?\"";
        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1>", host.StandardOutput.TextContent);
        Assert.Equal("bad substitution: no closing `}' in \"${map[}$(printf reached 1>&2; printf ok)\"\n",
            host.StandardError.TextContent);
    }

    private const string RejectedSelectorBody = "$({ cat <<DATA\nbody\nDATA\n}; printf extra)";
    private const string RenderedSelectorBody = "$({ cat <<DATA\nbody\nDATA\n } printf extra)";
    private const string SelectorTokenDiagnostic = "command substitution: line 9: syntax error near unexpected token `printf'\n";

    [Theory]
    [InlineData("\"${map[}", "tail]:-fallback}\"", " } printf extra)tail")]
    [InlineData("\"${map[", "}tail]:-fallback}\"", " } printf extra)}tail")]
    public async Task ARejectedCaptureReportsThroughTheLateSelectorBoundary(string opening, string closing, string line)
    {
        var source = "value=initial\nvalue=" + opening + RejectedSelectorBody + closing
            + "\nprintf '<%s:%s>' \"$?\" \"$value\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(source, host)).ExitCode
                : (await host.RunAsync(source, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal("<1:initial>", host.StandardOutput.TextContent);
            Assert.Equal(SelectorTokenDiagnostic + $"command substitution: line 9: `{line}'\n",
                host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("${map[\"lead", "tail\"]:-fallback}", " } printf extra)tail\"]:-fallback}",
        "command substitution: line 10: unexpected EOF while looking for matching `)'\n",
        "${map[\"lead" + RenderedSelectorBody + "tail\"]:-fallback}: bad substitution\n")]
    [InlineData("${map[\"1+", "+2\"]:-fallback}", " } printf extra)+2\"]:-fallback}", "",
        "${map[\"1+" + RenderedSelectorBody + "+2\"]:-fallback}: bad substitution\n")]
    [InlineData("\"${map[${missing:-", "end}+2]:-fallback}tail\"", " } printf extra)end}+2]:-fallback}tail\"", "",
        "bad substitution: no closing `}' in \"${map[${missing:-" + RenderedSelectorBody + "end}+2]:-fallback}tail\"\n")]
    public async Task ARejectedSelectorReportsTheFollowupForItsQuotedOrNestedRole(
        string opening, string closing, string line, string eof, string parameterError)
    {
        var source = "value=initial\nvalue=" + opening + RejectedSelectorBody + closing
            + "\nprintf '<%s:%s>' \"$?\" \"$value\"\n";
        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<1:initial>", host.StandardOutput.TextContent);
        Assert.Equal(SelectorTokenDiagnostic + $"command substitution: line 9: `{line}'\n" + eof + parameterError,
            host.StandardError.TextContent);
    }

    [Theory]
    [InlineData(":1:2", "bc")]
    [InlineData("#a", "bcdef")]
    [InlineData("##a*", "")]
    [InlineData("%f", "abcde")]
    [InlineData("%%*f", "")]
    [InlineData("/a/Z", "Zbcdef")]
    [InlineData("^", "Abcdef")]
    [InlineData("^^", "ABCDEF")]
    [InlineData(",", "abcdef")]
    [InlineData(",,", "abcdef")]
    [InlineData("@Q", "'abcdef'")]
    [InlineData("@E", "abcdef")]
    public async Task IndirectValueOperatorsSelectNamedNamerefsOnce(string operation, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            "a=(abcdef uvwxyz); i=0; declare -n r='a[i++]'; p=r; "
            + "printf '<%s>|%s' \"${!p" + operation + "}\" \"$i\"");

        Assert.Equal("<" + expected + ">|1", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("unset IFS; set -- x '' y", "\"${value/match/\"$@\"}\"", "<x \u007f y-tail>")]
    [InlineData("IFS=:; set -- x y ''", "\"${value/match/\"$@\"}\"", "<x y \u007f-tail>")]
    [InlineData("set -- '' x y", "\"${value/match/\"$@\"}\"", "< x y-tail>")]
    [InlineData("set -- ''", "\"${value/match/\"$@\"}\"", "<-tail>")]
    [InlineData("set -- x '' y", "\"${value/match/\"$*\"}\"", "<x  y-tail>")]
    [InlineData("set -- x '' y", "${value/match/\"$@\"}", "<x><\u007f><y-tail>")]
    [InlineData("IFS=; items=(x '' y)", "\"${value/match/\"${items[@]}\"}\"", "<x \u007f y-tail>")]
    [InlineData("unset missing; set -- x '' y", "\"${value/match/${missing:-\"$@\"}}\"", "<x \u007f y-tail>")]
    public async Task ParameterReplacementPreservesLaterQuotedEmptyFields(string setup, string operand, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(
            setup + "\nvalue=match-tail\nprintf '<%s>' " + operand + "\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("set --; v=; show \"$@${v^^$(printf p >&2; printf a)}\"; show \"${v^^}$@\"",
        "[1]<>[1]<>", "p", 0)]
    [InlineData("set --; v=; unset u; show \"$@${v}\"; show \"${u^^$(printf p >&2; printf a)}$@\"",
        "[0][0]", "", 0)]
    [InlineData("set --; v=a; show \"$@${v/a/}\"; show \"${v#a}$@\"; v=; show \"$@${v@E}\"",
        "[1]<>[1]<>[1]<>", "", 0)]
    [InlineData("set --; a=([2]=); unset pattern; show \"$@${a[2],,${pattern:=A}}\" \"$pattern\"",
        "[2]<><A>", "", 0)]
    [InlineData("set --; v=a; show \"$@${v:1}\"; show \"${v:2}$@\"; show \"$@${v[@]:2}\"",
        "[1]<>[0][0]", "", 0)]
    [InlineData("set --; IFS=:; a=(''); show \"$@${a[*]}\"; show \"$@${a[*]^^x}\"; a=(); show \"${a[*]}$@\"",
        "[1]<>[0][0]", "", 0)]
    [InlineData("empty=(); IFS=; set -- '' ''; show \"${empty[@]}$*\"; set --; show \"$*${empty[@]}\"",
        "[1]<>[0]", "", 0)]
    [InlineData("set --; v=; show \"$@${v[*]^^x}\"; show \"${v[*]:0}$@\"",
        "[1]<>[1]<>", "", 0)]
    [InlineData("""set --; unset v; show "$@${v-$@}" """, "[1]<>", "", 0)]
    [InlineData("""set --; zero=(); v=x; show "${v:+"${zero[@]}"}$@" """, "[1]<>", "", 0)]
    [InlineData("""set --; unset v; show "$@${v-${@#x}}" """, "[1]<>", "", 0)]
    [InlineData("""set --; a=([3]=''); show "$@${a[*]:+$(printf skipped >&2)}" """, "[1]<>", "", 0)]
    [InlineData("""zero=(); IFS=; set -- '' ''; show "${*:+unused}${zero[@]}" """, "[1]<>", "", 0)]
    [InlineData("""set --; unset v missing; values=("$@${v-${missing-$@}}"); show "${values[@]}" """, "[1]<>", "", 0)]
    [InlineData("""zero=(); IFS=; set -- ''; unset v; show "${zero[@]}${v-${*:+unused}}"; show "${zero[@]}${v-$*}" """,
        "[1]<>[1]<>", "", 0)]
    [InlineData("""set --; zero=(); unset v; show "$@${v-}"; show ${v-"$@"}; show "${zero[@]}${*:+unused}" """,
        "[0][0][0]", "", 0)]
    [InlineData("v=; unset missing; show \"${v^^${missing:?required}}\"\nprintf skipped",
        "", "missing: required\n", 1)]
    public async Task QuotedParameterExpansionPreservesAnExplicitEmptyResult(string body, string expectedOutput,
        string expectedError, int expectedStatus)
    {
        const string show = "show(){ printf '[%s]' \"$#\"; for field; do printf '<%s>' \"$field\"; done; }\n";
        var result = await EndToEndHarness.RunScriptAsync(show + body, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("""
        p=('a[q++]' 'b[r++]'); a=(alpha amber); b=(beta brown); i=0; q=0; r=0
        printf '<%s>|%s:%s:%s' "${!p[i++]#$(printf pattern >&2; printf a)}" "$i" "$q" "$r"
        """, "<beta>|2:1:1", "pattern")]
    [InlineData("""
        p=(reference reference); a=(alpha amber); declare -n reference='a[q++]'; i=0; q=0
        printf '<%s>|%s:%s' "${!p[i++]^^}" "$i" "$q"
        """, "<ALPHA>|2:1", "")]
    [InlineData("""
        p=(before after); before=alpha; export after=beta; i=0
        printf '<%s>|%s' "${!p[i++]@A}" "$i"
        """, "<declare -x after='alpha'>|2", "")]
    [InlineData("""
        p=(value 'a[@]'); value=alpha; a=('amber gold' 'apricot red'); IFS=:; i=0
        printf '<%s>' "${!p[i++]^^}"
        printf '|%s' "$i"
        """, "<AMBER GOLD:APRICOT RED>|2", "")]
    [InlineData("""
        p=('a[@]' 'a[@]'); a=('amber gold' 'apricot red'); IFS=:; i=0
        printf '<%s>' "${!p[i++]^^}"
        printf '|%s' "$i"
        """, "<AMBER GOLD><APRICOT RED>|2", "")]
    [InlineData("""
        p=('a[q++]' 'b[r++]'); a=(alpha amber); b=(beta brown); i=0; q=0; r=0
        printf '<%s>|%s:%s:%s' "${!p[i++]#}" "$i" "$q" "$r"
        i=0; q=0; r=0
        printf '<%s>|%s:%s:%s' "${!p[i++]##}" "$i" "$q" "$r"
        """, "<alpha>|1:1:0<beta>|2:1:1", "")]
    [InlineData("""
        p=(value); value=alpha; i=0
        printf '<%s>|%s' "${!p[i++]/$(printf pattern >&2; printf a)/$(printf replacement >&2; printf z)}" "$i"
        """, "<>|2", "patternreplacement")]
    [InlineData("""
        IFS=:; p=('a[@]' value); a=(alpha beta); value=scalar; i=0
        printf '<%s>' "${!p[i+++(i>1?(a[0]=9,0):0)]@Q}"
        printf '|%s:%s' "$i" "${a[0]}"
        """, "<'alpha:beta'>|2:9", "")]
    [InlineData("""
        q=0; a=(alpha amber); declare -n p='a[q++]'
        printf '<%s>|%s' "${!p@Q}" "$q"
        """, "<'alpha'>|1", "")]
    [InlineData("""
        IFS=''; a=('' $'\177' alpha); p=('a[@]' value); value=scalar; i=0
        printf '<%s>|%s' "${!p[i++]#$(printf pattern >&2; printf a)}" "$i"
        """, "< \u007F alpha>|2", "pattern")]
    public async Task IndirectValueOperatorsDistinguishInitialPayloadFromSelectedBinding(
        string script, string expectedOutput, string expectedError)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Equal(expectedError, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    // Bash 5.2.21: unbound namerefs fail before operands; invalid assignment targets fail after the RHS.
    [Theory]
    [InlineData("declare -n p; printf '%s' \"${!p}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("declare -n p; printf '%s' \"${!p:-$(printf effect >&2; printf fallback)}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("declare -n p; printf '%s' \"${!p:=$(printf effect >&2; printf value)}\"; printf after", "p: invalid indirect expansion\n")]
    [InlineData("p=(); printf '%s' \"${!p[@]:=$(printf effect >&2; printf value)}\"; printf after", "effect!p[@]: invalid indirect expansion\n")]
    [InlineData("declare -A p; printf '%s' \"${!p[*]:=$(printf effect >&2; printf value)}\"; printf after", "effect!p[*]: invalid indirect expansion\n")]
    [InlineData("p=(); printf '%s' \"${!p[@]:=${missing:?inner}}\"; printf after", "missing: inner\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p:=new}\"; printf after", "a[0]: invalid variable name\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p=new}\"; printf after", "a[0]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[key]'; printf '%s' \"${!p:=new}\"; printf after", "a[key]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[key]'; printf '%s' \"${!p=new}\"; printf after", "a[key]: invalid variable name\n")]
    [InlineData("a=(); p='a[$(printf selector >&2; printf 0)]'; printf '%s' \"${!p:=$(printf rhs >&2; printf new)}\"; printf after", "selectorrhsa[$(printf selector >&2; printf 0)]: invalid variable name\n")]
    [InlineData("declare -A a; p='a[$(printf selector >&2; printf key)]'; printf '%s' \"${!p:=$(printf rhs >&2; printf new)}\"; printf after", "selectorrhsa[$(printf selector >&2; printf key)]: invalid variable name\n")]
    [InlineData("a=(); p='a[0]'; printf '%s' \"${!p:=${bad:?inner}}\"; printf after", "bad: inner\n")]
    public async Task Evaluation_InvalidIndirectionRetainsOnlyReachedOperandEffects(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    [Theory]
    [InlineData("unset p early late\nprintf '%s' \"${early:=one}\" \"${!p:-${late:=operand}}\" \"${late:=argument}\"; printf skipped\nprintf '<%s:%s:%s:%s>' \"$?\" \"${PIPESTATUS[*]}\" \"${early-unset}\" \"${late-unset}\"",
        "<1:1:one:unset>", "p: invalid indirect expansion\n", 0)]
    [InlineData("a=(); p='a[0]'\nprintf '%s' \"${!p:=$(printf rhs: >&2; printf value)}\"; printf skipped\nprintf '<%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"",
        "<1:1>", "rhs:a[0]: invalid variable name\n", 0)]
    [InlineData("f() { local -n p; printf '%s' \"${!p}\"; printf skipped; }\nf; printf skipped\nprintf '<%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"",
        "<1:1>", "p: invalid indirect expansion\n", 0)]
    [InlineData("unset p\nset -e\nprintf '%s' \"${!p}\" || printf skipped\nprintf after",
        "", "p: invalid indirect expansion\n", 1)]
    [InlineData("unset p\nset -e\neval 'printf \"%s\" \"${!p}\"; printf skipped\nprintf \"<loaded:%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\"' || printf skipped\nprintf '<caller:%s>' \"$?\"",
        "<loaded:1:1><caller:0>", "p: invalid indirect expansion\n", 0)]
    [InlineData("unset p\nvalue=$(eval 'printf \"%s\" \"${!p}\"\nprintf skipped')\nprintf '<%s:%s>' \"$?\" \"$value\"",
        "<1:>", "p: invalid indirect expansion\n", 0)]
    [InlineData("a=([2]=present); i=0\nprintf '<%s:%s>' \"${!a[i++]:-fallback}\" \"$i\"",
        "<fallback:1>", "", 0)]
    [InlineData("unset a; i=0\nprintf '%s' \"${!a[i++]:-fallback}\"\nprintf '<%s:%s:%s>' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        "<1:1:1>", "a[i++]: invalid indirect expansion\n", 0)]
    [InlineData("declare -A a; export b\nprintf '<%s:%s>' \"${!a[missing]:-fallback}\" \"${!b[1]:-unset}\"",
        "<fallback:unset>", "", 0)]
    public async Task IndirectNameFailuresUseTheReachedCommandUnit(string script, string expectedOutput,
        string expectedError, int expectedStatus)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Equal(expectedError, host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("declare -n p=absent; printf '%s' \"${!p:-fallback}\"", "absent")]
    [InlineData("declare -n p; value=keep; printf '%s' \"${value:-${!p}}\"", "keep")]
    [InlineData("declare -n p; false && printf '%s' \"${!p}\"; printf after", "after")]
    [InlineData("p=(); printf '%s' \"${!p[@]:-fallback}\"", "fallback")]
    [InlineData("a=(old); p='a[0]'; printf '%s' \"${!p:=$(printf bad >&2; printf new)}\"", "old")]
    [InlineData("a=(''); p='a[0]'; printf '<%s>' \"${!p=new}\"", "<>")]
    [InlineData("p=a; printf '%s|%s' \"${!p:=new}\" \"$a\"", "new|new")]
    [InlineData("declare -n p=absent; printf '%s' \"${!p:?stop}\"", "absent")]
    public async Task Evaluation_IndirectFailuresDoNotRejectNamedTargetsOrUnreachedOperands(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Bash exits 127 for these errors; the engine retains its fatal-expansion status of 1.
    [Theory]
    [InlineData("p=absent; printf '%s' \"${!p:?stop}\"; printf after", "!p: stop\n")]
    [InlineData("p=absent; printf '%s' \"${!p?}\"; printf after", "!p: parameter not set\n")]
    [InlineData("p=absent; absent=; printf '%s' \"${!p:?}\"; printf after", "!p: parameter null or not set\n")]
    [InlineData("p=absent; printf '%s' \"${!p:?${bad:?inner}}\"; printf after", "bad: inner\n")]
    public async Task Evaluation_IndirectErrorRetainsSubjectAndFirstFailure(string script, string expectedError)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Bash 5.2.21: indirect aggregate reads select the name before applying its value operator.
    [Theory]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[*]:1:2}\"", "<AL>")]
    [InlineData("declare -A p; p[key]=target; target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=([999999999]=target); target=VALUE; printf '<%s>' \"${!p[@]:1}\"", "<ALUE>")]
    [InlineData("p=(target); target=VALUE; i=0; printf '<%s>|%s' \"${!p[@]:i++:i++}\" \"$i\"", "<V>|2")]
    [InlineData("p=(target); target=VALUE; printf '<%s>' \"${!p[@]}\"", "<0>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@:-fallback}\" \"${!*#V}\" \"${!@:1}\"", "<VALUE><ALUE><ALUE>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@:+yes}\" \"${!@%UE}\"", "<yes><VAL>")]
    [InlineData("set -- target; target=VALUE; printf '<%s>' \"${!@=$(printf bad >&2; printf one)}\"", "<VALUE>")]
    [InlineData("set -- missing; printf '<%s>' \"${!@:-fallback}\"", "<fallback>")]
    [InlineData("set --; printf '<%s>' \"${!@}\" \"${!*:-fallback}\"", "<><fallback>")]
    [InlineData("set -- target; printf '<%s>' \"${!#:-fallback}\"", "<target>")]
    [InlineData("set -- zero one; false; printf '<%s>' \"${!?:1}\"", "<ero>")]
    [InlineData("p=(a b); IFS=; ab=VALUE; printf '<%s>' \"${!p[*]:1}\"", "<ALUE>")]
    public async Task Evaluation_IndirectAggregatesSupportValueOperators(string script, string expected)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Theory]
    [InlineData("set -- target; unset target", "!@")]
    [InlineData("set -- target; unset target", "!*")]
    [InlineData("set --", "!@")]
    public async Task Evaluation_IndirectPositionalAssignmentRejectsBeforeOperandEffects(string setup, string parameter)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup
            + "; printf '%s' \"${" + parameter + ":=$(printf bad >&2; printf one)}\"; printf after",
            EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("$" + parameter + ": cannot assign in this way\n", result.StdErr);
    }

    [Theory]
    [InlineData("set -- a b; IFS=:; ab=VALUE", "${!@:-fallback}")]
    [InlineData("p=(a b); IFS=; ab=VALUE", "${!p[@]:1}")]
    public async Task Evaluation_IndirectAtFieldsRetainNameSeparators(string setup, string expansion)
    {
        var result = await EndToEndHarness.RunScriptAsync(setup
            + "; printf '%s' \"" + expansion + "\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("a b: invalid variable name\n", result.StdErr);
    }

    // Prompt expansion remains unsupported and skips selector effects.
    [Theory]
    [InlineData("x@P")]
    [InlineData("a[$(printf effect >&2; printf 0)]@P")]
    public async Task Evaluation_UnsupportedParameterOperatorsFailBeforeSelection(string expression)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "x=hello; printf '%s' \"${" + expression + "}\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("${" + expression + "}: unsupported parameter operator\n", result.StdErr);
    }

    [Theory]
    [InlineData("$")]
    public async Task Evaluation_UnsupportedSpecialParametersFailWithoutThrowing(string parameter)
    {
        var result = await EndToEndHarness.RunScriptAsync(
            "printf '%s' \"${" + parameter + "}\"; printf after", EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("${" + parameter + "}: unsupported special parameter\n", result.StdErr);
    }

    [Fact]
    public async Task Evaluation_UnreachedUnsupportedParameterOperatorsRemainLazy()
    {
        const string Script = "false && printf '%s' \"${x@A}\"; x=keep; printf '%s' \"${x:-${a[$(printf effect >&2; printf 0)]@A}}\"";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("keep", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    // Isolated Git Bash 5.2.37 files; remove only recorded diagnostic location prefixes.
    [Theory]
    [InlineData("printf '<%s>\\n' \"${bad!}\"; printf 'after\\n'\n", "", "${bad!}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad.dot}\"; printf 'after\\n'\n", "", "${bad.dot}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad name}\"; printf 'after\\n'\n", "", "${bad name}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${2bad}\"; printf 'after\\n'\n", "", "${2bad}: bad substitution\n", 1)]
    [InlineData("printf '<%s>\\n' \"${}\"; printf 'after\\n'\n", "", "${}: bad substitution\n", 1)]
    [InlineData("false && printf '<%s>\\n' \"${bad!}\"; printf 'after\\n'\n", "after\n", "", 0)]
    [InlineData("name=ok; printf '<%s>\\n' \"${name:-${bad!}}\"; printf 'after\\n'\n", "<ok>\nafter\n", "", 0)]
    [InlineData("unset name; printf '<%s>\\n' \"${name:-${bad!}}\"; printf 'after\\n'\n", "", "${bad!}: bad substitution\n", 1)]
    [InlineData("probe_aa=1; probe_bb=2; printf '<%s>\\n' \"${!probe_*}\"; printf 'after\\n'\n", "<probe_aa probe_bb>\nafter\n", "", 0)]
    [InlineData("x=abc; printf '<%s>\\n' \"${x:}\" \"${x:1:}\"\n", "", "${x:}: bad substitution\n", 1)]
    [InlineData("set -- a b c d e; x=abc; printf '<%s>\\n' \"${##abc}\" \"${#x:-d}\"\n", "", "${#x:-d}: bad substitution\n", 1)]
    [InlineData("p=target; target=VALUE; printf '<%s>\\n' \"${!p}\"; p=absent; printf '<%s>\\n' \"${!p:-fallback}\"\n", "<VALUE>\n<fallback>\n", "", 0)]
    [InlineData("p='bad!'; printf '<%s>\\n' \"${!p}\"; printf after\n", "", "bad!: invalid variable name\n", 1)]
    [InlineData("unset p; printf '<%s>\\n' \"${!p}\"; printf after\n", "", "p: invalid indirect expansion\n", 1)]
    [InlineData("x=VALUE; declare -n p=x; printf '<%s>\\n' \"${!p}\"\n", "<x>\n", "", 0)]
    [InlineData("set -- target; unset target; printf '<%s>\\n' \"${!1:=value}\" \"$target\"\n", "<value>\n<value>\n", "", 0)]
    [InlineData("p=9; set --; printf '<%s>\\n' \"${!p:=$(printf effect >&2; printf value)}\"; printf after\n", "", "effect9: invalid variable name\n", 1)]
    [InlineData("a=(zero one two); p='a[i++]'; i=1; printf '<%s>\\n' \"${!p}\" \"$i\"\n", "<one>\n<2>\n", "", 0)]
    [InlineData("p=('a[q++]' spare); a=(alpha amber); p_index=0; q=0\nprintf '<%s>|%s|%s' \"${!p[p_index++]}\" \"$p_index\" \"$q\"\n",
        "<alpha>|1|1", "", 0)]
    [InlineData("false && printf '<%s>\\n' \"${x@Z}\"; printf after\n", "after", "", 0)]
    [InlineData("probe_a=1; probe_b=2; f(){ printf 'n=%s\\n' \"$#\"; printf '<%s>\\n' \"$@\"; }; f \"${!probe_@}\"\n", "n=2\n<probe_a>\n<probe_b>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"${!:-missing}\" \"$!\" \"${#!}\"\n", "<missing>\n<>\n<0>\n", "", 0)]
    [InlineData("printf '<%s>\\n' \"${!:=$(printf bad >&2; printf value)}\"; printf after\n", "", "$!: cannot assign in this way\n", 1)]
    [InlineData("printf '<%s>\\n' \"${!!}\"; printf after\n", "", "${!!}: bad substitution\n", 1)]
    [InlineData("a=([2]=abcdef [3]=uvwxyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p:-fallback}\" \"$i\"\n", "<abcdef>\n<3>\n", "", 0)]
    [InlineData("a=([2]=abc [3]=xyz); p='a[i++]'; i=2; printf '<%s>\\n' \"${!p#x}\" \"$i\"\n", "<yz>\n<4>\n", "", 0)]
    public async Task Evaluation_ParameterGrammarAndOperators(
        string script, string expectedOutput, string expectedError, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(expectedError, result.StdErr);
    }

    // Diagnostic source rendering remains separate from the reached-error/effect contract.
    [Theory]
    [InlineData("printf '<%s>\\n' \"$(printf 'effect\\n' >&2)${bad!}\"; printf 'after\\n'\n", 1)]
    [InlineData("printf '<%s>\\n' \"${bad!}$(printf 'effect\\n' >&2)\"; printf 'after\\n'\n", 0)]
    public async Task Evaluation_MalformedParametersRetainOnlyEarlierEffects(string script, int expectedEffects)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("bad substitution", result.StdErr);
        Assert.Equal(expectedEffects, result.StdErr.Split("effect\n").Length - 1);
    }

    [Fact]
    public async Task Evaluation_InvalidParameterTailDoesNotEvaluateItsSelector()
    {
        const string Script = "printf '<%s>\\n' \"${a[$(printf bad >&2; printf 0)]!}\"; printf after\n";
        var result = await EndToEndHarness.RunScriptAsync(Script, EndToEndHarness.BuildTree([]));

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.StartsWith("${a[", result.StdErr);
        Assert.Contains("bad substitution", result.StdErr);
    }
}
