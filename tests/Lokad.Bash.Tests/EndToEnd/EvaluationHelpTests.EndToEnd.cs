using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationHelpTestsEndToEnd
{
    private const string TypeDocumentation = "type: type [-aftpP] [--] [name ...]\n"
        + "    Describe how names resolve. -a lists all matching shell definitions and the host result."
        + " -t prints the kind. -p prints the path of an external command."
        + " -P forces host lookup. -f skips shell functions.\n";

    [Theory]
    [InlineData("type --help")]
    [InlineData("command type -ftP --help -z")]
    public async Task TypeHelpPrintsTheSynopsisAtTheReachedOption(string source)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.Equal(TypeDocumentation, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData(">documentation")]
    [InlineData("1>&-")]
    public async Task TypeHelpPreservesOperandEffectsAndTheSelectedOutput(string redirection)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"""
            builtin type $omitted --help "$(printf reached >effect; printf missing)" {redirection}
            printf '<%s:%s>' "$?" "$_"
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<2:missing>", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.Equal("reached", host.ReadFile("/effect"));
        if (redirection == ">documentation")
            Assert.Equal(TypeDocumentation, host.ReadFile("/documentation"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("help true", "true: true\n    Return success.\n")]
    [InlineData("help -d true tru", "true - Return success.\ntrue - Return success.\n")]
    [InlineData("help -d read", "read - Read one record. With no destination, assign the whole record to REPLY.\n")]
    [InlineData("help -s '['", "[: [ expression ]\n")]
    [InlineData("help -s 'tr?e'", "true: true\n")]
    [InlineData("help -s typ type", "type: type [-aftpP] [--] [name ...]\n"
        + "typeset: typeset [-aAcfFgiIlnprux] [name[=value] ...]\ntype: type [-aftpP] [--] [name ...]\n")]
    [InlineData("help -s 't\\ype' 'tr[ua]e' '['", "type: type [-aftpP] [--] [name ...]\ntrue: true\n[: [ expression ]\n")]
    [InlineData("help -s apply_patch true", "true: true\n")]
    [InlineData("help -m true", "NAME\n    true - Return success.\n\nSYNOPSIS\n    true\n\nDESCRIPTION\n    Return success.\n\n")]
    [InlineData("help -m '['", "NAME\n    [ - Evaluate string, integer and file predicates; return 0 for true, 1 for false, or 2 for invalid syntax.\n\n"
        + "SYNOPSIS\n    [ expression ]\n\nDESCRIPTION\n    Evaluate string, integer and file predicates; return 0 for true, 1 for false, or 2 for invalid syntax.\n\n")]
    public async Task HelpKeepsTopicOrderAndExactRecordBytes(string source, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelpReportsOnlyTheLastTopicWhenEveryTopicMisses(bool merged)
    {
        const string diagnostic =
            "help: no help topics match `last missing'.  Try `help help' or `man -k last missing' or `info last missing'.\n";
        var result = await EvaluationTestHelper.RunAsync("help -s first_missing 'last missing'" + (merged ? " 2>&1" : ""));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(merged ? diagnostic : "", result.StdOut);
        Assert.Equal(merged ? "" : diagnostic, result.StdErr);
    }

    [Fact]
    public async Task HelpWithAnyMatchingTopicKeepsTheSuccessfulRecords()
    {
        var result = await EvaluationTestHelper.RunAsync("help -s first_missing true last_missing false");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("true: true\nfalse: false\n", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task HelpPreservesTheBytesOfAnUnmatchedTopic()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("help -s $'\\377'", CancellationToken.None);
        byte[] expected = [.. "help: no help topics match `"u8, 0xff,
            .. "'.  Try `help help' or `man -k "u8, 0xff, .. "' or `info "u8, 0xff, .. "'.\n"u8];

        Assert.Equal(1, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal(expected, host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task HelpPreservesTheByteOfAnInvalidOption()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("help $'-s\\376' --help", CancellationToken.None);
        byte[] expected = [.. "help: -"u8, 0xfe, .. ": invalid option\nhelp: usage: help [-dms] [pattern ...]\n"u8];

        Assert.Equal(2, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal(expected, host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task SourcedHelpMissKeepsTopicBytesAndIncludesTheSourceLine()
    {
        await using var host = new TestHost();
        host.AddFile("/body", "# Leading line\nhelp first_missing $'\\377'\n");
        var result = await host.RunAsync("source ./body", CancellationToken.None);
        byte[] expected = [.. "./body: line 2: help: no help topics match `"u8, 0xff,
            .. "'.  Try `help help' or `man -k "u8, 0xff, .. "' or `info "u8, 0xff, .. "'.\n"u8];

        Assert.Equal(1, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.Equal(expected, host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("help \\\n-Q", 2, 3, "help: -Q: invalid option\nhelp: usage: help [-dms] [pattern ...]\n")]
    [InlineData("help -s : >&-", 1, 2, "help: write error: Bad file descriptor\n")]
    public async Task SourcedHelpErrorsUseTheReachedLineAndDescriptor(string invocation,
        int status, int line, string diagnostic)
    {
        await using var host = new TestHost();
        host.AddFile("/body", $"# Leading line\n{invocation} \\\n2>diagnostic\n");
        var result = await new Engine().RunAsync(new Eval(Parser.Parse("source ./body")), host, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.True(host.StandardOutput.Content.IsEmpty);
        Assert.True(host.StandardError.Content.IsEmpty);
        Assert.Equal($"./body: line {line}: " + diagnostic, host.ReadFile("/diagnostic"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("local --help -q", "local: local [option] name[=value] ...\n")]
    [InlineData("command typeset --help", "typeset: typeset [-aAfFgiIlnrtux] name[=value] ... or typeset -p [-aAfFilnrtux] [name ...]\n")]
    [InlineData("f(){ local -x --help; }; f", "local: local [option] name[=value] ...\n")]
    public async Task DeclarationHelpUsesTheReachedOptionAndCommandName(string source, string synopsis)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.StartsWith(synopsis + "    ", result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(2, result.ExitCode);
    }

    [Theory]
    [InlineData("local -q", "line 1: local: can only be used in a function\n", 1)]
    [InlineData("readonly -fq --help", "line 1: readonly: -q: invalid option\nreadonly: usage: readonly [-aAf] [name[=value] ...] or readonly -p\n", 2)]
    [InlineData("readonly --unknown", "line 1: readonly: --: invalid option\nreadonly: usage: readonly [-aAf] [name[=value] ...] or readonly -p\n", 2)]
    [InlineData("readonly -- --help", "line 1: readonly: `--help': not a valid identifier\n", 1)]
    [InlineData("readonly --help >&-", "", 2)]
    public async Task DeclarationOptionFailuresPreserveTheirStatusAndDescriptor(string source, string error, int status)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.Empty(result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(status, result.ExitCode);
    }

    [Fact]
    public async Task DeclarationHelpRetainsCompoundAssignmentEffectsAndUsesRedirectedStdout()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "i=0; readonly --help a=($((++i))) >documentation; printf '[%s:%s][%s:%s]' \"$?\" \"${PIPESTATUS[*]}\" \"$i\" \"${a[0]}\"",
            CancellationToken.None);

        Assert.Equal("[2:2][1:1]", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.StartsWith("readonly: readonly [-aAf] [name[=value] ...] or readonly -p\n", host.ReadFile("/documentation"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task HelpKeepsTheAdmittedOutputUntilTheWriteSettles()
    {
        const int topics = 512;
        var source = "help -d" + string.Concat(Enumerable.Repeat(" true", topics));
        var record = "true - Return success.\n"u8.ToArray();
        const int allowance = 72 * 1024;
        var governor = new MemoryGovernor(allowance);
        using var owner = governor.CreateScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var evaluation = new Eval(Parser.Parse(source));
        evaluation.Memory.Budget = owner;

        var write = Assert.IsType<WriteOperation>(await evaluation.GetNextStep(deadline.Token));
        var pending = governor.ReservedBytes;
        try
        {
            Assert.True(write.FileDescriptor == FileDescriptor.StdOut, "Expected pending help output on stdout.");
            Assert.Equal(topics * record.Length, write.Content.Length);
            for (var offset = 0; offset < write.Content.Length; offset += record.Length)
                Assert.True(write.Content.Span.Slice(offset, record.Length).SequenceEqual(record));
            Assert.False(evaluation.Completion.IsCompleted);
            Assert.InRange(pending, write.Content.Length, allowance);
        }
        finally
        {
            write.SetResult(DescriptorWriteResult.Open);
            await evaluation.Completion.WaitAsync(deadline.Token);
        }

        Assert.Null(await evaluation.GetNextStep(deadline.Token));
        Assert.Equal(0, evaluation.LastExitCode);
        Assert.True(governor.ReservedBytes < pending);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
