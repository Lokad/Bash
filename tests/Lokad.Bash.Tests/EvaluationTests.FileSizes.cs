using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("test -s same -a -f same -a -r same -a -s same -a ! -s empty -a ! -s missing -a ! -s '\r' -a -w same")]
    [InlineData("[ -s same -a -f same -a -r same -a -s same -a ! -s empty -a ! -s missing -a ! -s '\r' -a -w same ]")]
    [InlineData("[[ -s same && -f same && -r same && -s same && ! -s empty && ! -s missing && ! -s '\r' && -w same ]]")]
    public async Task NonEmptyPredicates_BatchDistinctPathsAndKeepResultPositions(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/same", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);

        var access = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.Metadata, access.Request);
        Assert.Equal("/same", Assert.Single(access.Paths).Path);
        access.SetResult([KnownAccessTimeResult()]);

        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, sizes.Request);
        Assert.Equal(new[] { "/same", "/empty", "/missing" }, sizes.Paths.Select(path => path.Path));
        sizes.SetResult([SizeInfo(FilePathKind.File, 1), SizeInfo(FilePathKind.File, 0), default]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData(FilePathKind.File, 0, 1)]
    [InlineData(FilePathKind.File, 1, 0)]
    [InlineData(FilePathKind.Directory, 4096, 0)]
    [InlineData(FilePathKind.Other, 0, 1)]
    [InlineData(FilePathKind.Other, 1, 0)]
    [InlineData(FilePathKind.Missing, 0, 1)]
    public async Task NonEmptyPredicates_TestReportedSizeOfExistingEntries(FilePathKind kind, long size, int exitCode)
    {
        var state = new Eval(Parser.Parse("test -s entry"));
        var operation = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, operation.Request);
        operation.SetResult([SizeInfo(kind, size)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Fact]
    public async Task NonEmptyPredicates_SizeFailuresDoNotInvalidateOtherPredicates()
    {
        var state = new Eval(Parser.Parse("[[ -f failed && ! -s failed && failed -nt old && -N failed && ! -u failed && -s good ]]"));
        var kinds = await NextPathKindsAsync(state);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/failed", "/old" }, metadata.Paths.Select(path => path.Path));
        metadata.SetResult([KnownAccessTimeResult(), TimeInfo(0)]);
        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, sizes.Request);
        Assert.Equal(new[] { "/failed", "/good" }, sizes.Paths.Select(path => path.Path));
        sizes.SetResult([FilePathResolutionResult.Failure(new OperationError("Size unavailable\n")), SizeInfo(FilePathKind.File, 1)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task NonEmptyPredicates_PreserveTraversalInTheSizeRequest()
    {
        var state = new Eval(Parser.Parse("[[ -s dir/../file && -s dir/../file ]]"));
        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, sizes.Request);
        Assert.Equal("/dir/../file", Assert.Single(sizes.Paths).Path);
        sizes.SetResult([SizeInfo(FilePathKind.File, 1)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task NonEmptyPredicates_KeepRawOperandsDistinctWhenCanonicalPathsMatch()
    {
        var state = new Eval(Parser.Parse("[[ ! -s file/ && -s file && ! -s missing/../file ]]"));
        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, sizes.Request);
        Assert.Equal(new[] { "/file/", "/file", "/missing/../file" }, sizes.Paths.Select(path => path.Path));
        sizes.SetResult([default, SizeInfo(FilePathKind.File, 1), default]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task NonEmptyPredicates_ResolveRelativeToTheWorkingDirectory()
    {
        var state = new Eval(Parser.Parse("test -s ../file"), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal("/work/../file", Assert.Single(sizes.Paths).Path);
        sizes.SetResult([SizeInfo(FilePathKind.File, 1)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("false && test -s file", 1)]
    [InlineData("true || [[ -s file ]]", 0)]
    [InlineData("if false; then [ -s file ]; fi", 0)]
    [InlineData("test -s ''", 1)]
    [InlineData("[[ -s '\r' ]]", 1)]
    public async Task NonEmptyPredicates_SkippedCommandsAndInvalidPathsDoNotLookUpSizes(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Fact]
    public async Task NonEmptyPredicates_DoNotReuseSizesAcrossCommands()
    {
        var state = new Eval(Parser.Parse("test -s file && test -s file"));
        var first = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        first.SetResult([SizeInfo(FilePathKind.File, 1)]);
        var second = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        second.SetResult([SizeInfo(FilePathKind.File, 0)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Theory]
    [InlineData("[[ -s file ]]; after=1", FilePathInfoRequest.MetadataWithExactSize)]
    [InlineData("[[ -s file && -r file ]]; after=1", FilePathInfoRequest.Metadata)]
    [InlineData("[[ -s dir/../file && -r file ]]; after=1", FilePathInfoRequest.Metadata)]
    public async Task NonEmptyPredicates_CancellationStopsBeforeLaterOperations(string script, FilePathInfoRequest request)
    {
        var state = new Eval(Parser.Parse(script));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(request, operation.Request);
        cancellation.Cancel();
        operation.SetResult([request == FilePathInfoRequest.KindOnly
            ? PathKindResult(FilePathKind.Directory) : SizeInfo(FilePathKind.File, 1)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Theory]
    [InlineData(4096, 1, true)]
    [InlineData(4097, 1, false)]
    [InlineData(256, 64, true)]
    [InlineData(256, 65, false)]
    public async Task NonEmptyPredicates_RespectRawPathLimits(int length, int components, bool supported)
    {
        var prefix = "/" + string.Concat(Enumerable.Repeat("a/", components - 1));
        var operand = prefix + new string('b', length - prefix.Length);
        var state = new Eval(Parser.Parse($"test -s {operand}"));
        var next = await NextStepAsync(state);
        if (supported)
        {
            var sizes = Assert.IsType<PathInfosOperation>(next);
            Assert.Equal(operand, Assert.Single(sizes.Paths).Path);
            sizes.SetResult([SizeInfo(FilePathKind.File, 1)]);
            Assert.Null(await NextStepAsync(state));
        }
        else
        {
            Assert.Null(next);
        }
        Assert.Equal(supported ? 0 : 1, state.LastExitCode);
    }

    private static FilePathResolutionResult SizeInfo(FilePathKind kind, long size) => FilePathResolutionResult.Success(
        new FilePathInfo(kind, new FileMetadata(size, DateTimeOffset.UnixEpoch, FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute, null, null, null, null, identity: null)));
}
