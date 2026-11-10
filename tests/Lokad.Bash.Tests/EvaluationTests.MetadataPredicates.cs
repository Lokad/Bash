using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData(FilePathKind.File, FileAccessRights.None, false, false, false)]
    [InlineData(FilePathKind.File, FileAccessRights.Read, true, false, false)]
    [InlineData(FilePathKind.File, FileAccessRights.Write, false, true, false)]
    [InlineData(FilePathKind.File, FileAccessRights.Execute, false, false, true)]
    [InlineData(FilePathKind.File, FileAccessRights.Read | FileAccessRights.Write, true, true, false)]
    [InlineData(FilePathKind.File, FileAccessRights.Read | FileAccessRights.Execute, true, false, true)]
    [InlineData(FilePathKind.File, FileAccessRights.Write | FileAccessRights.Execute, false, true, true)]
    [InlineData(FilePathKind.File, FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute, true, true, true)]
    [InlineData(FilePathKind.Directory, FileAccessRights.Execute, false, false, true)]
    [InlineData(FilePathKind.Other, FileAccessRights.Write, false, true, false)]
    [InlineData(FilePathKind.Missing, FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute, false, false, false)]
    public async Task EffectiveAccess_EvaluatesIndependentRightsForEveryTestForm(
        FilePathKind kind, FileAccessRights access, bool readable, bool writable, bool executable)
    {
        foreach (var (predicate, expected) in new[] { ("-r", readable), ("-w", writable), ("-x", executable) })
        foreach (var format in new[] { "test {0} file", "[ {0} file ]", "[[ {0} file ]]" })
        {
            var script = string.Format(format, predicate);
            var state = new Eval(Parser.Parse(script), options:
                EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
            var host = new TestOperationVisitor([]);
            host.EnqueuePathInfo(new FilePathInfo(kind, new FileMetadata(
                null, DateTimeOffset.UnixEpoch, access, statusChangedAt: null, accessedAt: null, specialModeBits: null, ownershipMatch: null, identity: null)));

            await new Engine().RunAsync(state, host, CancellationToken.None);

            Assert.Equal(expected ? 0 : 1, state.LastExitCode);
            Assert.Equal("/work/file", Assert.Single(Assert.Single(host.PathInfoBatches)).Path);
        }
    }

    [Theory]
    [InlineData(FilePathKind.File, 1, 0, 0)]
    [InlineData(FilePathKind.File, 0, 0, 1)]
    [InlineData(FilePathKind.File, -1, 0, 1)]
    [InlineData(FilePathKind.File, 1, null, 1)]
    [InlineData(FilePathKind.Missing, 1, 0, 1)]
    [InlineData(FilePathKind.Directory, 1, 0, 0)]
    [InlineData(FilePathKind.Other, 1, 0, 0)]
    public async Task AccessTimes_CompareKnownInstantsForEveryTestForm(
        FilePathKind kind, int modifiedTicks, int? accessedTicks, int exitCode)
    {
        var modifiedAt = DateTimeOffset.UnixEpoch.AddTicks(modifiedTicks);
        DateTimeOffset? accessedAt = accessedTicks is { } ticks
            ? DateTimeOffset.UnixEpoch.AddTicks(ticks).ToOffset(TimeSpan.FromHours(2)) : null;
        foreach (var script in new[] { "test -N file", "[ -N file ]", "[[ -N file ]]" })
        {
            var state = new Eval(Parser.Parse(script), options:
                EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
            var host = new TestOperationVisitor([]);
            host.EnqueuePathInfo(new FilePathInfo(kind, new FileMetadata(
                null, modifiedAt, FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute, DateTimeOffset.UnixEpoch.AddDays(-1), accessedAt, null, null, identity: null)));

            await new Engine().RunAsync(state, host, CancellationToken.None);

            Assert.Equal(exitCode, state.LastExitCode);
            Assert.Equal("/work/file", Assert.Single(Assert.Single(host.PathInfoBatches)).Path);
        }
    }

    [Theory]
    [InlineData("test -N same -a -w same -a -e same -a -r same -a -x same -a -t 0 -a -N same -a ! -N missing -a ! -r missing -a ! -w missing -a ! -x missing")]
    [InlineData("[ -N same -a -w same -a -e same -a -r same -a -x same -a -t 0 -a -N same -a ! -N missing -a ! -r missing -a ! -w missing -a ! -x missing ]")]
    [InlineData("[[ -N same && -w same && -e same && -r same && -x same && -t 0 && -N same && ! -N missing && ! -r missing && ! -w missing && ! -x missing ]]")]
    public async Task MetadataPredicates_BatchDistinctPathsAndPreserveOtherPredicateResults(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/same", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);

        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/same", "/missing" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([KnownAccessTimeResult(), default]);

        var hostTests = await NextTerminalStatusesAsync(state);
        Assert.Equal(FileDescriptor.StdIn, Assert.Single(hostTests.Descriptors));
        hostTests.SetResult([true]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task MetadataPredicates_EvaluateAccessAndTimestampsIndependentlyWithinOneBatch()
    {
        var state = new Eval(Parser.Parse("[[ -w writable && ! -r writable && ! -x writable && ! -N writable && -N modified && -r modified && -x modified && ! -w modified ]]"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/writable", "/modified" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([
            FilePathResolutionResult.Success(new FilePathInfo(FilePathKind.File, new FileMetadata(
                null, DateTimeOffset.UnixEpoch, FileAccessRights.Write, null, null, null, null, identity: null))),
            FilePathResolutionResult.Success(new FilePathInfo(FilePathKind.File, new FileMetadata(
                null, DateTimeOffset.UnixEpoch.AddTicks(1), FileAccessRights.Read | FileAccessRights.Execute, null, DateTimeOffset.UnixEpoch, null, null, identity: null)))
        ]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(OperationErrorKind.PermissionDenied)]
    [InlineData(OperationErrorKind.NotFound)]
    [InlineData(OperationErrorKind.Unknown)]
    public async Task MetadataPredicates_LookupFailuresDoNotInvalidateKindOrOtherMetadataResults(OperationErrorKind errorKind)
    {
        var state = new Eval(Parser.Parse("[[ -e denied && ! denied -nt old && ! -N denied && ! -r denied && ! -w denied && ! -x denied && -N good && -r good && -w good && -x good ]]"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/denied", "/old", "/good" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("lookup failed\n") { Kind = errorKind }), TimeInfo(0), KnownAccessTimeResult()]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -N ''", 1)]
    [InlineData("[ -N '\r' ]", 1)]
    [InlineData("[[ ! -N '' ]]", 0)]
    [InlineData("false && test -N skipped", 1)]
    [InlineData("true || [[ -N skipped ]]", 0)]
    [InlineData("test -w ''", 1)]
    [InlineData("[ -w '\r' ]", 1)]
    [InlineData("[[ ! -w '' ]]", 0)]
    [InlineData("false && test -w skipped", 1)]
    [InlineData("true || [[ -w skipped ]]", 0)]
    [InlineData("test -r ''", 1)]
    [InlineData("[ -r '\r' ]", 1)]
    [InlineData("[[ ! -r '' ]]", 0)]
    [InlineData("false && test -r skipped", 1)]
    [InlineData("true || [[ -r skipped ]]", 0)]
    [InlineData("test -x ''", 1)]
    [InlineData("[ -x '\r' ]", 1)]
    [InlineData("[[ ! -x '' ]]", 0)]
    [InlineData("false && test -x skipped", 1)]
    [InlineData("true || [[ -x skipped ]]", 0)]
    public async Task MetadataPredicates_InvalidOperandsAndSkippedCommandsDoNotQueryTheHost(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("target/")]
    [InlineData("dir/../target")]
    [InlineData("/../../dir/../target")]
    public async Task MetadataPredicates_PreserveTraversalSyntax(string operand)
    {
        foreach (var predicate in new[] { "-N", "-r", "-w", "-x" })
        {
            var state = new Eval(Parser.Parse($"[[ {predicate} {operand} ]]"));
            var metadata = await NextMetadataAsync(state);
            Assert.Equal(operand.StartsWith('/') ? operand : "/" + operand, Assert.Single(metadata.Paths).Path);
            metadata.SetResult([KnownAccessTimeResult()]);
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(0, state.LastExitCode);
        }
    }

    [Fact]
    public async Task MetadataPredicates_FailedTraversalIsFalse()
    {
        var state = new Eval(Parser.Parse("test -N dir/../target -o -r dir/../target -o -w dir/../target -o -x dir/../target"));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal("/dir/../target", Assert.Single(metadata.Paths).Path);
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Theory]
    [InlineData("[[ ! -w file/ && -N file ]]")]
    [InlineData("[[ -w file && ! -N file/ ]]")]
    [InlineData("[[ ! -r file/ && -x file ]]")]
    [InlineData("[[ -r file && ! -x file/ ]]")]
    public async Task MetadataPredicates_InvalidTraversalDoesNotInvalidateAnotherOperandForTheSamePath(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(2, metadata.Paths.Count);
        Assert.Contains(metadata.Paths, path => path.Path == "/file/");
        Assert.Contains(metadata.Paths, path => path.Path == "/file");
        metadata.SetResult(metadata.Paths.Select(path => path.Path == "/file/" ? default : KnownAccessTimeResult()).ToArray());
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task MetadataPredicates_RejectPathsOverSharedLimitsBeforeCollectingCheckpoints()
    {
        foreach (var operand in new[] { new string('a', 4097), string.Concat(Enumerable.Repeat("a/../", 33)) + "target" })
        {
            var state = new Eval(Parser.Parse($"test -N {operand} -o -r {operand} -o -w {operand} -o -x {operand}"));
            Assert.Null(await NextStepAsync(state));
            Assert.Equal(1, state.LastExitCode);
        }
    }

    [Fact]
    public async Task MetadataPredicates_CancellationStopsWaitingForMetadata()
    {
        var state = new Eval(Parser.Parse("test -N file -a -r file -a -w file -a -x file; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var metadata = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(FilePathInfoRequest.Metadata, metadata.Request);

        cancellation.Cancel();
        metadata.SetResult([KnownAccessTimeResult()]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task MetadataPredicates_UnexpectedLookupFailuresPropagate()
    {
        var failure = new InvalidOperationException("provider failure");
        var host = new MockFileSystem { BeforeGetPathInfo = _ => throw failure };
        var state = new Eval(Parser.Parse("[[ -N file && -r file && -w file && -x file ]]; after=1"));

        Assert.Same(failure, await Record.ExceptionAsync(() => new Engine().RunAsync(state, host, CancellationToken.None)));
        Assert.False(state.Variables.ContainsKey("after"));
    }

    private static async Task<PathInfosOperation> NextMetadataAsync(Eval state)
    {
        var operation = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.Metadata, operation.Request);
        return operation;
    }

    private static FilePathResolutionResult KnownAccessTimeResult() => FilePathResolutionResult.Success(new FilePathInfo(
        FilePathKind.File, new FileMetadata(null, DateTimeOffset.UnixEpoch.AddTicks(1), FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute,
            statusChangedAt: null, DateTimeOffset.UnixEpoch, specialModeBits: null, ownershipMatch: null, identity: null)));
}
