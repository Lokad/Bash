using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("-O", FileOwnershipMatch.EffectiveUser, 0)]
    [InlineData("-G", FileOwnershipMatch.EffectiveGroup, 0)]
    [InlineData("-O", FileOwnershipMatch.EffectiveGroup, 1)]
    [InlineData("-G", FileOwnershipMatch.EffectiveUser, 1)]
    [InlineData("-O", FileOwnershipMatch.EffectiveUser | FileOwnershipMatch.EffectiveGroup, 0)]
    [InlineData("-G", FileOwnershipMatch.EffectiveUser | FileOwnershipMatch.EffectiveGroup, 0)]
    [InlineData("-O", FileOwnershipMatch.None, 1)]
    [InlineData("-G", FileOwnershipMatch.None, 1)]
    [InlineData("-O", null, 1)]
    [InlineData("-G", null, 1)]
    public async Task OwnershipPredicates_UseReportedMatches(string predicate, FileOwnershipMatch? ownership, int exitCode)
    {
        foreach (var script in new[] { $"test {predicate} item", $"[ {predicate} item ]", $"[[ {predicate} item ]]" })
        {
            var state = new Eval(Parser.Parse(script));
            var metadata = await NextMetadataAsync(state);
            Assert.Equal("/item", Assert.Single(metadata.Paths).Path);
            metadata.SetResult([OwnershipResult(ownership, FilePathKind.File)]);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(exitCode, state.LastExitCode);
        }
    }

    [Theory]
    [InlineData("test -e a -a -r a -a ! -w a -a -x a -a ! -N a -a -u a -a ! -g a -a ! -k a -a -O a -a -G a -a -G dir -a ! -O other -a ! -G unknown -a ! -O missing -a -t 0")]
    [InlineData("[ -e a -a -r a -a ! -w a -a -x a -a ! -N a -a -u a -a ! -g a -a ! -k a -a -O a -a -G a -a -G dir -a ! -O other -a ! -G unknown -a ! -O missing -a -t 0 ]")]
    [InlineData("[[ -e a && -r a && ! -w a && -x a && ! -N a && -u a && ! -g a && ! -k a && -O a && -G a && -G dir && ! -O other && ! -G unknown && ! -O missing && -t 0 ]]")]
    public async Task OwnershipPredicates_ShareMetadataAndPreserveMixedResultPositions(string script)
    {
        var state = new Eval(Parser.Parse(script), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/work/a", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);

        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/work/a", "/work/dir", "/work/other", "/work/unknown", "/work/missing" },
            metadata.Paths.Select(path => path.Path));
        metadata.SetResult([
            FilePathResolutionResult.Success(new FilePathInfo(FilePathKind.File, new FileMetadata(
                null, DateTimeOffset.UnixEpoch, FileAccessRights.Read | FileAccessRights.Execute, null, null,
                FileSpecialModeBits.SetUserId, FileOwnershipMatch.EffectiveUser | FileOwnershipMatch.EffectiveGroup, identity: null))),
            OwnershipResult(FileOwnershipMatch.EffectiveGroup, FilePathKind.Directory),
            OwnershipResult(FileOwnershipMatch.None, FilePathKind.File),
            OwnershipResult(null, FilePathKind.File), default]);

        var hostTests = await NextTerminalStatusesAsync(state);
        Assert.Equal(FileDescriptor.StdIn, Assert.Single(hostTests.Descriptors));
        hostTests.SetResult([true]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(OperationErrorKind.PermissionDenied)]
    [InlineData(OperationErrorKind.NotFound)]
    [InlineData(OperationErrorKind.Unknown)]
    public async Task OwnershipPredicates_FailuresDoNotInvalidateKindsOrOtherMetadata(OperationErrorKind errorKind)
    {
        var state = new Eval(Parser.Parse("[[ -e denied && ! denied -nt old && ! -O denied && ! -G denied && -O good && -G good ]]"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/denied", "/old", "/good" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([
            FilePathResolutionResult.Failure(new OperationError("lookup failed\n") { Kind = errorKind }), TimeInfo(0),
            OwnershipResult(FileOwnershipMatch.EffectiveUser | FileOwnershipMatch.EffectiveGroup, FilePathKind.Other)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task OwnershipPredicates_UnavailableMetadataIsFalse()
    {
        var state = new Eval(Parser.Parse("[[ ! -O item && ! -G item ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Single(metadata.Paths);
        metadata.SetResult([PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -O ''", 1)]
    [InlineData("[ -G '\r' ]", 1)]
    [InlineData("[[ ! -G '' ]]", 0)]
    [InlineData("false && test -O skipped", 1)]
    [InlineData("true || [[ -G skipped ]]", 0)]
    public async Task OwnershipPredicates_InvalidOperandsAndSkippedCommandsDoNotQueryTheHost(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Fact]
    public async Task OwnershipPredicates_RejectExcessiveRawPathsBeforeLookup()
    {
        foreach (var operand in new[] { new string('a', 4097), string.Concat(Enumerable.Repeat("a/../", 33)) + "item" })
        {
            var state = new Eval(Parser.Parse($"[[ -O {operand} || -G {operand} ]]"));
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(1, state.LastExitCode);
        }
    }

    [Theory]
    [InlineData("target/")]
    [InlineData("dir/../target")]
    [InlineData("/../../dir/../target")]
    public async Task OwnershipPredicates_PreserveTraversalSyntax(string operand)
    {
        var state = new Eval(Parser.Parse($"[[ -O {operand} ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(operand.StartsWith('/') ? operand : "/" + operand, Assert.Single(metadata.Paths).Path);
        metadata.SetResult([OwnershipResult(FileOwnershipMatch.EffectiveUser, FilePathKind.Directory)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task OwnershipPredicates_FailedTraversalDoesNotInvalidateAnotherOperandForTheSamePath()
    {
        var state = new Eval(Parser.Parse("[[ ! -O dir/../target && -G target ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/dir/../target", "/target" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("Permission denied\n")), OwnershipResult(FileOwnershipMatch.EffectiveGroup, FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task OwnershipPredicates_CancellationStopsEvaluation()
    {
        var state = new Eval(Parser.Parse("test -O item; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var metadata = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));

        cancellation.Cancel();
        metadata.SetResult([OwnershipResult(FileOwnershipMatch.EffectiveUser, FilePathKind.File)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task OwnershipPredicates_DispatchMetadataWithoutReadingContent()
    {
        var host = new TestOperationVisitor([]);
        host.EnqueuePathInfo(OwnershipResult(FileOwnershipMatch.EffectiveUser, FilePathKind.File).Info);
        var state = new Eval(Parser.Parse("[[ -O item && ! -G item ]]"));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("/item", Assert.Single(Assert.Single(host.PathInfoBatches)).Path);
    }

    private static FilePathResolutionResult OwnershipResult(FileOwnershipMatch? ownership, FilePathKind kind) =>
        FilePathResolutionResult.Success(new FilePathInfo(kind,
            new FileMetadata(null, DateTimeOffset.UnixEpoch, FileAccessRights.Read | FileAccessRights.Execute, null, null, null, ownership, identity: null)));
}
