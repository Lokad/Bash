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
