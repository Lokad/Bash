using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class TrapCommandUnitTests
{
    [Theory]
    [InlineData("trap -p -- $'\\r+15\\t'", "trap -- 'sample' SIGTERM\n|0|trap -- 'sample' SIGTERM\n", "")]
    [InlineData("trap -p -- -0", "trap -- '' EXIT\n|0|trap -- 'sample' SIGTERM\n", "")]
    [InlineData("trap -- ' +15 '", "|0|", "")]
    [InlineData("trap -- - $'\\v15'", "|0|", "")]
    [InlineData("trap -- '+15' INT", "|0|trap -- 'sample' SIGTERM\ntrap -- '+15' SIGINT\n", "")]
    [InlineData("trap -p -- $'15\\n'", "|1|trap -- 'sample' SIGTERM\n", "trap: 15\n: invalid signal specification\n")]
    public async Task TrapNumericConditionsPreserveActionAndResetClassification(string command, string output, string error)
    {
        var script = "trap sample TERM; trap '' EXIT; " + command + "; printf '|%s|' \"$?\"; trap -p TERM INT";
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrapListingReleasesTheFirstRecordBeforeWritingTheSecond(bool manyQuotes)
    {
        const int Width = 4096;
        var action = new byte[Width];
        action.AsSpan().Fill(manyQuotes ? (byte)'\'' : (byte)'x');
        action[^1] = 0xff;
        var printable = new string((char)action[0], Width - 1).Replace("'", "'\\''", StringComparison.Ordinal);
        byte[] expected = [.. "trap -- '"u8, .. System.Text.Encoding.ASCII.GetBytes(printable), 0xff, .. "' SIGINT\n"u8];
        var state = new Eval(Parser.Parse("trap \"$1\" INT; trap -p INT INT"));
        state.ReplacePositionalParameters([ShellValue.FromBytes(action)]);
        var governor = new MemoryGovernor(512 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;

        var first = Assert.IsType<WriteOperation>(await state.GetNextStep());
        var beforeFirst = (state.Memory.Workspace.Bytes, governor.ReservedBytes);
        var firstContent = first.Content.ToArray();
        first.SetResult(DescriptorWriteResult.Open);
        var second = Assert.IsType<WriteOperation>(await state.GetNextStep());
        var beforeSecond = (state.Memory.Workspace.Bytes, governor.ReservedBytes);
        var secondContent = second.Content.ToArray();
        second.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(FileDescriptor.StdOut, first.FileDescriptor);
        Assert.Equal(FileDescriptor.StdOut, second.FileDescriptor);
        Assert.Equal(expected, firstContent);
        Assert.Equal(expected, secondContent);
        Assert.Equal(beforeFirst, beforeSecond);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    // GNU Bash 5.2.21 / 5.2.37, C locale, pipefail. Golden bytes and complete
    // file effects are independent of managed parsing and diagnostic wording.
    public static IEnumerable<object[]> Actions()
    {
        var cases = new object[][]
        {
            ["root-separate", "trap 'printf first\n)\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-same-unit", "trap 'printf wrong; )\n' EXIT; exit 7", 7, "", true, true, false, false, ""],
            ["root-semicolon-newline", "trap 'printf first;\n)\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-second-unit", "trap 'printf A\nprintf wrong; )\n' EXIT; exit 7", 7, "41", true, true, false, false, ""],
            ["root-assignment", "trap 'value=changed\nprintf '\\''%s'\\'' \"$value\"\n)\n' EXIT; exit 7", 7, "6368616E676564", true, true, false, false, ""],
            ["root-file", "trap 'printf first >out\n)\n' EXIT; exit 7", 7, "", true, true, false, true, "6669727374"],
            ["root-same-unit-file", "trap 'printf wrong >out; )\n' EXIT; exit 7", 7, "", true, true, false, false, ""],
            ["root-descriptor", "trap 'exec 3>out\nprintf first >&3\n)\n' EXIT; exit 7", 7, "", true, true, false, true, "6669727374"],
            ["root-unfinished", "trap 'printf first\nif true; then printf wrong\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-invalid-function", "trap 'printf first\nf(){ printf wrong; )\n}\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-quoted-newline", "trap 'printf '\\''%s'\\'' '\\''first\nsecond'\\''\n)\n' EXIT; exit 7", 7, "66697273740A7365636F6E64", true, true, false, false, ""],
            ["root-here-document", "trap 'cat <<EOF\nbody ) '\\''\nEOF\n)\n' EXIT; exit 7", 7, "626F6479202920270A", true, true, false, false, ""],
            ["root-same-unit-here-document", "trap 'cat <<EOF; )\nbody\nEOF\n' EXIT; exit 7", 7, "", true, true, false, false, ""],
            ["root-early-exit", "trap 'printf first\nexit 9\n)\n' EXIT; exit 7", 9, "6669727374", false, false, false, false, ""],
            ["root-same-unit-exit", "trap 'exit 9; )\n' EXIT; exit 7", 7, "", true, true, false, false, ""],
            ["root-default-exit", "trap 'printf first\nexit\n)\n' EXIT; exit 7", 7, "6669727374", false, false, false, false, ""],
            ["root-return", "trap 'printf first\nreturn 9\n)\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-default-return", "trap 'printf first\nreturn\n)\n' EXIT; exit 7", 7, "6669727374", true, true, false, false, ""],
            ["root-arithmetic-expansion", "trap 'printf first\nx=$((1/0))\nprintf wrong\n)\n' EXIT; exit 7", 7, "666972737477726F6E67", true, true, false, false, ""],
            ["root-readonly-assignment", "trap 'readonly x=old\nprintf first\nx=new\nprintf wrong\n)\n' EXIT; exit 7", 7, "666972737477726F6E67", true, true, false, false, ""],
            ["root-parameter-failure", "trap 'printf first\n: ${missing?inner}\nprintf wrong\n)\n' EXIT; exit 7", 7, "6669727374", false, true, false, false, ""],
            ["root-valid-last-failure", "trap 'printf first\nfalse\n' EXIT; exit 7", 7, "6669727374", false, false, false, false, ""],
            ["root-valid-last-success", "trap 'printf first\ntrue\n' EXIT; exit 7", 7, "6669727374", false, false, false, false, ""],
            ["root-blank", "trap '# synthetic\n\n' EXIT; exit 7", 7, "", false, false, false, false, ""],
            ["root-replace-handler", "trap 'trap '\\''printf wrong'\\'' EXIT\nprintf once\nexit 9\n)\n' EXIT; exit 7", 9, "6F6E6365", false, false, false, false, ""],
            ["root-reset-handler", "trap 'trap - EXIT\nprintf once\nexit 9\n)\n' EXIT; exit 7", 9, "6F6E6365", false, false, false, false, ""],
            ["root-pipeline", "trap 'printf A | cat\n)\n' EXIT; exit 7", 7, "41", true, true, false, false, ""],
            ["root-compound", "trap '{\nprintf A\nprintf B\n}\n)\n' EXIT; exit 7", 7, "4142", true, true, false, false, ""],
            ["root-byte-output", "trap 'printf '\\''%s'\\'' $'\\''\\377\\r'\\''\n)\n' EXIT; exit 7", 7, "FF0D", true, true, false, false, ""],
            ["root-early-errexit", "set -e; trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; exit 7", 1, "6669727374", false, false, false, false, ""],
            ["function-early-errexit", "set -e; x=outer; set -- caller; trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; f(){ local x=inner; exit 7; }; f callee", 1, "6669727374", false, false, false, false, ""],
            ["source-early-errexit", "set -e; x=outer; set -- caller; trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; f(){ local x=inner; source ./unit callee; }; f function", 1, "6669727374", false, false, true, false, ""],
            ["subshell-early-errexit", "set -e; (trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; exit 7); printf '[after:%s]' \"$?\"", 1, "6669727374", false, false, false, false, ""],
            ["capture-early-errexit", "set -e; value=$(trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; exit 7); printf '[after:%s:%s]' \"$?\" \"$value\"", 7, "", true, true, false, false, ""],
            ["pipeline-early-errexit", "set -e; : | { trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; exit 7; }; printf '[after:%s]' \"$?\"", 1, "6669727374", false, false, false, false, ""],
            ["function-loop-early-errexit", "set -e; trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; f(){ for i in one; do exit 7; done; }; f", 1, "6669727374", false, false, false, false, ""],
            ["function-late-syntax", "x=outer; set -- caller; trap 'printf first\n)\n' EXIT; f(){ local x=inner; exit 7; }; f callee", 7, "6669727374", true, true, false, false, ""],
            ["function-early-exit", "x=outer; set -- caller; trap 'printf first\nexit 9\n)\n' EXIT; f(){ local x=inner; exit 7; }; f callee", 9, "6669727374", false, false, false, false, ""],
            ["source-late-syntax", "x=outer; set -- caller; trap 'printf first\n)\n' EXIT; f(){ local x=inner; source ./unit callee; }; f function", 7, "6669727374", true, true, true, false, ""],
            ["source-early-exit", "x=outer; set -- caller; trap 'printf first\nexit 9\n)\n' EXIT; f(){ local x=inner; source ./unit callee; }; f function", 9, "6669727374", false, false, true, false, ""],
            ["subshell-late-syntax", "(trap 'printf first\n)\n' EXIT; exit 7); printf '[after:%s]' \"$?\"", 0, "66697273745B61667465723A375D", true, true, false, false, ""],
            ["subshell-early-exit", "(trap 'printf first\nexit 9\n)\n' EXIT; exit 7); printf '[after:%s]' \"$?\"", 0, "66697273745B61667465723A395D", false, false, false, false, ""],
            ["capture-late-syntax", "value=$(trap 'printf first\n)\n' EXIT; exit 7); printf '[after:%s:%s]' \"$?\" \"$value\"", 0, "5B61667465723A373A66697273745D", true, true, false, false, ""],
            ["capture-early-exit", "value=$(trap 'printf first\nexit 9\n)\n' EXIT; exit 7); printf '[after:%s:%s]' \"$?\" \"$value\"", 0, "5B61667465723A393A66697273745D", false, false, false, false, ""],
            ["pipeline-late-syntax", ": | { trap 'printf first\n)\n' EXIT; exit 7; }; printf '[after:%s]' \"$?\"", 0, "66697273745B61667465723A375D", true, true, false, false, ""],
            ["pipeline-early-exit", ": | { trap 'printf first\nexit 9\n)\n' EXIT; exit 7; }; printf '[after:%s]' \"$?\"", 0, "66697273745B61667465723A395D", false, false, false, false, ""],
            ["falloff-late-syntax", "trap 'printf first\n)\n' EXIT; false", 1, "6669727374", true, true, false, false, ""],
            ["falloff-early-exit", "trap 'printf first\nexit 9\n)\n' EXIT; false", 9, "6669727374", false, false, false, false, ""],
            ["function-return", "x=outer; set -- caller; trap 'printf '\\''[%s:%s:%s]'\\'' \"$?\" \"$x\" \"$1\"\nreturn 9\n)\n' EXIT; f(){ local x=inner; exit 7; }; f callee", 9, "5B373A696E6E65723A63616C6C65655D", false, false, false, false, ""],
            ["function-default-return", "x=outer; set -- caller; trap 'printf '\\''[%s:%s:%s]'\\'' \"$?\" \"$x\" \"$1\"\nreturn\n)\n' EXIT; f(){ local x=inner; exit 7; }; f callee", 7, "5B373A696E6E65723A63616C6C65655D", false, false, false, false, ""],
            ["source-return", "x=outer; set -- caller; trap 'printf '\\''[%s:%s:%s]'\\'' \"$?\" \"$x\" \"$1\"\nreturn 9\n)\n' EXIT; f(){ local x=inner; source ./unit callee; }; f function", 9, "5B373A696E6E65723A63616C6C65655D", false, false, true, false, ""],
            ["source-default-return", "x=outer; set -- caller; trap 'printf '\\''[%s:%s:%s]'\\'' \"$?\" \"$x\" \"$1\"\nreturn\n)\n' EXIT; f(){ local x=inner; source ./unit callee; }; f function", 7, "5B373A696E6E65723A63616C6C65655D", false, false, true, false, ""],
            ["root-break", "trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; exit 7", 7, "666972737477726F6E67", true, true, false, false, ""],
            ["function-loop-break", "trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; f(){ for i in one; do exit 7; done; }; f", 7, "6669727374", true, true, false, false, ""],
            ["loop-break", "for i in one; do trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; exit 7; done", 7, "6669727374", true, true, false, false, ""],
            ["root-continue", "trap 'printf first\ncontinue\nprintf wrong\n)\n' EXIT; exit 7", 7, "666972737477726F6E67", true, true, false, false, ""],
            ["function-loop-continue", "trap 'printf first\ncontinue\nprintf wrong\n)\n' EXIT; f(){ for i in one; do exit 7; done; }; f", 7, "6669727374", true, true, false, false, ""],
            ["loop-continue", "for i in one; do trap 'printf first\ncontinue\nprintf wrong\n)\n' EXIT; exit 7; done", 7, "6669727374", true, true, false, false, ""],
            ["errexit-tested", "set -e; trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; f(){ exit 7; }; f || :", 1, "6669727374", false, false, false, false, ""],
            ["errexit-enabled-in-action", "set -e; trap 'set -e\nprintf first\nfalse\n)\n' EXIT; f(){ exit 7; }; f || :", 1, "6669727374", false, false, false, false, ""],
            ["parameter-before-handler", "trap 'printf first\n)\n' EXIT; : \"${missing?root}\"", 1, "6669727374", true, true, false, false, ""],
            ["parameter-before-handler-exit", "trap 'printf first\nexit 9\n)\n' EXIT; : \"${missing?root}\"", 9, "6669727374", false, true, false, false, ""],
            ["loop-tail-break", "for i in one; do trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; exit 7; done; printf after", 7, "6669727374", true, true, false, false, ""],
            ["loop-tail-continue", "for i in one; do trap 'printf first\ncontinue\nprintf wrong\n)\n' EXIT; exit 7; done; printf after", 7, "6669727374", true, true, false, false, ""],
            ["recovery-arithmetic-e", "set -e; trap 'printf first\nx=$((1/0))\nprintf wrong\n)\n' EXIT; exit 7", 2, "666972737477726F6E67", true, true, false, false, ""],
            ["recovery-readonly-e", "set -e; trap 'readonly x=old\nprintf first\nx=new\nprintf wrong\n)\n' EXIT; exit 7", 1, "6669727374", false, true, false, false, ""],
            ["recovery-nameref", "trap 'declare -n r\nprintf first\n: ${r:=inner}\nprintf wrong\n)\n' EXIT; exit 7", 7, "666972737477726F6E67", true, true, false, false, ""],
            ["recovery-nameref-e", "set -e; trap 'declare -n r\nprintf first\n: ${r:=inner}\nprintf wrong\n)\n' EXIT; exit 7", 2, "666972737477726F6E67", true, true, false, false, ""],
            ["root-exit-redirection", "trap 'printf first\n)\n' EXIT; exit 7 >out", 7, "", true, true, false, true, "6669727374"],
            ["root-source-exit-frame", "set -- caller; trap 'printf '\\''[%s]'\\'' \"$1\"\n)\n' EXIT; source ./unit callee", 7, "5B63616C6C65655D", true, true, true, false, ""],
            ["root-loop-numeric-exit", "for i in one; do trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; exit bad; done; printf after", 2, "6669727374", true, true, false, false, ""],
            ["captured-output-errexit", "set -e; trap 'printf \"[outer:%s]\" \"$value\"' EXIT; value=$(trap 'printf first\nfalse\nprintf wrong\n)\n' EXIT; exit 7); printf wrong", 7, "5B6F757465723A666972737477726F6E675D", true, true, false, false, ""],
            ["policy-syntax-e", "set -e; trap 'printf first\n)\n' EXIT; exit 7", 2, "6669727374", true, true, false, false, ""],
            ["policy-arithmetic-valid", "trap 'printf first\nx=$((1/0))\nprintf after\n' EXIT; exit 7", 7, "66697273746166746572", false, true, false, false, ""],
            ["policy-arithmetic-valid-e", "set -e; trap 'printf first\nx=$((1/0))\nprintf after\n' EXIT; exit 7", 7, "66697273746166746572", false, true, false, false, ""],
            ["policy-readonly-valid", "trap 'readonly x=old\nprintf first\nx=new\nprintf after\n' EXIT; exit 7", 7, "66697273746166746572", false, true, false, false, ""],
            ["policy-readonly-valid-e", "set -e; trap 'readonly x=old\nprintf first\nx=new\nprintf after\n' EXIT; exit 7", 1, "6669727374", false, true, false, false, ""],
            ["policy-parameter-e", "set -e; trap 'printf first\n: ${missing?inner}\nprintf wrong\n)\n' EXIT; exit 7", 1, "6669727374", false, true, false, false, ""],
            ["policy-tested-assignment", "trap 'readonly x=old\nprintf first\nif x=new; then printf wrong; fi\nprintf after\n)\n' EXIT; exit 7", 7, "66697273746166746572", true, true, false, false, ""],
            ["policy-tested-assignment-e", "set -e; trap 'readonly x=old\nprintf first\nif x=new; then printf wrong; fi\nprintf after\n)\n' EXIT; exit 7", 1, "6669727374", false, true, false, false, ""],
            ["policy-break-e", "set -e; trap 'printf first\nbreak\nprintf wrong\n)\n' EXIT; f(){ for i in one; do exit 7; done; }; f", 2, "6669727374", true, true, false, false, ""],
            ["policy-continue-e", "set -e; trap 'printf first\ncontinue\nprintf wrong\n)\n' EXIT; f(){ for i in one; do exit 7; done; }; f", 2, "6669727374", true, true, false, false, ""],
        };
        foreach (var prepared in new[] { false, true })
        foreach (var sample in cases)
            yield return [prepared, ..sample];
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ExitActionsPreserveCommandUnitEffectsAndControl(
        bool prepared, string name, string script, int status, string outputHex,
        bool syntaxError, bool diagnostic, bool sourceFile, bool outputFile, string fileHex)
    {
        var memory = new MemoryGovernor(1024 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        if (sourceFile) host.AddFile("/unit", "exit 7\n");
        using var owner = memory.CreateScope();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, EvaluationOptions.Default, cancel.Token);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(Convert.FromHexString(outputHex), host.StandardOutput.Content.ToArray());
        Assert.Equal(diagnostic, !host.StandardError.Content.IsEmpty);
        Assert.Equal(syntaxError, host.StandardError.TextContent.Contains("syntax error", StringComparison.Ordinal));
        var expected = new SortedDictionary<string, (FilePathKind Kind, string ContentHex)>(StringComparer.Ordinal);
        if (sourceFile) expected.Add("unit", (FilePathKind.File, "6578697420370A"));
        if (outputFile) expected.Add("out", (FilePathKind.File, fileHex));
        Assert.Equal(expected.ToArray(), host.CaptureEntries().ToArray());
        Assert.True(host.OpenFileCount == 0, name + ": descriptors must close");
        owner.Dispose();
        Assert.Equal(0, memory.ReservedBytes);
    }
}
