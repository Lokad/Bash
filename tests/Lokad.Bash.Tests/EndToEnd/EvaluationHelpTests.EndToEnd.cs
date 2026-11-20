using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

public sealed class EvaluationHelpTestsEndToEnd
{
    private const string TypeDocumentation = "type: type [-ftpP] [--] [name ...]\n"
        + "    Describe how names resolve. -t prints the kind. -p prints the path of an external command."
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
    [InlineData("help -s '['", "[: [ expression ]\n")]
    [InlineData("help -s 'tr?e'", "true: true\n")]
    [InlineData("help -m true", "NAME\n    true - Return success.\n\nSYNOPSIS\n    true\n\nDESCRIPTION\n    Return success.\n\n")]
    public async Task HelpKeepsTopicOrderAndExactRecordBytes(string source, string expected)
    {
        var result = await EvaluationTestHelper.RunAsync(source);

        Assert.Equal(expected, result.StdOut);
        Assert.Empty(result.StdErr);
        Assert.Equal(0, result.ExitCode);
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
    [InlineData("local -q", "local: can only be used in a function\n", 1)]
    [InlineData("readonly -fq --help", "readonly: -q: invalid option\nreadonly: usage: readonly [-aAf] [name[=value] ...] or readonly -p\n", 2)]
    [InlineData("readonly --unknown", "readonly: --: invalid option\nreadonly: usage: readonly [-aAf] [name[=value] ...] or readonly -p\n", 2)]
    [InlineData("readonly -- --help", "readonly: `--help': not a valid identifier\n", 1)]
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
