using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("*")]
    [InlineData("*/child")]
    [InlineData("*/.")]
    public async Task GlobsResolveListedLinkTargetsOnlyForDirectoryTraversal(string pattern)
    {
        const string prefix = "alias/../";
        var state = new Eval(Parser.Parse("cmd " + prefix + pattern), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var listing = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/work/alias/..", listing.Path.Path);
        string[] names = ["directory-link", "file-link", "dangling", "denied"];
        listing.SetResult(DirectoryListingResult.Success(names
            .Select(name => new DirectoryEntry(name, FilePathKind.SymbolicLink)).ToArray()));

        if (pattern != "*")
        {
            FilePathResolutionResult[] results = [PathKindResult(FilePathKind.Directory), PathKindResult(FilePathKind.File),
                PathKindResult(FilePathKind.Missing), FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))];
            for (var index = 0; index < names.Length; index++)
            {
                var lookup = await NextPathKindsAsync(state);
                Assert.Equal(new FilePathLookup(RawShellPath.Parse(prefix + names[index]),
                    new ShellPath("/work"), FileLinkFollowing.Follow), Assert.Single(lookup.Paths));
                lookup.SetResult([results[index]]);
            }
        }
        if (pattern == "*/child")
        {
            var child = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
            Assert.Equal("/work/alias/../directory-link", child.Path.Path);
            child.SetResult(DirectoryListingResult.Success([new DirectoryEntry("child", FilePathKind.File)]));
        }

        var command = await NextCommandAsync(state, "cmd");
        var expected = pattern == "*" ? names.Order(StringComparer.Ordinal).Select(name => prefix + name).ToArray()
            : new[] { prefix + "directory-link/" + (pattern == "*/." ? "." : "child") };
        Assert.Equal(expected, command.Command.Arguments.Select(value => value.ToUtf8String()));
        command.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobListingsPreserveTraversalAndFailurePolicy(bool failed)
    {
        var state = new Eval(Parser.Parse("cmd alias/../dir/*/*"), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var root = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
        Assert.Equal("/work/alias/../dir", root.Path.Path);
        Assert.Equal(FileLinkFollowing.Follow, root.Path.LinkFollowing);
        root.SetResult(failed
            ? DirectoryListingResult.Failure(new OperationError("Not a directory\n"))
            : DirectoryListingResult.Success([new DirectoryEntry("nested", FilePathKind.Directory)]));
        if (!failed)
        {
            var child = Assert.IsType<ListDirectoryOperation>(await NextStepAsync(state));
            Assert.Equal("/work/alias/../dir/nested", child.Path.Path);
            child.SetResult(DirectoryListingResult.Success([new DirectoryEntry("input", FilePathKind.File)]));
        }
        var command = await NextCommandAsync(state, "cmd");
        Assert.Equal(failed ? "alias/../dir/*/*" : "alias/../dir/nested/input",
            Assert.Single(command.Command.Arguments).ToUtf8String());
        command.SetResult(new CommandExecutionResult.Completed(0));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("link/../input")]
    [InlineData("link/../../input")]
    [InlineData("link/")]
    [InlineData("link/.")]
    public async Task InputRedirectionPreservesTraversal(string operand)
    {
        var state = new Eval(Parser.Parse($": < {operand}"), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var open = Assert.IsType<OpenFileOperation>(await NextStepAsync(state));
        Assert.Equal(FileOpenMode.Read, open.Mode);
        Assert.Equal(new FilePathLookup(RawShellPath.Parse(operand), new ShellPath("/work"), FileLinkFollowing.Follow), open.Path);
        open.SetResult(FileOpenResult.Failed(new OperationError("rejected traversal\n")));
        var diagnostic = await NextWriteAsync(state);
        Assert.Contains("rejected traversal", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.NotEqual(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -L link -a -h link -a -f link -a -L dangling -a ! -e dangling -a ! -h absent -a ! -L denied -a -e good")]
    [InlineData("[ -L link -a -h link -a -f link -a -L dangling -a ! -e dangling -a ! -h absent -a ! -L denied -a -e good ]")]
    public async Task SymbolicLinks_BatchByPathAndPolicyAndDistinguishDanglingLinks(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal(new[] { "/link", "/link", "/dangling", "/dangling", "/absent", "/denied", "/good" },
            kinds.Paths.Select(path => path.Path));
        Assert.Equal(new[] { FileLinkFollowing.NoFollow, FileLinkFollowing.Follow, FileLinkFollowing.NoFollow,
            FileLinkFollowing.Follow, FileLinkFollowing.NoFollow, FileLinkFollowing.NoFollow, FileLinkFollowing.Follow },
            kinds.Paths.Select(path => path.LinkFollowing));
        kinds.SetResult([
            PathKindResult(FilePathKind.SymbolicLink), PathKindResult(FilePathKind.File),
            PathKindResult(FilePathKind.SymbolicLink), default, default,
            FilePathResolutionResult.Failure(new OperationError("Permission denied\n")), PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("-h", FilePathInfoRequest.KindOnly, FileLinkFollowing.NoFollow)]
    [InlineData("-L", FilePathInfoRequest.KindOnly, FileLinkFollowing.NoFollow)]
    [InlineData("-d", FilePathInfoRequest.KindOnly, FileLinkFollowing.Follow)]
    [InlineData("-r", FilePathInfoRequest.Metadata, FileLinkFollowing.Follow)]
    [InlineData("-s", FilePathInfoRequest.MetadataWithExactSize, FileLinkFollowing.Follow)]
    public async Task SymbolicLinks_PreserveTraversalBeforeHostResolution(
        string predicate, FilePathInfoRequest request, FileLinkFollowing following)
    {
        foreach (var operand in new[] { "link/../target", "link/", "link/.", "./link" })
        {
            var state = new Eval(Parser.Parse($"test {predicate} {operand}"), options:
                EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
            var operation = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
            Assert.Equal(request, operation.Request);
            Assert.Equal(new FilePathLookup(RawShellPath.Parse(operand), new ShellPath("/work"), following),
                Assert.Single(operation.Paths));
            // The provider selects the directory after physical traversal, including trailing slashes.
            operation.SetResult([SizeInfo(FilePathKind.Directory, 1)]);
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(following == FileLinkFollowing.NoFollow ? 1 : 0, state.LastExitCode);
        }
    }

    [Fact]
    public async Task SymbolicLinks_CancellationStopsWaitingForLookup()
    {
        var state = new Eval(Parser.Parse("test -L link; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(FileLinkFollowing.NoFollow, Assert.Single(operation.Paths).LinkFollowing);
        cancellation.Cancel();
        operation.SetResult([PathKindResult(FilePathKind.SymbolicLink)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }
}
