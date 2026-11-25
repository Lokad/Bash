using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task Evaluation_TypeBuiltin_OnlySendsTextNamesRequiringHostLookup()
    {
        var state = new Eval(Parser.Parse(
            "function café { :; }; type -t café echo 'if' 'host-é' $'\\xff' ''"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["host-é"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Found("/synthetic/host-é")]);

        var output = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, output.FileDescriptor);
        Assert.Equal("function\nbuiltin\nkeyword\nfile\n", Text(output.Content));
        output.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ConsumesHostRepliesInOperandOrder()
    {
        var state = new Eval(Parser.Parse(
            "f(){ :; }; type -t first echo if apply_patch f absent second $'\\xff' last"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["first", "apply_patch", "absent", "second", "last"], lookup.CommandNames);
        lookup.SetResult([
            new CommandLookupResult.Found("/synthetic/first"),
            new CommandLookupResult.Found("/synthetic/apply_patch"),
            new CommandLookupResult.Missing(),
            new CommandLookupResult.Failed(new OperationError("lookup unavailable\n")),
            new CommandLookupResult.Found("/synthetic/last")
        ]);

        var first = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, first.FileDescriptor);
        Assert.Equal("file\nbuiltin\nkeyword\nbuiltin\nfunction\n", Text(first.Content));
        first.SetResult(DescriptorWriteResult.Open);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: type: second: lookup unavailable\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        var last = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, last.FileDescriptor);
        Assert.Equal("file\n", Text(last.Content));
        last.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_TypeBuiltin_ForcedPathBatchesShadowedNamesThroughTheHost()
    {
        var state = new Eval(Parser.Parse(
            "tool(){ printf unexpected; }; PATH=/synthetic type -P tool printf if '/synthetic/absolute tool' '' $'\\xff' damaged"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["tool", "printf", "if", "/synthetic/absolute tool", "damaged"], lookup.CommandNames);
        Assert.Equal("/synthetic", FindEnvironmentValue(lookup.Environment, "PATH"));
        lookup.SetResult([
            new CommandLookupResult.Found("/synthetic/tool"),
            new CommandLookupResult.Found("/synthetic/printf"),
            new CommandLookupResult.Missing(),
            new CommandLookupResult.Found("/synthetic/absolute tool"),
            new CommandLookupResult.Failed(new OperationError("lookup unavailable\n"))
        ]);

        var output = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdOut, output.FileDescriptor);
        Assert.Equal("/synthetic/tool\n/synthetic/printf\n/synthetic/absolute tool\n", Text(output.Content));
        output.SetResult(DescriptorWriteResult.Open);
        var error = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, error.FileDescriptor);
        Assert.Equal("line 1: type: damaged: lookup unavailable\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Theory]
    [InlineData("command -V")]
    [InlineData("type")]
    public async Task Evaluation_CommandQueries_OperandFailureStopsBeforeLookupAndLaterEffects(string query)
    {
        var state = new Eval(Parser.Parse("x=0; " + query + " ${unset:?missing} \"$((x++))\""));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: unset: missing\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("0", GetVariable(state, "x"));
    }

    [Theory]
    [InlineData("type", false, 1)]
    [InlineData("type", true, 1)]
    [InlineData("command -V", false, 0)]
    [InlineData("command -V", true, 0)]
    public async Task Evaluation_CommandQueries_ContinueOrderedOutputAfterWriteFailures(
        string command, bool appendFailed, int expectedExitCode)
    {
        var state = new Eval(Parser.Parse(command + " echo missing : absent"));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["missing", "absent"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Missing(), new CommandLookupResult.Missing()]);
        var builtin = command == "type" ? "type" : "command";
        string[] expected =
        [
            "echo is a shell builtin\n",
            "line 1: " + builtin + ": missing: not found\n",
            ": is a shell builtin\n",
            "line 1: " + builtin + ": absent: not found\n"
        ];

        for (var index = 0; index < expected.Length; index++)
        {
            var append = await NextWriteAsync(state);
            var diagnostic = index % 2 == 1;
            Assert.Equal(diagnostic ? FileDescriptor.StdErr : FileDescriptor.StdOut, append.FileDescriptor);
            Assert.Equal(expected[index], Text(append.Content));
            append.SetResult(diagnostic || appendFailed
                ? DescriptorWriteResult.Failure(new OperationError("Write failed\n")) : DescriptorWriteResult.Open);
        }

        if (command == "type" && appendFailed)
        {
            var diagnostic = await NextWriteAsync(state);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
            Assert.Equal("line 1: type: write error: Write failed\n", Text(diagnostic.Content));
            diagnostic.SetResult(DescriptorWriteResult.Open);
        }

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(expectedExitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("type", "type")]
    [InlineData("command -V", "command")]
    public async Task Evaluation_CommandQueries_CheckTheSharedBudgetBeforeEitherChannel(
        string command, string builtin)
    {
        var state = new Eval(Parser.Parse(command + " echo missing enormous"));
        const int MemoryBudgetBytes = 16 * 1024;
        state.Memory.Budget = new Lokad.Bash.Memory.MemoryGovernor(MemoryBudgetBytes);
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["missing", "enormous"], lookup.CommandNames);
        var resolvedName = new string('x', MemoryBudgetBytes);
        lookup.SetResult(
        [
            new CommandLookupResult.Missing(),
            new CommandLookupResult.Found(resolvedName)
        ]);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal(builtin + ": shared memory budget exhausted\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }
}
