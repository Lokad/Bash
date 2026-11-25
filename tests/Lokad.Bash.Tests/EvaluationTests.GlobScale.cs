using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Tree;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobRetainsItsAllowanceUntilACancelledLookupSettles(bool linkCheck)
    {
        var state = new Eval(Parser.Parse(linkCheck ? "cmd */" : "cmd */leaf"));
        var budget = new MemoryGovernor(128 * 1024);
        using var owner = budget.CreateScope();
        using var cancellation = new CancellationTokenSource();
        state.Memory.Budget = owner;
        var running = state.EnsureRunning(cancellation.Token);
        var initial = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        initial.SetResult(DirectoryListingResult.Success(
            [new DirectoryEntry("candidate", linkCheck ? FilePathKind.SymbolicLink : FilePathKind.Directory)]));
        var lookup = await NextStepAsync(state);
        var reserved = budget.ReservedBytes;
        cancellation.Cancel();
        var completedEarly = await Task.WhenAny(running, Task.Delay(TimeSpan.FromMilliseconds(100))) == running;
        var reservedWhilePending = budget.ReservedBytes;
        if (linkCheck)
            Assert.IsType<PathInfosOperation>(lookup).SetResult([PathKindResult(FilePathKind.Directory)]);
        else
            Assert.IsType<ListDirectoryOperation>(lookup).SetResult(DirectoryListingResult.Success([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.False(completedEarly);
        Assert.True(reserved > 0);
        Assert.Equal(reserved, reservedWhilePending);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task GlobRefundsRejectedDirectoryCandidatesBeforeCheckingTheNextOne()
    {
        const int Count = 512;
        var entries = Enumerable.Range(0, Count)
            .Select(index => new DirectoryEntry($"candidate_{index:D4}", FilePathKind.SymbolicLink))
            .ToArray();
        var state = new Eval(Parser.Parse("cmd */"));
        var budget = new MemoryGovernor(96 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/", listing.Path.Path);
        listing.SetResult(DirectoryListingResult.Success(entries));

        var checkedLinks = 0;
        var step = await NextStepAsync(state);
        while (step is PathInfosOperation path)
        {
            Assert.Equal(FilePathInfoRequest.KindOnly, path.Request);
            Assert.Equal("/" + entries[checkedLinks++].Name, Assert.Single(path.Paths).Path);
            path.SetResult([PathKindResult(FilePathKind.File)]);
            step = await NextStepAsync(state);
        }
        if (step is WriteOperation diagnostic)
        {
            diagnostic.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await NextStepAsync(state));
        }

        Assert.Equal(Count, checkedLinks);
        var command = Assert.IsType<RunCommand>(step);
        Assert.Equal("*/", Assert.Single(command.Command.Arguments).ToUtf8String());
        command.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task GlobReleasesTerminalLookupPathsForAcceptedLinks()
    {
        var cwd = new ShellPath("/" + string.Join('/', Enumerable.Repeat(new string('d', 64), 12)));
        var entries = Enumerable.Range(0, 256).Reverse()
            .Select(index => new DirectoryEntry($"link_{index:D3}", FilePathKind.SymbolicLink)).ToArray();
        var state = new Eval(Parser.Parse("cmd */"), options: EvaluationOptions.Default with { CurrentDirectory = cwd });
        var budget = new MemoryGovernor(256 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal(cwd.Path, listing.Path.Path);
        listing.SetResult(DirectoryListingResult.Success(entries));

        foreach (var entry in entries)
        {
            var lookup = await NextPathKindsAsync(state);
            Assert.Equal(new FilePathLookup(RawShellPath.Parse(entry.Name), cwd, FileLinkFollowing.Follow),
                Assert.Single(lookup.Paths));
            lookup.SetResult([PathKindResult(FilePathKind.Directory)]);
        }

        var command = await NextCommandAsync(state, "cmd");
        Assert.Equal(entries.Reverse().Select(entry => entry.Name + "/"),
            command.Command.Arguments.Select(argument => argument.ToUtf8String()));
        command.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task GlobReusesCandidateAllowanceAcrossComponents()
    {
        const int Width = 64;
        const int Depth = 8;
        var entries = Enumerable.Range(0, Width)
            .Select(index => new DirectoryEntry($"branch_{index:D4}", FilePathKind.Directory)).ToArray();
        var component = DirectoryListingResult.Success([new DirectoryEntry("step", FilePathKind.Directory)]);
        var leaf = DirectoryListingResult.Success([new DirectoryEntry("leaf", FilePathKind.File)]);
        var state = new Eval(Parser.Parse("cmd */" + string.Concat(Enumerable.Repeat("step/", Depth)) + "leaf"));
        var budget = new MemoryGovernor(160 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        var initial = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/", initial.Path.Path);
        initial.SetResult(DirectoryListingResult.Success(entries.Reverse().ToArray()));

        var listings = 0;
        var step = await NextStepAsync(state);
        while (step is ListDirectoryOperation listing)
        {
            var depth = listings / Width;
            var branch = entries[Width - 1 - listings % Width].Name;
            Assert.Equal("/" + branch + string.Concat(Enumerable.Repeat("/step", depth)), listing.Path.Path);
            listing.SetResult(depth == Depth ? leaf : component);
            listings++;
            step = await NextStepAsync(state);
        }
        if (step is WriteOperation diagnostic)
        {
            diagnostic.SetResult(DescriptorWriteResult.Open);
            Assert.Null(await NextStepAsync(state));
        }

        Assert.Equal(Width * (Depth + 1), listings);
        var command = Assert.IsType<RunCommand>(step);
        var suffix = string.Concat(Enumerable.Repeat("/step", Depth)) + "/leaf";
        Assert.Equal(entries.Select(entry => entry.Name + suffix),
            command.Command.Arguments.Select(argument => argument.ToUtf8String()));
        command.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public async Task BroadGlobExpansionReturnsEveryDirectMatchInOrdinalOrder()
    {
        const int Count = 10_000;
        var entries = Enumerable.Range(0, Count)
            .Select(index => new DirectoryEntry($"value_{index:D5}", FilePathKind.File))
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

    [Fact]
    public async Task GlobExpansionOrdersMixedWidthPathsByTheirUtf8Bytes()
    {
        var prefix = new string('p', 96);
        string[] suffixes = [
            "\U0010FFFD", "\U00010400", "\uE000", "\U00010001", "\uFFFD",
            "\U00010000z", "\U00010000", "\U00010000\uE000", "\U00010000\U00010000", "a", ""
        ];
        var entries = suffixes.Select(suffix => new DirectoryEntry(prefix + suffix, FilePathKind.File)).ToArray();
        var expected = entries.Select(entry => ShellValue.FromText(entry.Name))
            .OrderBy(value => value, ShellValue.ByteOrder);
        var state = new Eval(Parser.Parse("cmd *"));
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        listing.SetResult(DirectoryListingResult.Success(entries));

        var command = await NextCommandAsync(state, "cmd");
        Assert.Equal(expected.Select(value => Convert.ToHexString(value.Bytes.Span)),
            command.Command.Arguments.Select(value => Convert.ToHexString(value.Bytes.Span)));
        command.SetResult(new CommandExecutionResult.Completed(0));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }
}
