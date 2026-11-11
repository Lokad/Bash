using Lokad.Bash.Tests.EndToEnd;
using Lokad.Bash.Files;
using Lokad.Bash.Tree;
using Lokad.Bash.Memory;
using System;
using Lokad.Bash.Syntax;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationRedirectionTestsEndToEnd
{

    // Git Bash 5.2.37, isolated environment: bodies follow the physical line, not each command.
    [Theory]
    [InlineData("cat <<ONE | cat\nfirst\nONE\nprintf after", "first\nafter", 0)]
    [InlineData("cat <<ONE | cat <<'TWO'\nfirst\nONE\nsecond\nTWO\nprintf after", "second\nafter", 0)]
    [InlineData("cat <<ONE && cat <<TWO\nfirst\nONE\nsecond\nTWO\nprintf after", "first\nsecond\nafter", 0)]
    [InlineData("false <<ONE || cat <<TWO\nfirst\nONE\nsecond\nTWO\nprintf after", "second\nafter", 0)]
    [InlineData("cat <<ONE; cat <<TWO\nfirst\nONE\nsecond\nTWO\nprintf after", "first\nsecond\nafter", 0)]
    [InlineData("cat <<ONE <<TWO\nfirst\nONE\nsecond\nTWO\nprintf after", "second\nafter", 0)]
    [InlineData("{ cat; } <<END | cat\nbody\nEND\nprintf after", "body\nafter", 0)]
    [InlineData("(cat) <<END; printf after\nbody\nEND\n", "body\nafter", 0)]
    [InlineData("f(){ cat; } <<END; f\nbody\nEND\n", "body\n", 0)]
    [InlineData("f(){ cat <<END; }; f\nbody\nEND\n", "body\n", 0)]
    [InlineData("if cat <<END; then printf yes; fi\nbody\nEND\n", "body\nyes", 0)]
    [InlineData("if false; then :; elif cat <<END; then printf yes; else :; fi\nbody\nEND\n", "body\nyes", 0)]
    [InlineData("if false; then :; else cat <<END; fi\nbody\nEND\n", "body\n", 0)]
    [InlineData("while false <<END; do :; done; printf after\nbody\nEND\n", "after", 0)]
    [InlineData("until true <<END; do :; done; printf after\nbody\nEND\n", "after", 0)]
    [InlineData("for x in a b; do cat <<END; done\n$x\nEND\n", "a\nb\n", 0)]
    [InlineData("for ((i=0;i<2;i++)); do cat <<END; done\n$i\nEND\n", "0\n1\n", 0)]
    [InlineData("case a in a) cat <<END;; *) :;; esac\nbody\nEND\n", "body\n", 0)]
    [InlineData("! cat <<END; printf 'status=%s\\n' \"$?\"\nbody\nEND\n", "body\nstatus=1\n", 0)]
    [InlineData("x=$(cat <<ONE | cat <<TWO\nfirst\nONE\nsecond\nTWO\n); printf '<%s>\\n' \"$x\"", "<second>\n", 0)]
    [InlineData("cat <<ONE |\nfirst\nONE\ncat <<'TWO'\nsecond\nTWO\n", "second\n", 0)]
    [InlineData("cat <<-ONE | cat <<-'TWO'\n\tfirst\n\tONE\n\tsecond\n\tTWO\n", "second\n", 0)]
    public async Task Evaluation_HereDocuments_FollowLexicalOrderAcrossCommandBoundaries(
        string script, string expectedOutput, int expectedStatus)
    {
        var result = await EndToEndHarness.RunScriptAsync(script,
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedOutput, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        var definition = "outer(){ " + script + (script.EndsWith('\n') ? "" : ";") + "\n}";
        var function = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse(definition).Commands));
        using var output = new ByteMemoryBuilder(0, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppend(function, output));
        var replay = await EndToEndHarness.RunScriptAsync(Utf8Text.Decode(output.ToMemory()) + "outer",
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));
        Assert.Equal(expectedStatus, replay.ExitCode);
        Assert.Equal(expectedOutput, replay.StdOut);
        Assert.Equal(string.Empty, replay.StdErr);

        using var exported = new BoundedByteMemoryBuilder(0, 8192, new MemoryGovernor(TestMemoryBudget.FunctionFormattingBytes));
        Assert.True(FunctionDefinitionFormatter.TryAppendExport(function, exported));
        var child = await EndToEndHarness.RunScriptAsync(Utf8Text.Decode(exported.ToMemory()) + "\nouter",
            EndToEndHarness.BuildTree(Array.Empty<(string Path, string Content)>()));
        Assert.Equal(expectedStatus, child.ExitCode);
        Assert.Equal(expectedOutput, child.StdOut);
        Assert.Empty(child.StdErr);
    }


    [Fact]
    public async Task Evaluation_Redirection_HereDocumentPreservesCarriageReturn()
    {
        var script = "cat <<EOF\nvalue\rcontent\nEOF\n";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\rcontent\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OpensOutputFileForEcho()
    {
        // This test verifies that output redirection writes to a file and silences STDOUT.
        // The script "echo hi > /out" must produce no STDOUT/STDERR, exit 0, and write "hi\n" to /out.
        // If redirection fails, STDOUT would contain "hi\n" or the file would be missing.
        var script = @"echo hi > /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hi\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_OrdersDuplicateBeforeOpen()
    {
        // This test verifies that descriptor duplication happens before file redirection.
        // The group writes "out" to STDOUT and "err" to STDERR; with "2>&1 > /out", STDERR should
        // still go to the original STDOUT, while STDOUT is redirected to /out.
        // If the order is wrong, /out would contain "err\n" or STDOUT would be empty.
        var script = @"{ echo out; echo err >&2; } 2>&1 > /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("err\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_AssignsStdInAndStdOut()
    {
        // This test verifies that input and output redirections apply to a command.
        // The script "cat < /input > /out" must copy /input to /out, emit no STDOUT/STDERR, and exit 0.
        // If either redirection is ignored, STDOUT would contain input or /out would be empty.
        var script = @"cat < /input > /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("input\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_DevNullDiscardsOutputWithoutBackingFile()
    {
        var result = await EndToEndHarness.RunScriptAsync("echo hi > /dev/null", EndToEndHarness.BuildTree([]));

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OutputAndError()
    {
        // This test verifies that "&>" redirects both STDOUT and STDERR to the same file.
        // The group emits "out" on STDOUT and "err" on STDERR; /out must contain both lines, while
        // STDOUT/STDERR are empty and exit code is 0.
        // If only STDOUT is redirected, STDERR would contain "err\n".
        var script = @"{ echo out; echo err >&2; } &> /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\nerr\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicateOutputNonNumericRedirectsBoth()
    {
        // This test verifies that ">&" duplicates STDOUT and STDERR to the target file.
        // The group emits "out" and "err"; /out must contain both lines, while STDOUT/STDERR are empty.
        // If only STDOUT is redirected, STDERR would be non-empty.
        var script = @"{ echo out; echo err >&2; } >& /out";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out\nerr\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicatesInputDescriptor()
    {
        // This test verifies that "<&" duplicates an input descriptor.
        // The script opens fd 3 for /input and reads from it using cat; STDOUT must be "input\n".
        // If duplication fails, STDOUT would be empty.
        var script = @"exec 3< /input; cat <&3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("input\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_DuplicatesNonStandardInputDescriptor()
    {
        // This test verifies that non-standard descriptors can duplicate STDIN.
        // The script duplicates fd 0 into fd 5, then reads from fd 5; STDOUT must be "value\n".
        // If duplication fails, STDOUT would be empty or missing the input.
        var script = @"exec 5<&0; cat <&5";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_RejectsClosedOutputDescriptor()
    {
        var script = "exec 3>&1\nexec 3>&-\necho hi >&3\necho \"status:$?\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("3: Bad file descriptor\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_RejectsClosedInputDescriptor()
    {
        var script = "exec 3< /dev/null\nexec 3<&-\ncat <&3\necho \"status:$?\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("3: Bad file descriptor\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_StopsAfterClosedDescriptorFailure()
    {
        var script = "echo old > /out\nexec 3>&-\necho ignored >&3 > /out\necho \"status:$?\"";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("3: Bad file descriptor\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("old\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_RedirectsClosedDescriptorError()
    {
        var script = "exec 3>&-\n: 2> /errors >&3\necho \"status:$?\"\necho after >&2";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("status:1\n", result.StdOut);
        Assert.Equal("after\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("3: Bad file descriptor\n", await EndToEndHarness.ReadFileAsync(result.Host, "/errors"));
    }

    [Fact]
    public async Task Evaluation_Redirection_ReopensClosedDescriptor()
    {
        var script = "exec 3>&1\nexec 3>&-\nexec 3>&2\necho hi >&3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal("hi\n", result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_CloseIsScopedToCommand()
    {
        var script = "exec 3>&1\n: 3>&-\necho hi >&3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereStringCreatesPipe()
    {
        // This test verifies that here-strings feed the command via STDIN.
        // The script "cat <<< value" must output "value\n" with no STDERR and exit code 0.
        // If the here-string is ignored, STDOUT would be empty.
        var script = @"cat <<< value";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("value\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentCreatesPipe()
    {
        // This test verifies that here-documents feed multi-line input to a command.
        // The script uses a here-doc with two lines; STDOUT must contain both lines and exit 0.
        // If the here-doc is not connected, STDOUT would be empty.
        var script = @"cat <<EOF
line1
line2
EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("line1\nline2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentStripsTabs()
    {
        // This test verifies that "<<-" strips leading tabs in here-doc lines.
        // The script provides tab-indented lines; STDOUT must contain them without leading tabs.
        // If tabs are not stripped, STDOUT would include leading tab characters.
        var script = @"cat <<-EOF
	line1
		line2
	EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("line1\nline2\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentExpandsWhenUnquoted()
    {
        // This test verifies that unquoted here-documents perform variable expansion.
        // The script sets FOO=bar and uses an unquoted here-doc containing $FOO; STDOUT must be "bar\n".
        // If expansion is skipped, STDOUT would contain "$FOO\n".
        var script = @"FOO=bar; cat <<EOF
$FOO
EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("bar\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentExpandsLegacyBacktickCommandSubstitution()
    {
        var script = @"cat <<EOF
`printf hi`
EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentEscapedLegacyBackticksAreLiteral()
    {
        var script = @"cat <<EOF
\`printf hi\`
EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("`printf hi`\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentSkipsExpansionWhenQuoted()
    {
        // This test verifies that quoted here-documents do not perform variable expansion.
        // The script sets FOO=bar and uses a quoted here-doc containing $FOO; STDOUT must be "$FOO\n".
        // If expansion runs, STDOUT would be "bar\n".
        var script = @"FOO=bar; cat <<'EOF'
$FOO
EOF
".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("$FOO\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_HereDocumentWrittenToFileCanBeReadByNextCommand()
    {
        // This test verifies that a command using a here-doc and output redirection can be
        // followed by another command that reads the file that was just written.
        var script = @"cat > /out << 'EOF'
hi
EOF
cat /out".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("hi\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hi\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_AllocatesOutputDescriptor()
    {
        // This test verifies that {fd}> allocates a new descriptor and stores it in a variable.
        // The script opens /out on a new fd, writes "data" through that fd, then echoes the fd value.
        // STDOUT must contain the numeric fd, STDERR empty, exit code 0, and /out must contain "data\n".
        // If allocation fails, the fd value would be empty or non-numeric and /out would be missing.
        var script = @": {fd}> /out; echo data >&$fd; echo $fd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Matches("^\\d+\\n$", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("data\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_OpenAndClosedFilesAreNonterminal()
    {
        var script = @"exec 4>/out
if test -t 4; then echo open; else echo closed; fi
exec 4>&-
if test -t 4; then echo open; else echo closed; fi".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("closed\nclosed\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_ClosingDescriptorInSubshellDoesNotCloseParentDescriptor()
    {
        var script = @"exec 3>/out; (exec 3>&-); echo kept >&3";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("kept\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_CloseKeepsDescriptorReferencedByAlias()
    {
        var script = @"exec 3>/out; exec 4>&3 3>&-; echo kept >&4";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("kept\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_CloseKeepsImplicitAllocatedDescriptorAlias()
    {
        var script = @": {fd}>/out; exec 4>&$fd 4>&-; echo kept >&$fd";
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal(string.Empty, result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("kept\n", await EndToEndHarness.ReadFileAsync(result.Host, "/out"));
    }

    [Fact]
    public async Task Evaluation_Redirection_NestedExecPreservesUnrelatedDescriptor()
    {
        var script = @"{ exec 3>/out; } >/group
echo kept >&3
exec 3>&-
cat /out
cat /group".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("kept\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_OuterScopeRestoresDescriptorChangedByNestedExec()
    {
        var script = @"{ exec 1>/out; echo inner; } >/group
echo outer
cat /out
cat /group".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("outer\ninner\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("test -t 9")]
    [InlineData("[[ -t 9 ]]")]
    public async Task Evaluation_Redirection_TerminalTestRejectsMappedNonterminalDescriptor(string predicate)
    {
        var script = $@"exec 9> /out
{predicate}
echo open:$?
exec 9>&-
{predicate}
echo closed:$?".Replace("\r\n", "\n", StringComparison.Ordinal);
        var result = await EvaluationTestHelper.RunAsync(script);

        Assert.Equal("open:1\nclosed:1\n", result.StdOut);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_AllocatesInputDescriptor()
    {
        // This test verifies that {fd}< allocates an input descriptor and stores it in a variable.
        // The script opens /input on a new fd, reads from it, then echoes the fd value.
        // STDOUT must start with "input" and end with a numeric fd on the next line.
        // If allocation fails, the output would be missing the input or fd number.
        var script = @": {fd}< /input; cat <&$fd; echo $fd";
        var result = await EvaluationTestHelper.RunAsync(script);

        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("input", lines[0]);
        Assert.Matches("^\\d+$", lines[1]);
        Assert.Equal(string.Empty, result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }
}
