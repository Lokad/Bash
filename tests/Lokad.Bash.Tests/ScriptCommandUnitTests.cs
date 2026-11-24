using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ScriptCommandUnitTests
{
    public static IEnumerable<object[]> CompleteUnits()
    {
        yield return ["printf A\n)\n", "A", 2, true];
        yield return ["printf wrong; )\n", "", 2, true];
        yield return ["printf prefix \"$(for ((i=7)); do :; done)\"\nprintf wrong", "prefix", 0, true];
        yield return ["printf wrong; true && value=(\"$(for ((i=7)); do :; done)\")", "", 1, true];
        yield return ["printf A;\n)\n", "A", 2, true];
        yield return ["{\nprintf A\n}\n)\n", "A", 2, true];
        yield return ["{\nprintf wrong\n)\n}\n", "", 2, true];
        yield return ["f(){\nprintf wrong\n)\n}\n", "", 2, true];
        yield return ["f(){ printf A; }\nf\n)\n", "A", 2, true];
        yield return ["printf A |\ncat\n)\n", "A", 2, true];
        yield return ["false ||\nprintf A\n)\n", "A", 2, true];
        yield return ["printf '%s' 'a\nb'\n)\n", "a\nb", 2, true];
        yield return ["cat <<EOF\nbody ) '\nEOF\n)\n", "body ) '\n", 2, true];
        yield return ["printf wrong; cat <<EOF; )\nbody\nEOF\n", "", 2, true];
        yield return ["f(){ cat <<EOF\nbody\nEOF\n}\nf\n)\n", "body\n", 2, true];
        yield return ["printf A\nexit 7\n)\n", "A", 7, false];
        yield return ["set -e\nfalse\n)\n", "", 1, false];
        yield return ["trap 'printf \"trap:%s\" \"$?\"' EXIT\nprintf A\n)\n", "Atrap:2", 2, true];
        yield return ["x=$((1/0)); printf wrong\nprintf 'next:%s' \"$?\"\n", "next:1", 0, false];
        yield return ["readonly x=old\nx=new; printf wrong\nprintf 'next:%s:%s' \"$?\" \"$x\"\n", "next:1:old", 0, false];
        yield return ["set -e\nx=$((1/0))\nprintf after\n", "after", 0, false];
        yield return ["printf A\nx=${missing?bad}\nprintf wrong\n", "A", 1, false];
        yield return ["# comment\n\n# trailing", "", 0, false];
    }

    [Theory]
    [MemberData(nameof(CompleteUnits))]
    public Task PublicEntryPointRunsOnlyCompletedUnits(string source, string output, int status, bool syntaxError) =>
        AssertUnitsAsync(source, output, status, syntaxError);

    [Theory]
    [MemberData(nameof(CompleteUnits))]
    public Task EvalSharesTheCommandUnitPath(string source, string output, int status, bool syntaxError) =>
        AssertUnitsAsync("eval '" + source.Replace("'", "'\\''", StringComparison.Ordinal) + "'", output, status, syntaxError);

    [Theory]
    [InlineData("printf discarded |&\\\n", "Expected command after pipeline operator.")]
    [InlineData("printf discarded &&", "Expected command after list operator.")]
    [InlineData("printf discarded >\\", "Expected a word after redirection operator.")]
    [InlineData("read -r word <<\\", "Expected a word after redirection operator.")]
    [InlineData("if true; then :\n", "Expected fi keyword.")]
    [InlineData("if true; then :", "Expected terminator before reserved word.")]
    [InlineData("while false; do :\n", "Expected done keyword.")]
    [InlineData("case a in a) :;;\n", "Expected case pattern.")]
    [InlineData("{ :;\n", "Expected } to close a group.")]
    [InlineData("( :\n", "Expected ) to close a subshell.")]
    [InlineData("f()\n", "Expected compound command as function body.")]
    public async Task IncompleteGrammarReportsTheReachedReaderLine(string ending, string parserMessage)
    {
        var source = "printf before\n" + ending;
        Assert.False(Parser.TryParse(source, out _, out var error));
        Assert.Equal(parserMessage, error?.Message);
        Assert.Equal(source.Length, error?.Offset);

        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal("line 3: syntax error: unexpected end of file\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("printf '[%s]' word\\", "[word]")]
    [InlineData("f(){ printf '%s' \"$#\"; }; f \\", "0")]
    [InlineData("printf kept; \\", "kept")]
    [InlineData("printf '[%s]' word\\\\\\", "[word\\]")]
    [InlineData("printf '[%s]' \"`printf '%s' word\\\\`\"", "[word\\]")]
    public async Task FinalFileContinuationPreservesCompletedWordsAndUnits(string script, string output)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal(output, host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData(true, "printf '[%s]' word\\", "[word\\]")]
    [InlineData(false, "printf '[%s]' \"word\"\\", "[word\\]")]
    [InlineData(true, "f(){ printf '%s' \"$#\"; }; f \\", "1")]
    [InlineData(false, "f(){ printf '%s' \"$#\"; }; f \\", "1")]
    public async Task LoadedReaderKeepsTheFinalBackslashInItsArgument(bool source, string input, string output)
    {
        await using var host = new TestHost();
        if (source)
            host.AddFile("/input", input);
        var invoke = source ? "source input" : "eval '" + input.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        var result = await host.RunAsync(invoke, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task SourcedFinalRedirectionPassesLiteralBackslashToTheHost()
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        host.AddFile("/input", "printf written >out\\");

        var result = await host.RunAsync("source input", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("written", host.ReadFile("/out\\"));
        Assert.False(host.FileExists("/out"));
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task ContinuedConditionalWordReportsOpeningAndReachedEofLines()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("printf prior\n[[ -n value\\", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("prior", host.StandardOutput.TextContent);
        Assert.Equal("line 2: unexpected EOF while looking for `]]'\nline 4: syntax error: unexpected end of file\n",
            host.StandardError.TextContent);
    }

    [Fact]
    public async Task SourcedMissingCommandReportsEndOfInputThroughTheReachedDescriptor()
    {
        await using var host = new TestHost();
        host.AddFile("/fixture", "printf inside\nprintf discarded ||");
        var result = await host.RunAsync("source ./fixture 2>report\nprintf 'outer:%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("insideouter:2", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("./fixture: line 3: syntax error: unexpected end of file\n", host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("for", "newline", "for")]
    [InlineData("case\n", "newline", "case")]
    [InlineData("function", "newline", "function")]
    [InlineData("case a in (a", "newline", "case a in (a")]
    [InlineData("if true; then )\n", ")", "if true; then )")]
    [InlineData("case a in (a ;;\n", ";;", "case a in (a ;;")]
    [InlineData("f() printf hidden\n", "printf", "f() printf hidden")]
    public async Task IncompleteCompoundReportsTheReachedTokenAndLine(string unit, string token, string line)
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\n" + unit);

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal($"input: line 2: syntax error near unexpected token `{token}'\ninput: line 2: `{line}'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task IncompleteSourcedCompoundReportsHereDocumentWarningBeforeEndOfInput()
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        host.AddFile("/input", "printf before\nif true; then read -r word <<NOTE\nsample\n");

        var result = await host.RunAsync("source input 2>report\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal("input: line 3: warning: here-document at line 2 delimited by end-of-file (wanted `NOTE')\n"
            + "input: line 4: syntax error: unexpected end of file\n", host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData("\n", 5)]
    public async Task IncompleteEvalCompoundAddsItsReaderLineToTheCallSite(string terminator, int line)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("eval 'printf before\nif true; then :" + terminator
            + "'\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal($"eval: line {line}: syntax error: unexpected end of file\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("printf '<%s:%s>' \"$?\" \"${PIPESTATUS[*]}\"; (exit 7); value=", "<2:2>|7:7", 7)]
    [InlineData("printf discarded; printf unfinished >", "|2:2", 2)]
    public async Task QuotedSubstitutionFailurePublishesParserStatusBeforeAnyCompletePrefix(
        string prefix, string output, int status)
    {
        var script = "trap 'printf \"|%s:%s\" \"$?\" \"${PIPESTATUS[*]}\"' EXIT\n"
            + prefix + "\"$(for ((i=7)); do :; done)\"\nprintf discarded";
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task QuotedSubstitutionDiagnosticPrecedesPrefixRedirections()
    {
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        const string Script = """
            exec 2>diag; printf prefix >&2; value="$(for ((i=7)); do :; done)"
            : >discarded
            """;

        Assert.False(Parser.TryParse(Script, out _, out _));
        var result = await host.RunAsync(Script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("syntax error: Arithmetic for header requires two semicolons.\n", host.StandardError.TextContent);
        Assert.Equal("prefix", host.ReadFile("/diag"));
        Assert.Equal("diag", Assert.Single(host.CaptureEntries()).Key);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(true, "value=$(for ((i=7)); do : >body; break; done)", 2, "2:inner:source-arg", "Arithmetic for header")]
    [InlineData(false, "value=\"$(printf hidden |)\"", 1, "257:inner:caller-arg", "syntax error near unexpected token `)'")]
    public async Task FatalSubstitutionInsideScriptBuiltinRunsExitWithActiveScope(
        bool source, string malformed, int status, string trapOutput, string diagnostic)
    {
        var inner = "value=inner\n: >completed\n" + malformed + "\n: >inner-after\n";
        var invoke = source ? "source unit source-arg" : "eval '" + inner + "'";
        var script = """
            set -- caller-arg
            value=outer
            trap 'printf '%s:%s:%s' "$?" "$value" "$1"' EXIT

            """ + invoke + " >out 2>err || : >fallback\n: >caller-after";
        foreach (var prepared in new[] { false, true })
        {
            var governor = new MemoryGovernor(256 * 1024);
            using var owner = governor.CreateScope();
            await using var host = new TestHost { MemoryBudget = owner };
            if (source)
                host.AddFile("/unit", inner);
            var actualStatus = prepared
                ? (await new Engine().RunAsync(new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(script)),
                    host, owner, CancellationToken.None)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(status, actualStatus);
            Assert.Empty(host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
            Assert.Equal(trapOutput, host.ReadFile("/out"));
            Assert.Contains(diagnostic, host.ReadFile("/err"), StringComparison.Ordinal);
            Assert.Equal(source ? new[] { "completed", "err", "out", "unit" } : ["completed", "err", "out"],
                host.CaptureEntries().Keys.OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(0, host.OpenFileCount);
            if (!prepared)
                Assert.Equal(0, governor.ReservedBytes);
            owner.Dispose();
            Assert.Equal(0, governor.ReservedBytes);
        }
    }

    [Theory]
    [InlineData("for ((i=7)); do :; done", "2", "syntax error:")]
    [InlineData("value=\"$(for ((i=7)); do :; done)\"", "2", "syntax error:")]
    [InlineData("value=(\"$(for ((i=7)); do :; done)\")", "1", "syntax error:")]
    [InlineData("value=$(printf hidden |", "2", "unit: line 3: unexpected EOF while looking for matching `)'")]
    public async Task RecoverableSourceSyntaxFailureReturnsToCaller(string malformed, string builtinStatus, string diagnostic)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "printf before\n" + malformed);
        var result = await host.RunAsync("source unit\nprintf ':%s:after' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:" + builtinStatus + ":after", host.StandardOutput.TextContent);
        Assert.Contains(diagnostic, host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("value=$(cat <<", "newline", "value=$(cat <<")]
    [InlineData("value=$(cat <<;\r\n)\n", ";", "value=$(cat <<;\r")]
    public async Task MissingRedirectionOperandReportsTheRejectedTokenAndItsPhysicalLine(
        string malformed, string token, string line)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf before\n" + malformed, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal($"syntax error near unexpected token `{token}'\n`{line}'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("(", ")", "2:source-arg;2")]
    [InlineData("value=$(", "); printf '%s' \"$value\"", "1:caller-arg;0")]
    public async Task SourceSyntaxExitStaysAtItsChildShellBoundary(string open, string close, string output)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "value=$(for ((i=7)); do :; done)");
        var script = "set -- caller-arg\n" + open
            + " trap 'printf \"%s:%s;\" \"$?\" \"$1\"' EXIT; source unit source-arg; : >child-after " + close
            + "\nprintf '%s' \"$?\"";
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Contains("Arithmetic for header", host.StandardError.TextContent, StringComparison.Ordinal);
        Assert.False(host.FileExists("/child-after"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("readonly value=old\nvalue=$(exit 7); printf wrong >skipped\n", "1:old", 0, "readonly variable")]
    [InlineData("set -e\nreadonly value=old\nvalue=new || printf wrong >skipped\n", "", 1, "readonly variable")]
    [InlineData("set -e\nreadonly value=old\neval 'value=new; printf wrong >skipped\n:' || printf wrong >skipped\n", "0:old", 0, "readonly variable")]
    [InlineData("set -e\nvalue=$((1/0)); printf wrong >skipped\n", "1:unset", 0, "division by 0")]
    [InlineData("set -e\nprintf '%s' p{Z..a}s; printf wrong >skipped\n", "", 1, "bad substitution")]
    [InlineData("declare -n ref\n: ${ref:=bad name}; printf wrong >skipped\n", "2:unset", 0, "not a valid identifier")]
    public async Task ExpansionFailuresResumeOnlyAtTheirNextAllowedUnit(
        string prefix, string expectedOutput, int expectedStatus, string diagnostic)
    {
        const string tail = "printf '%s:%s' \"$?\" \"${value-unset}\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(prefix + tail, host)).ExitCode
                : (await host.RunAsync(prefix + tail, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Contains(diagnostic, host.StandardError.TextContent, StringComparison.Ordinal);
            Assert.False(host.FileExists("/skipped"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Theory]
    [InlineData("readonly v=; i=0\n(exit 7) && :\nv=$((++i)) >skipped; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[1:1:1]", "line 3: ")]
    [InlineData("readonly v=; i=0\nf() { local PIPESTATUS=(8 9); v=$((++i)); }\nf; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[1:0:1]", "line 2: ")]
    [InlineData("readonly v=; i=0\nf(){ local -; set -e; (exit 7) && :\nv=$((++i)); }\nf\nprintf skipped",
        7, "", "line 3: ")]
    [InlineData("readonly v=; i=0\nf(){ local -; set +e; v=$((++i)); }\nset -e\nsource ./fixture\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[1:1:1][0:0:1]", "./fixture: line 2: ")]
    [InlineData("readonly v=; i=0\nset -e\n(exit 7) && :\neval 'v=$((++i)); printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' || printf alternative\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"",
        0, "[1:1:1][0:0:1]", "line 5: ")]
    [InlineData("readonly v=; i=0\nset -e\n(eval 'v=$((++i)); printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' || printf alternative)\nprintf skipped",
        1, "", "line 4: ")]
    [InlineData("readonly v=; i=0\ntext=$(printf begin; eval 'v=$((++i)); printf skipped\nprintf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"')\nprintf '[%s:%s:%s]<%s>' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"$text\"",
        0, "[1:1:0]<begin>", "line 4: ")]
    [InlineData("readonly v=; i=0\nset -e\ntrap 'printf \"[%s:%s:%s]\" \"$?\" \"${PIPESTATUS[*]}\" \"$i\"' EXIT\n(exit 7) && :\nv=$((++i)); printf skipped",
        7, "[7:7:1]", "line 5: ")]
    public async Task RejectedAssignmentsPublishStatusAtTheReachedReader(
        string script, int expectedStatus, string expectedOutput, string diagnosticPrefix)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            host.AddFile("/fixture", "f; printf skipped\nprintf '[%s:%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\"\n");
            var status = prepared
                ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(expectedStatus, status);
            Assert.Equal(expectedOutput, host.StandardOutput.TextContent);
            Assert.Equal(diagnosticPrefix + "v: readonly variable\n", host.StandardError.TextContent);
            Assert.False(host.FileExists("/skipped"));
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    private static async Task AssertUnitsAsync(string source, string output, int status, bool syntaxError)
    {
        await using var host = new TestHost();
        var result = await BashScript.RunAsync(host, source, EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(syntaxError, host.StandardError.TextContent.Contains("syntax error", StringComparison.Ordinal));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("v=plain; \\\n ) # comment\n", ")", " ) # comment")]
    [InlineData("v=plain;\\\n;\n", ";;", ";")]
    [InlineData("&& :\n", "&&", "&& :")]
    public async Task UnexpectedTokenShowsThePhysicalLineOfTheReachedUnit(string unit, string token, string line)
    {
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };

        var result = await host.RunAsync("printf prior\n" + unit + "printf wrong\n", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("prior", host.StandardOutput.TextContent);
        Assert.Equal($"syntax error near unexpected token `{token}'\n`{line}'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ -n\nx ]]\n", "unexpected argument `newline' to conditional unary operator\n", "-n", "[[ -n")]
    [InlineData("[[ -n ]]; :\n", "unexpected argument `]]' to conditional unary operator\n", ";", "[[ -n ]]; :")]
    [InlineData("[[ x\n ]]\n", "unexpected token `newline', conditional binary operator expected\n", "x", "[[ x")]
    [InlineData("[[ x =~ ]]\n", "unexpected argument `]]' to conditional binary operator\n", "]]", "[[ x =~ ]]")]
    [InlineData("[[ ( ( x == # note\ny ) ) ]]\n",
        "unexpected argument `newline' to conditional binary operator\nexpected `)'\nexpected `)'\n", "note", "[[ ( ( x == # note")]
    public async Task ConditionalDiagnosticRetainsTheFailedGrammarRole(string script, string reason, string token, string line)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal(reason + $"syntax error near `{token}'\n`{line}'\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("[[ a wrong $(printf hidden) ]] >untouched\n",
        "input: line 2: conditional binary operator expected\ninput: line 2: syntax error near `wrong'\n"
        + "input: line 2: `[[ a wrong $(printf hidden) ]] >untouched'\n")]
    [InlineData("[[ (\n (\n a ]]\n",
        "input: line 3: unexpected token `]]', expected `)'\ninput: line 2: expected `)'\n"
        + "input: line 4: syntax error near `]]'\ninput: line 4: ` a ]]'\n")]
    [InlineData("[[ a == a\n ) ]]\n",
        "input: line 2: syntax error in conditional expression: unexpected token `)'\n"
        + "input: line 3: syntax error near `)'\ninput: line 3: ` ) ]]'\n")]
    [InlineData("[[ (\n a == first\\\n\"last\n",
        "input: line 4: unexpected EOF while looking for matching `\"'\n"
        + "input: line 5: unexpected argument to conditional binary operator\ninput: line 2: expected `)'\n")]
    [InlineData("[[ a == a\n# the last physical line",
        "input: line 2: unexpected EOF while looking for `]]'\ninput: line 4: syntax error: unexpected end of file\n")]
    [InlineData("[[ a == a\\\n",
        "input: line 2: unexpected EOF while looking for `]]'\ninput: line 4: syntax error: unexpected end of file\n")]
    [InlineData("[[ a == \\\n",
        "input: line 3: unexpected argument `EOF' to conditional binary operator\ninput: line 3: syntax error: unexpected end of file\n")]
    [InlineData("[[ a == ",
        "input: line 2: unexpected argument `newline' to conditional binary operator\n"
        + "input: line 2: syntax error near `=='\ninput: line 2: `[[ a == '\n")]
    [InlineData("[[ a <<< value ]] >untouched\n",
        "input: line 2: unexpected token `<<<', conditional binary operator expected\n"
        + "input: line 2: syntax error near `<<<'\ninput: line 2: `[[ a <<< value ]] >untouched'\n")]
    [InlineData("[[ -n |& ]]\n",
        "input: line 2: unexpected argument `|&' to conditional unary operator\n"
        + "input: line 2: syntax error near `&'\ninput: line 2: `[[ -n |& ]]'\n")]
    [InlineData("[[ a == >> ]]\n",
        "input: line 2: unexpected argument `>>' to conditional binary operator\n"
        + "input: line 2: syntax error near `>>'\ninput: line 2: `[[ a == >> ]]'\n")]
    [InlineData("[[ ( a == a ;; ]]\n",
        "input: line 2: unexpected token `;;', expected `)'\n"
        + "input: line 2: syntax error near `;'\ninput: line 2: `[[ ( a == a ;; ]]'\n")]
    [InlineData("[[ a ;\\\n;\\\n& ]]\n",
        "input: line 4: unexpected token `;;&', conditional binary operator expected\n"
        + "input: line 4: syntax error near `&'\ninput: line 4: `& ]]'\n")]
    [InlineData("[[ a ;;\\\n x ]]\n",
        "input: line 3: unexpected token `;;', conditional binary operator expected\n"
        + "input: line 3: syntax error near ` '\ninput: line 3: ` x ]]'\n")]
    [InlineData("[[ a ;;\\\n\n x ]]\n",
        "input: line 3: unexpected token `;;', conditional binary operator expected\n"
        + "input: line 3: syntax error near `\n'\ninput: line 3: `'\n")]
    [InlineData("[[ a ;;\\\n",
        "input: line 3: unexpected token `;;', conditional binary operator expected\ninput: line 3: syntax error\n")]
    [InlineData("[[ -n $\\\n'last\n",
        "input: line 3: unexpected EOF while looking for matching `''\n"
        + "input: line 4: unexpected argument to conditional unary operator\n")]
    [InlineData("[[ x == $(printf '%s' 'last\n",
        "input: line 2: unexpected EOF while looking for matching `''\n"
        + "input: line 3: unexpected argument to conditional binary operator\n")]
    [InlineData("printf '%s' \"$( [[ x == \"last\n",
        "input: line 2: unexpected EOF while looking for matching `\"'\n"
        + "input: line 3: unexpected argument to conditional binary operator\n")]
    [InlineData("[[ x == `printf open\n",
        "input: line 2: unexpected EOF while looking for matching ``'\n"
        + "input: line 3: unexpected argument to conditional binary operator\n")]
    [InlineData("[[ (\n (\n x == $(printf '%s' 'open\n",
        "input: line 4: unexpected EOF while looking for matching `''\n"
        + "input: line 5: unexpected argument to conditional binary operator\n"
        + "input: line 3: expected `)'\ninput: line 2: expected `)'\n")]
    public async Task SourcedConditionalDiagnosticsUseTheLineForEachReaderMessage(string unit, string diagnostic)
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        host.AddFile("/input", "printf before\n" + unit);

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
        Assert.False(host.FileExists("/untouched"));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("[[ \"unfinished", "\"", "unexpected token `", "' in conditional command\n")]
    [InlineData("[[ $'unfinished", "'", "unexpected token `", "' in conditional command\n")]
    [InlineData("[[ ( x =~ \"unfinished", "\"", "unexpected argument to conditional binary operator\nline 1: expected `)'\n", "")]
    public async Task ConditionalQuoteEndOfInputReportsTheUnfinishedRole(string script, string quote, string reason, string suffix)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        var prefix = System.Text.Encoding.UTF8.GetBytes($"line 1: unexpected EOF while looking for matching `{quote}'\nline 2: " + reason);
        byte[] expected = suffix.Length == 0 ? prefix : [.. prefix, 0xff, .. System.Text.Encoding.UTF8.GetBytes(suffix)];
        Assert.Equal(expected, host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("printf '%s' 'open\n", '\'', 2, "Unterminated single-quoted string.", 0)]
    [InlineData("printf '%s' $\\\n'open\n", '\'', 3, "Unterminated single-quoted string.", 0)]
    [InlineData("printf '%s' $\\\n\"open\n", '"', 3, "Unterminated double-quoted string.", 0)]
    [InlineData("printf '%s' \"open\\", '"', 2, "Unexpected end of input after escape.", -1)]
    public async Task UnclosedWordQuoteReportsItsOpeningLine(string unit, char quote, int line, string message, int offsetAdjustment)
    {
        var script = "printf before\n" + unit;
        Assert.False(Parser.TryParse(script, out _, out var error));
        Assert.Equal(message, error?.Message);
        Assert.Equal(script.Length + offsetAdjustment, error?.Offset);

        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal($"line {line}: unexpected EOF while looking for matching `{quote}'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task UnclosedQuoteInReachedLegacyCaptureUsesItsReaderOrigin()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nvalue=`printf '%s' 'open\n`\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal("input: command substitution: line 3: unexpected EOF while looking for matching `''\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("a=('open\n", '\'')]
    [InlineData("declare -a a=(\"open\n", '"')]
    [InlineData("a=(word\\", ')')]
    public async Task UnclosedSourcedInitializerReportsAssignmentFailure(string unit, char delimiter)
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        host.AddFile("/input", "printf before\n" + unit);

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:1:after", host.StandardOutput.TextContent);
        Assert.Equal($"input: line 2: unexpected EOF while looking for matching `{delimiter}'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Theory]
    [InlineData("x ==", "", "")]
    [InlineData("$(cat <<ONE) ==", "first\nONE\n", "line 2: warning: command substitution: 1 unterminated here-document\n")]
    public async Task ConditionalFailureLeavesLaterCaptureUnread(string before, string body, string warnings)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("printf prior\n[[ " + before + "\n" + body
            + "$(cat <<TWO) ]]\nsecond\nTWO\n", CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("prior", host.StandardOutput.TextContent);
        Assert.Equal(warnings + "unexpected argument `newline' to conditional binary operator\n"
            + $"syntax error near `=='\n`[[ {before}'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task SyntaxDiagnosticRetainsItsLargeRecordUntilTheWriteSettles()
    {
        using var cancellation = new CancellationTokenSource();
        var memory = new MemoryGovernor(256 * 1024);
        var entered = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = new TaskCompletionSource<DescriptorWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = memory,
            BeforeAppend = (_, bytes) => entered.SetResult(bytes)
        };
        host.EnqueueAppendTask(write.Task);
        var line = ") # " + new string('x', 16 * 1024);
        var running = BashScript.RunAsync(host, line, EvaluationOptions.Default, cancellation.Token);
        try
        {
            var bytes = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal($"syntax error near unexpected token `)'\n`{line}'\n",
                System.Text.Encoding.UTF8.GetString(bytes.Span));
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes >= line.Length + bytes.Length);
        }
        finally
        {
            write.TrySetResult(DescriptorWriteResult.Open);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task LaterSyntaxFailurePreservesDirectoryEffectsAndClosesPersistentDescriptors()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddDirectory("/work");
        var result = await BashScript.RunAsync(host,
            "cd /work\nexec 3>out\nprintf first >&3\ntrap 'printf trap >&3' EXIT\n)\n",
            EvaluationOptions.Default, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(new ShellPath("/work"), result.CurrentDirectory);
        Assert.Equal("firsttrap", host.ReadFile("/work/out"));
        Assert.Equal("syntax error near unexpected token `)'\n`)'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void WholeScriptParsingStillValidatesBeforeExecution()
    {
        Assert.False(Parser.TryParse("printf earlier\n)\n", out var script, out var error));
        Assert.Null(script);
        Assert.NotNull(error);
    }
}
