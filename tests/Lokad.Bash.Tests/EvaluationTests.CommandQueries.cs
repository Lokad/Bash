using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("command -V")]
    [InlineData("type")]
    public async Task Evaluation_CommandQueries_OperandFailureStopsBeforeLookupAndLaterEffects(string query)
    {
        var state = new Eval(Parser.Parse("x=0; " + query + " ${unset:?missing} \"$((x++))\""));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("unset: missing\n", Text(diagnostic.Content));
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
        var state = new Eval(Parser.Parse(Utf8(command + " echo missing : absent")));
        var lookup = await NextResolveCommandsAsync(state);
        Assert.Equal(["missing", "absent"], lookup.CommandNames);
        lookup.SetResult([new CommandLookupResult.Missing(), new CommandLookupResult.Missing()]);
        var builtin = command == "type" ? "type" : "command";
        string[] expected =
        [
            "echo is a shell builtin\n",
            builtin + ": missing: not found\n",
            ": is a shell builtin\n",
            builtin + ": absent: not found\n"
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
            Assert.Equal("type: write error: Write failed\n", Text(diagnostic.Content));
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
        var state = new Eval(Parser.Parse(Utf8(command + " echo missing enormous")));
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
