using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ScriptCommandUnitTests
{
    private const string PrintParserStatus = "printf \"<%s:%s>\" \"$?\" \"${PIPESTATUS[*]}\"";

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
    [InlineData("printf discarded |&\\\n", "Expected command after pipeline operator.", 3)]
    [InlineData("printf discarded &&", "Expected command after list operator.", 3)]
    [InlineData("printf discarded >\\", "Expected a word after redirection operator.", 3)]
    [InlineData("printf discarded>\\", "Expected a word after redirection operator.", 4)]
    [InlineData(": \"argument\">\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData(": 2<\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData(": 2< \\\n", "Expected a word after redirection operator.", 3)]
    [InlineData(": 3>&->\\\n", "Expected a word after redirection operator.", 3)]
    [InlineData("{ :; }>\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData("( : )>\\\n", "Expected a word after redirection operator.", 3)]
    [InlineData("[[ x ]]>\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData("a[1 + 2]=v>\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData("a=(x)>\\\n", "Expected a word after redirection operator.", 4)]
    [InlineData("read -r word <<\\", "Expected a word after redirection operator.", 3)]
    [InlineData("if true; then :\n", "Expected fi keyword.", 3)]
    [InlineData("if true; then :", "Expected terminator before reserved word.", 3)]
    [InlineData("while false; do :\n", "Expected done keyword.", 3)]
    [InlineData("case a in a) :;;\n", "Expected case pattern.", 3)]
    [InlineData("{ :;\n", "Expected } to close a group.", 3)]
    [InlineData("( :\n", "Expected ) to close a subshell.", 3)]
    [InlineData("f()\n", "Expected compound command as function body.", 3)]
    public async Task IncompleteGrammarReportsTheReachedReaderLine(string ending, string parserMessage, int line)
    {
        var source = "printf before\n" + ending;
        Assert.False(Parser.TryParse(source, out _, out var error));
        Assert.Equal(parserMessage, error?.Message);
        Assert.Equal(source.Length, error?.Offset);

        await using var host = new TestHost();
        var result = await host.RunAsync(source, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal($"line {line}: syntax error: unexpected end of file\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData(": word>\\\n", "line 4: syntax error: unexpected end of file")]
    [InlineData("value=$(: word>\\\n", "line 4: unexpected EOF while looking for matching `)'")]
    [InlineData("value=$(: word >\\\n", "line 3: unexpected EOF while looking for matching `)'")]
    public async Task SourcedContinuationRetainsTheRedirectionLookaheadLine(string ending, string diagnostic)
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "printf before\n" + ending);

        var result = await host.RunAsync("source unit\nprintf ':%s' \"$?\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2", host.StandardOutput.TextContent);
        Assert.Equal("unit: " + diagnostic + "\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("probe[0\nprintf discarded\n", 2, ']')]
    [InlineData(">out probe[0\n", 2, ']')]
    [InlineData("false && probe[0\n", 2, ']')]
    [InlineData("probe\\\n[0\n", 3, ']')]
    [InlineData("probe[0\n: 'text\n", 3, '\'')]
    [InlineData("value=$(printf inside\nprobe[0\n)\n", 3, ']')]
    [InlineData("probe[0\n: $\\\n{unset\n", 4, '}')]
    [InlineData("probe[0\n: ${unset:-\n 'text\n", 4, '\'')]
    [InlineData("probe[0\n: `printf text\n", 3, '`')]
    [InlineData("probe[0\n: $((1+\n ${unset\n", 3, ')')]
    [InlineData("probe[0\n: $\\\n[1+\n", 4, ']')]
    [InlineData("probe[${unset\n", 2, '}')]
    [InlineData("probe[0\n: $((1+2)\n", 3, ')')]
    public async Task IncompleteAssignmentSelectorStopsTheCommandUnit(string ending, int line, char delimiter)
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };

        var result = await host.RunAsync("printf before\n" + ending, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal($"line {line}: unexpected EOF while looking for matching `{delimiter}'\n", host.StandardError.TextContent);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task ACompleteAdjacentSubshellInASelectorKeepsItsArithmeticFallback()
    {
        const string script = "probe[0 + $((printf 0)\n)]=ok\nprintf '%s' \"${probe[0]}\"";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;
            Assert.Equal(0, status);
            Assert.Equal("ok", host.StandardOutput.TextContent);
            Assert.Empty(host.StandardError.TextContent);
        }
    }

    [Theory]
    [InlineData(": \"\n${unset\n", 3, '}')]
    [InlineData(": $((1+2)\n", 2, ')')]
    public async Task IncompleteExpansionsInAnOrdinaryWordReportTheOpenedDelimiter(string ending, int line, char delimiter)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("printf before\n" + ending, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal($"line {line}: unexpected EOF while looking for matching `{delimiter}'\n", host.StandardError.TextContent);
    }

    [Fact]
    public async Task AssignmentSelectorReportsTheBracketAfterAContinuedHereDocumentRead()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("printf before\nprobe[$(cat <<END)\\\n]=item\n0\nEND\nprintf discarded\n",
            CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal("line 2: warning: command substitution: 1 unterminated here-document\n"
            + "line 2: unexpected EOF while looking for matching `]'\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("=", "\n")]
    [InlineData("+=", "")]
    public async Task CollectedHereDocumentBodyDoesNotSupplyTheSelectorOperator(string operatorText, string finalLf)
    {
        var script = "arr[$(cat <<END)]\\\n" + operatorText + "item\n0\nEND" + finalLf;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;
            Assert.Equal(127, status);
            Assert.Empty(host.StandardOutput.TextContent);
            Assert.Equal("line 1: warning: command substitution: 1 unterminated here-document\n"
                + $"line 6: arr[{operatorText}item: command not found\n", host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task DeclarationRetainsTheWordsFromAContinuedSelector()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("declare arr[$(cat <<END)]\\\n=item\n0\nEND\n", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("line 1: warning: command substitution: 1 unterminated here-document\n"
            + "line 5: declare: `arr[=item': not a valid identifier\n"
            + "line 5: declare: `0]': not a valid identifier\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("", "", "new")]
    [InlineData("", "+", "oldnew")]
    [InlineData("declare ", "+", "oldnew")]
    public async Task SelectorOperatorCanFollowACollectedHereDocumentBody(string declaration, string append, string expected)
    {
        var script = "arr[0]=old\n" + declaration + "arr[$(cat <<END)]" + append
            + "\\\n0\nEND\n=new\nprintf '%s' \"${arr[0]}\"\n";
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;
            Assert.Equal(0, status);
            Assert.Equal(expected, host.StandardOutput.TextContent);
            Assert.Equal("line 2: warning: command substitution: 1 unterminated here-document\n", host.StandardError.TextContent);
        }
    }

    [Theory]
    [InlineData("\"$LINENO\"$(cat <<END)\nEND", 2, "2")]
    [InlineData("\"$LINENO\"$(cat <<END)\\\nEND", 4, "4")]
    [InlineData("missing $(cat <<END)\\\nword\nEND", 4, "missing")]
    public async Task BufferedHereDocumentPreservesTheCommandWordReaderLine(string script, int line, string command)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal("line 1: warning: command substitution: 1 unterminated here-document\n"
            + $"line {line}: {command}: command not found\n", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("probe[0")]
    [InlineData("probe\\[0")]
    [InlineData("\"probe[0\"")]
    public async Task BracketInAnOrdinaryArgumentRemainsALiteralWord(string argument)
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("printf '<%s>' " + argument + "\nprintf after\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<probe[0>after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
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

    [Theory]
    [InlineData("missing\\", "line 4: missing: command not found\n")]
    [InlineData("\"$LINENO\" \\", "line 3: 3: command not found\n")]
    [InlineData("missing earlier \\", "line 2: missing: command not found\n")]
    public async Task FinalFileBackslashPreservesCommandWordLookahead(string command, string diagnostic)
    {
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var script = ":\n" + command;
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(127, status);
            Assert.Empty(host.StandardOutput.TextContent);
            Assert.Equal(diagnostic, host.StandardError.TextContent);
        }
    }

    [Theory]
    [InlineData(true, "input: line 3: 3: command not found\n")]
    [InlineData(false, "line 4: 4: command not found\n")]
    public async Task FinalStringBackslashKeepsTheFirstArgumentReaderLine(bool source, string diagnostic)
    {
        await using var host = new TestHost();
        const string body = ":\n\"$LINENO\" \\";
        host.AddFile("/input", body);

        var result = await host.RunAsync(source ? "source input" : "eval '" + body + "'", CancellationToken.None);

        Assert.Equal(127, result.ExitCode);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
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
    [InlineData("do\n", "do", "do")]
    [InlineData(": | ]]\n", "]]", ": | ]]")]
    [InlineData("f (word)\n", "word", "f (word)")]
    [InlineData("f (\n)\n", "newline", "f (")]
    [InlineData("f (", "newline", "f (")]
    [InlineData("x=y f (word)\n", "(", "x=y f (word)")]
    [InlineData("n= ()\n", "(", "n= ()")]
    [InlineData("f (( 1 ))\n", "(", "f (( 1 ))")]
    [InlineData("f ((\n 1\n", "(", "f ((")]
    [InlineData("f ( (()) )\n", "", "f ( (()) )")]
    [InlineData("f ( ((\t'lit'\t)) )\n", "$'\\t\\'lit\\'\\t'", "f ( ((\t'lit'\t)) )")]
    [InlineData("f ( (( $( printf 2 ; printf 3 ) )) )\n", " $(printf 2; printf 3) ",
        "f ( (( $( printf 2 ; printf 3 ) )) )")]
    [InlineData("case (( 1 )) in x) :;; esac\n", "(", "case (( 1 )) in x) :;; esac")]
    [InlineData("{ :; } > (( 1 ))\n", "(", "{ :; } > (( 1 ))")]
    public async Task InvalidCompoundOrCommandReportsTheReachedTokenAndLine(string unit, string token, string line)
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
    public async Task MultilineArithmeticInAFunctionHeaderQuotesTheExpressionAndKeepsTheClosingLine()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nf ( ((\n value=2\n)) )\nprintf skipped\n");

        var result = await host.RunAsync("source input 2>report\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal("input: line 4: syntax error near unexpected token `$'\\n value=2\\n''\ninput: line 4: `)) )'\n",
            host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ArithmeticTokenAfterAnAssignmentReportsTheSecondOpener()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nvalue=x (\\\n\\\n( 1 ))\nprintf skipped\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal("input: line 4: syntax error near unexpected token `('\ninput: line 4: `( 1 ))'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("(\\\n( 1", ')', 3)]
    [InlineData("((\n 'literal", '\'', 3)]
    [InlineData("for (( i=0; i<2", ')', 2)]
    public async Task IncompleteArithmeticCommandReportsTheOpeningDelimiter(string unit, char delimiter, int line)
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\n" + unit);

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal($"input: line {line}: unexpected EOF while looking for matching `{delimiter}'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("value=(( 1")]
    [InlineData("value=( (( 1")]
    public async Task IncompleteArithmeticTokenInAnArrayInitializerReportsArrayFailure(string unit)
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\n" + unit);

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:1:after", host.StandardOutput.TextContent);
        Assert.Equal($"input: line 2: syntax error near unexpected token `('\ninput: line 2: `{unit}'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task IncompleteArithmeticCommandInASourcedCaptureReturnsToTheCaller()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nprintf '%s' \"$( (( 1");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal("input: line 2: unexpected EOF while looking for matching `)'\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("f (( $(if :) ))", 2, "near unexpected token `('", "f (( $(if :) ))")]
    [InlineData("((printf skipped)\n)", 2, "near `skipped)'", "((printf skipped)")]
    [InlineData("((printf skipped)\\\n )", 3, "near unexpected token `'", " )")]
    [InlineData("((printf skipped)\\\n\"two words\"; :", 3, "near unexpected token `\"two words\"'", "\"two words\"; :")]
    [InlineData("for ((i=0;i<1;i++); ); do :; done", 2, "near `;'", "for ((i=0;i<1;i++); ); do :; done")]
    public async Task ArithmeticReaderFailureKeepsTheCallerAndTheReachedPhysicalLine(
        string unit, int line, string diagnostic, string shown)
    {
        var memory = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        host.AddFile("/input", "printf before\n" + unit + "\nprintf skipped\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before:2:after", host.StandardOutput.TextContent);
        Assert.Equal($"input: line {line}: syntax error {diagnostic}\ninput: line {line}: `{shown}'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task RawScriptCanRunNestedSubshellsWithClosersOnSeparateLines()
    {
        await using var host = new TestHost();

        var result = await host.RunAsync("((printf body)\n)\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("body", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Fact]
    public async Task BlankBeforeLineBreakAllowsSourcedNestedSubshells()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "((printf body) \n)\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("body:0:after", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
    }

    [Fact]
    public async Task ContinuedArithmeticCloserInACaptureQuotesTheReachedToken()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nprintf '<%s>' \"$( ((printf skipped)\\\n ) )\"\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal("input: line 3: syntax error near unexpected token `$'\\\\\\n''\ninput: line 3: ` ) )\"'\n",
            host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task PartialArithmeticForHeaderInACaptureEndsTheLoadedReader()
    {
        await using var host = new TestHost();
        host.AddFile("/input", "printf before\nprintf '<%s>' \"$( for ((i=0;i<1;i++)\n); do printf skipped; done )\"\n");

        var result = await host.RunAsync("source input\nprintf ':%s:after' \"$?\"\n", CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("before", host.StandardOutput.TextContent);
        Assert.Equal("input: line 2: syntax error near `;i++)'\ninput: line 2: `printf '<%s>' \"$( for ((i=0;i<1;i++)'\n",
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

    private const string EvalFirstLine = "printf \"<first:%s>\" \"$LINENO\"";
    private const string EvalSecondLine = "printf \"<second:%s>\" \"$LINENO\"\n)\n";
    private const string EvalLocationLead = "\n\nprintf before\n";
    private const string EvalLocationTail = "\nprintf \"<reader:%s>\" \"$?\"\n";

    [Theory]
    [InlineData("eval", 7)]
    [InlineData("eval --", 4)]
    [InlineData("builtin eval", 4)]
    [InlineData("command -p -- eval", 4)]
    [InlineData("prefix=value \\\n  eval", 4)]
    [InlineData("prefix='one\ntwo' eval", 5)]
    public async Task EvalUsesTheParsedInvocationLineForCommandsAndSyntaxErrors(string invocation, int line)
    {
        var script = EvalLocationLead + $"{invocation} '{EvalFirstLine}\n{EvalSecondLine}'" + EvalLocationTail;
        foreach (var prepared in new[] { false, true })
        {
            await using var host = new TestHost();
            var status = prepared ? (await EndToEndHarness.RunScriptAsync(script, host)).ExitCode
                : (await host.RunAsync(script, CancellationToken.None)).ExitCode;

            Assert.Equal(0, status);
            Assert.Equal($"before<first:{line}><second:{line + 1}><reader:2>", host.StandardOutput.TextContent);
            Assert.Equal($"eval: line {line + 2}: syntax error near unexpected token `)'\neval: line {line + 2}: `)'\n",
                host.StandardError.TextContent);
            Assert.Equal(0, host.OpenFileCount);
        }
    }

    [Fact]
    public async Task EvalWithAContinuedSecondArgumentUsesTheFirstArgumentLookahead()
    {
        var script = EvalLocationLead + $"eval '{EvalFirstLine};' \\\n  '{EvalSecondLine}'" + EvalLocationTail;
        await using var host = new TestHost();

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before<first:4><second:4><reader:2>", host.StandardOutput.TextContent);
        Assert.Equal("eval: line 5: syntax error near unexpected token `)'\neval: line 5: `)'\n",
            host.StandardError.TextContent);
    }

    [Fact]
    public async Task EvalBeginningWithRedirectionUsesTheReachedSourceLine()
    {
        await using var host = new TestHost();
        host.AddFile("/caller.sh", EvalLocationLead + $"2>report eval '{EvalFirstLine}\n{EvalSecondLine}'" + EvalLocationTail);

        var result = await host.RunAsync("source ./caller.sh\nprintf \"<outer:%s>\" \"$?\"\n", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("before<first:4><second:5><reader:2><outer:0>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal("./caller.sh: eval: line 6: syntax error near unexpected token `)'\n"
            + "./caller.sh: eval: line 6: `)'\n", host.ReadFile("/report"));
        Assert.Equal(0, host.OpenFileCount);
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
        Assert.Equal("line 2: syntax error: arithmetic expression required\n"
            + "line 2: syntax error: `((i=7))'\n"
            + "line 2: unexpected EOF while looking for matching `\"'\n", host.StandardError.TextContent);
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
        Assert.Equal("line 1: syntax error: arithmetic expression required\n"
            + "line 1: syntax error: `((i=7))'\n"
            + "line 1: unexpected EOF while looking for matching `\"'\n", host.StandardError.TextContent);
        Assert.Equal("prefix", host.ReadFile("/diag"));
        Assert.Equal("diag", Assert.Single(host.CaptureEntries()).Key);
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("\"", "line 3: unexpected EOF while looking for matching `\"'\n")]
    [InlineData("\"\n: \"x\"", "line 4: unexpected EOF while looking for matching `\"'\n")]
    [InlineData("\"\n: \"", "")]
    [InlineData("\"\n: \"x`", "line 4: unexpected EOF while looking for matching ``'\n")]
    [InlineData("'", "line 3: unexpected EOF while looking for matching `''\n")]
    [InlineData("\n: \"", "")]
    public async Task QuotedArithmeticForRecoveryReportsTheUnclosedSuffixDelimiter(string suffix, string diagnostic)
    {
        var script = "value=\"$(for ((1;)); do\n: >body\ndone)" + suffix;
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("line 1: syntax error: arithmetic expression required\n"
            + "line 1: syntax error: `((1;))'\n" + diagnostic, host.StandardError.TextContent);
        Assert.Empty(host.StandardOutput.TextContent);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("set +e", 1, "earlier<1:1>", "line 4: syntax error: `((1;))'\n"
        + "line 4: unexpected EOF while looking for matching `\"'\n")]
    [InlineData("set -e", 2, "earlier<2:0>", "")]
    public async Task QuotedArrayHeaderFailureDiscardsTheUnitAndItsRedirections(
        string option, int status, string output, string extraDiagnostics)
    {
        var script = $"trap '{PrintParserStatus}' EXIT\n{option}\nprintf earlier\n"
            + "printf prefix >discarded; items=(\"$(for ((1;)); do :; done)\")\nprintf later\n";
        var governor = new MemoryGovernor(256 * 1024);
        await using var host = new TestHost { MemoryBudget = governor };

        Assert.False(Parser.TryParse(script, out _, out var error));
        Assert.Equal("Arithmetic for header requires two semicolons.", error?.Message);
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("line 4: syntax error: arithmetic expression required\n" + extraDiagnostics, host.StandardError.TextContent);
        Assert.Empty(host.CaptureEntries());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("items=(\"$(for ((1;)); do\n :\n done)\"\n tail\n )", 0, "earlier<1:1><0:0>",
        "./sample.sh: line 4: unexpected EOF while looking for matching `\"'\n")]
    [InlineData("items=(\"$(for ((1;)); do :; done)\n", 2, "earlier<2:2>", "")]
    [InlineData("items=(\"$(v=\"$(for ((1;)); do :; done)\"; printf child)\")", 2, "earlier<2:2>", "")]
    public async Task SourcedQuotedArrayHeaderFailureUsesTheRecoveredDelimiterAndStatus(
        string unit, int status, string output, string recovery)
    {
        await using var host = new TestHost();
        host.AddFile("/sample.sh", "printf earlier\n" + unit + "\nprintf later\n");
        var script = $"trap '{PrintParserStatus}' EXIT\nset +e\nsource ./sample.sh\n{PrintParserStatus}\n";

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal("./sample.sh: line 2: syntax error: arithmetic expression required\n"
            + "./sample.sh: line 2: syntax error: `((1;))'\n" + recovery, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("set -e", ")", "earlier<2:0>", "syntax error near unexpected token `)'\n")]
    [InlineData("set +e", ")", "earlier<2:2>", "syntax error near unexpected token `)'\n`)'\n")]
    [InlineData("set -e", "[[ ( ( -z ]]", "earlier<2:0>", "unexpected argument `]]' to conditional unary operator\n")]
    [InlineData("set -e", "printf prefix; value=\"$(for ((1;)); do :; done)\"", "earlier<2:0>",
        "line 4: syntax error: arithmetic expression required\n")]
    public async Task StreamedParserFailureUsesTheReachedErrexitState(
        string option, string unit, string output, string diagnostic)
    {
        await using var host = new TestHost();
        var script = $"trap '{PrintParserStatus}' EXIT\nprintf earlier\n{option}\n{unit}\n";

        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Equal(diagnostic, host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(true, "value=$(for ((i=7)); do : >body; break; done)", 2, "2:inner:source-arg", "arithmetic expression required")]
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
        Assert.Equal("unit: line 1: syntax error: arithmetic expression required\n"
            + "unit: line 1: syntax error: `((i=7))'\n", host.StandardError.TextContent);
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
        0, "[1:1:1][0:0:1]", "line 2: ")]
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
