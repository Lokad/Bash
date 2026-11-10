using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task GlobArgumentEncodingConsumesTheCharacterAllowance()
    {
        // printf consumes byte-valued operands, so later argument decoding cannot mask a missing charge.
        var state = new Eval(Parser.Parse("printf '%s' *; echo should-not-run"));
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        // The UTF-16 path fits the remaining allowance; its UTF-8 argument copy does not.
        state.ExpansionBudget.Words.Charge(0, 16 * 1024 * 1024 - 300);
        listing.SetResult(DirectoryListingResult.Success([new DirectoryEntry(new string('界', 128), FilePathKind.File)]));

        var error = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, error.FileDescriptor);
        Assert.Equal("shell: word expansion exceeds its word or character limit\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task BroadGlobExpansionReturnsEveryDirectMatchInOrdinalOrder()
    {
        const int Count = 10_000;
        var entries = Enumerable.Range(0, Count)
            .Select(index => new DirectoryEntry(Utf8($"value_{index:D5}"), FilePathKind.File))
            .ToArray();
        var state = new Eval(Parser.Parse("cmd *"));
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/", listing.Path.Path);
        // Reverse host order so the consumer must actually sort, without constructing a FileTree.
        listing.SetResult(DirectoryListingResult.Success(entries.Reverse().ToArray()));

        var command = await NextCommandAsync(state, "cmd");
        var matches = command.Command.Arguments;
        Assert.Equal(Count, matches.Count);
        Assert.Equal("value_00000", matches[0].ToUtf8String());
        Assert.Equal("value_09999", matches[^1].ToUtf8String());
        Assert.Equal(entries.Select(entry => entry.Name), matches.Select(argument => argument.ToUtf8String()));
        command.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }
}
