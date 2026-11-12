using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("-u", FileSpecialModeBits.SetUserId, 0)]
    [InlineData("-g", FileSpecialModeBits.SetGroupId, 0)]
    [InlineData("-k", FileSpecialModeBits.Sticky, 0)]
    [InlineData("-u", FileSpecialModeBits.SetGroupId, 1)]
    [InlineData("-g", FileSpecialModeBits.Sticky, 1)]
    [InlineData("-k", FileSpecialModeBits.SetUserId, 1)]
    [InlineData("-u", FileSpecialModeBits.None, 1)]
    [InlineData("-g", FileSpecialModeBits.None, 1)]
    [InlineData("-k", FileSpecialModeBits.None, 1)]
    [InlineData("-u", null, 1)]
    [InlineData("-g", null, 1)]
    [InlineData("-k", null, 1)]
    public async Task SpecialModePredicates_UseReportedBits(string predicate, FileSpecialModeBits? bits, int exitCode)
    {
        foreach (var script in new[] { $"test {predicate} item", $"[ {predicate} item ]", $"[[ {predicate} item ]]" })
        {
            var state = new Eval(Parser.Parse(script));
            var metadata = await NextMetadataAsync(state);
            Assert.Equal("/item", Assert.Single(metadata.Paths).Path);
            metadata.SetResult([SpecialModeResult(bits, FilePathKind.File)]);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(exitCode, state.LastExitCode);
        }
    }

    [Theory]
    [InlineData("test -e a -a ! -N a -a -r a -a ! -w a -a -x a -a -u a -a -g a -a -k dir -a ! -u unset -a ! -k unknown -a ! -g missing -a -t 0")]
    [InlineData("[ -e a -a ! -N a -a -r a -a ! -w a -a -x a -a -u a -a -g a -a -k dir -a ! -u unset -a ! -k unknown -a ! -g missing -a -t 0 ]")]
    public async Task SpecialModePredicates_ShareMetadataAndPreserveMixedResultPositions(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/a", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);

        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/a", "/dir", "/unset", "/unknown", "/missing" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([
            SpecialModeResult(FileSpecialModeBits.SetUserId | FileSpecialModeBits.SetGroupId, FilePathKind.File),
            SpecialModeResult(FileSpecialModeBits.Sticky, FilePathKind.Directory),
            SpecialModeResult(FileSpecialModeBits.None, FilePathKind.File),
            SpecialModeResult(null, FilePathKind.File), default]);

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
    public async Task SpecialModePredicates_FailuresDoNotInvalidateKindsOrOtherMetadata(OperationErrorKind errorKind)
    {
        var state = new Eval(Parser.Parse("test -e denied -a ! denied -nt old -a ! -N denied -a ! -u denied -a ! -g denied -a ! -k denied -a -u good -a -g good -a -k good"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/denied", "/old", "/good" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([
            FilePathResolutionResult.Failure(new OperationError("lookup failed\n") { Kind = errorKind }), TimeInfo(0),
            SpecialModeResult(FileSpecialModeBits.SetUserId | FileSpecialModeBits.SetGroupId | FileSpecialModeBits.Sticky,
                FilePathKind.Other)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SpecialModePredicates_UnavailableMetadataIsFalse()
    {
        var state = new Eval(Parser.Parse("test ! -u item -a ! -g item -a ! -k item"));
        var metadata = await NextMetadataAsync(state);
        Assert.Single(metadata.Paths);
        metadata.SetResult([PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -u ''", 1)]
    [InlineData("[ -g '\r' ]", 1)]
    [InlineData("[[ ! -k '' ]]", 0)]
    [InlineData("false && test -u skipped", 1)]
    [InlineData("true || [[ -g skipped ]]", 0)]
    public async Task SpecialModePredicates_InvalidOperandsAndSkippedCommandsDoNotQueryTheHost(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("target/")]
    [InlineData("dir/../target")]
    [InlineData("/../../dir/../target")]
    public async Task SpecialModePredicates_PreserveTraversalSyntax(string operand)
    {
        var state = new Eval(Parser.Parse($"[[ -k {operand} ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(operand.StartsWith('/') ? operand : "/" + operand, Assert.Single(metadata.Paths).Path);
        metadata.SetResult([SpecialModeResult(FileSpecialModeBits.Sticky, FilePathKind.Directory)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SpecialModePredicates_FailedTraversalDoesNotInvalidateAnotherOperandForTheSamePath()
    {
        var state = new Eval(Parser.Parse("test ! -u dir/../target -a -g target"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/dir/../target", "/target" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("Permission denied\n")), SpecialModeResult(FileSpecialModeBits.SetGroupId, FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task SpecialModePredicates_CancellationStopsEvaluation()
    {
        var state = new Eval(Parser.Parse("test -u item; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var metadata = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));

        cancellation.Cancel();
        metadata.SetResult([SpecialModeResult(FileSpecialModeBits.SetUserId, FilePathKind.File)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task SpecialModePredicates_DispatchMetadataWithoutReadingContent()
    {
        var host = new TestOperationVisitor([]);
        host.EnqueuePathInfo(SpecialModeResult(FileSpecialModeBits.SetUserId | FileSpecialModeBits.Sticky, FilePathKind.File).Info);
        var state = new Eval(Parser.Parse("test -u item -a -k item -a ! -g item"));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal("/item", Assert.Single(Assert.Single(host.PathInfoBatches)).Path);
    }

    private static FilePathResolutionResult SpecialModeResult(FileSpecialModeBits? bits, FilePathKind kind) =>
        FilePathResolutionResult.Success(new FilePathInfo(kind,
            new FileMetadata(null, DateTimeOffset.UnixEpoch, FileAccessRights.Read | FileAccessRights.Execute, null, null, bits, null, identity: null)));
}
