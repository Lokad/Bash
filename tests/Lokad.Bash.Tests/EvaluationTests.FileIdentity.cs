using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("a", "a", true, FilePathKind.File, 0)]
    [InlineData("a", "a", false, FilePathKind.File, 1)]
    [InlineData("a", "b", true, FilePathKind.File, 1)]
    [InlineData(null, "a", true, FilePathKind.File, 1)]
    [InlineData("a", null, true, FilePathKind.File, 1)]
    [InlineData(null, null, true, FilePathKind.File, 1)]
    [InlineData("a", "a", true, FilePathKind.Missing, 1)]
    [InlineData("a", "a", true, FilePathKind.Directory, 0)]
    [InlineData("a", "a", true, FilePathKind.Other, 0)]
    public async Task SameFile_UsesKnownIdentities(string? leftKey, string? rightKey, bool sameScope, FilePathKind kind, int exitCode)
    {
        var scope = new object();
        foreach (var script in new[] { "test left -ef right", "[ left -ef right ]", "[[ left -ef right ]]" })
        {
            var state = new Eval(Parser.Parse(script));
            var metadata = await NextMetadataAsync(state);
            Assert.Equal(new[] { "/left", "/right" }, metadata.Paths.Select(path => path.Path));
            metadata.SetResult([
                IdentityResult(leftKey is null ? null : new FileIdentity(scope, leftKey), kind),
                IdentityResult(rightKey is null ? null : new FileIdentity(sameScope ? scope : new object(), rightKey), kind)]);
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(exitCode, state.LastExitCode);
        }
    }

    [Fact]
    public async Task SameFile_DeduplicatesBothOperandsAndPreservesMixedResultPositions()
    {
        var identity = new FileIdentity(new object(), "file");
        var state = new Eval(Parser.Parse("[[ -e a && -r a && ! -O b && a -ef a && a -ef b && b -ef a && ! a -ef unknown && ! missing -ef a && ! a -ef missing && -L link ]]"),
            options: EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal(new[] { "/work/a", "/work/link" }, kinds.Paths.Select(path => path.Path));
        Assert.Equal(FileLinkFollowing.NoFollow, kinds.Paths[1].LinkFollowing);
        kinds.SetResult([PathKindResult(FilePathKind.File), PathKindResult(FilePathKind.SymbolicLink)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/work/a", "/work/b", "/work/unknown", "/work/missing" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([IdentityResult(identity, FilePathKind.File), IdentityResult(identity, FilePathKind.File),
            IdentityResult(null, FilePathKind.File), default]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(OperationErrorKind.PermissionDenied)]
    [InlineData(OperationErrorKind.NotFound)]
    [InlineData(OperationErrorKind.Unknown)]
    public async Task SameFile_MetadataFailuresDoNotInvalidateKindsOrOtherComparisons(OperationErrorKind error)
    {
        var state = new Eval(Parser.Parse("[[ -e denied && ! denied -ef good && ! good -ef denied && good -ef good && ! unavailable -ef good ]]"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/denied", "/good", "/unavailable" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("lookup failed\n") { Kind = error }),
            IdentityResult(new FileIdentity(new object(), "good"), FilePathKind.File), PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SameFile_ReusesTimeComparisonMetadataAlongsideOtherMetadataPredicates()
    {
        var identity = new FileIdentity(new object(), "file");
        var state = new Eval(Parser.Parse("[[ a -ef b && -r a && ! -O b && -u extra && ! a -nt b && -e a ]]"));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/a", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/a", "/b", "/extra" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([IdentityResult(identity, FilePathKind.File), IdentityResult(identity, FilePathKind.File),
            SpecialModeResult(FileSpecialModeBits.SetUserId, FilePathKind.File)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(OperationErrorKind.PermissionDenied)]
    [InlineData(OperationErrorKind.NotFound)]
    [InlineData(OperationErrorKind.Unknown)]
    public async Task SameFile_ReusesFailedMetadataWithoutRetryingOrInvalidatingKinds(OperationErrorKind error)
    {
        var state = new Eval(Parser.Parse("[[ -e failed && ! failed -nt good && ! failed -ef good && ! -r failed && good -ef good && -r good ]]"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/failed", "/good" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("lookup failed\n") { Kind = error }),
            IdentityResult(new FileIdentity(new object(), "good"), FilePathKind.File)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test '' -ef a", 1)]
    [InlineData("[ a -ef '\r' ]", 1)]
    [InlineData("[[ ! '' -ef '' ]]", 0)]
    [InlineData("false && test a -ef b", 1)]
    [InlineData("true || [[ a -ef b ]]", 0)]
    public async Task SameFile_InvalidOperandsAndSkippedCommandsDoNotQueryTheHost(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("a/", "a")]
    [InlineData("a", "a/")]
    [InlineData("dir/../a", "a")]
    [InlineData("a", "/../../dir/../a")]
    public async Task SameFile_PreservesBothOperandsTraversal(string left, string right)
    {
        var state = new Eval(Parser.Parse($"[[ {left} -ef {right} ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { left.StartsWith('/') ? left : "/" + left,
            right.StartsWith('/') ? right : "/" + right }, metadata.Paths.Select(path => path.Path));
        Assert.All(metadata.Paths, path => Assert.Equal(FileLinkFollowing.Follow, path.LinkFollowing));
        var identity = new FileIdentity(new object(), "a");
        metadata.SetResult([IdentityResult(identity, FilePathKind.Directory), IdentityResult(identity, FilePathKind.Directory)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SameFile_FailedTraversalDoesNotInvalidateAnotherComparisonForTheSamePath()
    {
        var state = new Eval(Parser.Parse("[[ ! a -ef dir/../a && a -ef ./a ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/a", "/dir/../a", "/./a" }, metadata.Paths.Select(path => path.Path));
        var identity = new FileIdentity(new object(), "a");
        metadata.SetResult([IdentityResult(identity, FilePathKind.File),
            FilePathResolutionResult.Failure(new OperationError("Permission denied\n")),
            IdentityResult(identity, FilePathKind.File)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SameFile_RejectsExcessiveRawPathsBeforeLookup()
    {
        foreach (var operand in new[] { new string('a', 4097), string.Concat(Enumerable.Repeat("a/../", 33)) + "a" })
        foreach (var script in new[] { $"[[ {operand} -ef a ]]", $"[[ a -ef {operand} ]]" })
        {
            var state = new Eval(Parser.Parse(script));
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(1, state.LastExitCode);
        }
    }

    [Fact]
    public async Task SameFile_CancellationStopsEvaluation()
    {
        var state = new Eval(Parser.Parse("test a -ef b; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var metadata = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        cancellation.Cancel();
        metadata.SetResult([default, default]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task SameFile_DispatchesMetadataWithoutReadingContent()
    {
        var identity = new FileIdentity(new object(), "file");
        var host = new TestOperationVisitor([]);
        host.EnqueuePathInfo(IdentityResult(identity, FilePathKind.File).Info);
        host.EnqueuePathInfo(IdentityResult(identity, FilePathKind.File).Info);
        var state = new Eval(Parser.Parse("[[ a -ef b && b -ef a ]]"));
        await new Engine().RunAsync(state, host, CancellationToken.None);
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(2, Assert.Single(host.PathInfoBatches).Count);
    }

    private static FilePathResolutionResult IdentityResult(FileIdentity? identity, FilePathKind kind) =>
        FilePathResolutionResult.Success(new FilePathInfo(kind,
            new FileMetadata(null, DateTimeOffset.UnixEpoch, FileAccessRights.Read, null, null, null, null, identity)));
}
