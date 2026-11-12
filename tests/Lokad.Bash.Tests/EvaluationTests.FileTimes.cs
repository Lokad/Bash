using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("test -r new -a old -ot new -a new -nt ./old -a ! old -nt new -a ! new -ot old -a ! -t invalid -a ! old -nt '\r' -a -w old")]
    public async Task FileTimeComparisons_BatchDistinctOperandsAndKeepAccessPredicatePositions(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var operation = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/new", "/old", "/./old" }, operation.Paths.Select(path => path.Path));
        operation.SetResult([TimeInfo(1), TimeInfo(0), TimeInfo(0)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test left -nt ../right")]
    [InlineData("[[ left -nt ../right ]]")]
    public async Task FileTimeComparisons_ResolveBothOperandsAgainstTheWorkingDirectory(string script)
    {
        var state = new Eval(Parser.Parse(script), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });
        var operation = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/work/left", "/work/../right" }, operation.Paths.Select(path => path.Path));
        operation.SetResult([TimeInfo(1), TimeInfo(0)]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task FileTimeComparisons_DeduplicatePreservedOperands()
    {
        var state = new Eval(Parser.Parse("test -d dir -a new -nt dir/../old -a new -nt dir/../old"
            + " -a new -nt old/ -a ! new/ -nt old -a ! missing/../new -nt old"));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/dir", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.Directory)]);
        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/new", "/dir/../old", "/old/", "/new/", "/old", "/missing/../new" },
            metadata.Paths.Select(path => path.Path));
        metadata.SetResult([TimeInfo(1), TimeInfo(0), default, default, TimeInfo(0), default]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test new -nt denied/../missing")]
    [InlineData("[[ denied/../missing -ot new ]]")]
    public async Task FileTimeComparisons_DirectoryLookupFailuresAreNotMissingPaths(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var metadata = await NextMetadataAsync(state);
        Assert.Contains(metadata.Paths, path => path.Path == "/denied/../missing");
        metadata.SetResult(metadata.Paths.Select(path => path.Path == "/new" ? TimeInfo(1)
            : FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))).ToArray());
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task FileTimeComparisons_MetadataFailuresDoNotInvalidateKindPredicates()
    {
        var state = new Eval(Parser.Parse("test -f new -a ! new -nt missing"));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/new", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);
        var metadata = await NextMetadataAsync(state);
        metadata.SetResult([FilePathResolutionResult.Failure(new OperationError("Metadata unavailable\n")), default]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test new -nt dir/../old -o dir -nt missing", false)]
    [InlineData("[ new -nt dir/../old -a ! dir -nt missing ]", false)]
    [InlineData("test new -nt dir/../old -a ! dir -nt missing", false)]
    [InlineData("test -d dir -a dir/../new -nt dir/../old -a ! dir -nt missing", false)]
    [InlineData("test new -nt dir/../old -o dir -nt missing", true)]
    public async Task FileTimeComparisons_OperandFailuresDoNotInvalidateOtherPaths(
        string script, bool directoryLookupFails)
    {
        var state = new Eval(Parser.Parse(script));
        if (script.Contains("-d dir", StringComparison.Ordinal))
        {
            var kinds = await NextPathKindsAsync(state);
            Assert.Equal("/dir", Assert.Single(kinds.Paths).Path);
            kinds.SetResult([PathKindResult(FilePathKind.Directory)]);
        }
        var metadata = await NextMetadataAsync(state);
        metadata.SetResult(metadata.Paths.Select(path => path.Path switch
        {
            "/new" or "/dir/../new" => TimeInfo(1),
            "/dir/../old" when !directoryLookupFails => TimeInfo(0),
            "/missing" => default,
            _ => FilePathResolutionResult.Failure(new OperationError("Metadata unavailable\n"))
        }).ToArray());
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(directoryLookupFails ? 1 : 0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test {0} -nt present")]
    [InlineData("[ present -ot {0} ]")]
    [InlineData("[[ {0} -nt present ]]")]
    public async Task FileTimeComparisons_RejectOversizedTraversalBeforeAllocatingCheckpoints(string script)
    {
        var operand = "/" + string.Concat(Enumerable.Repeat("a/./", 4096)) + "file";
        var state = new Eval(Parser.Parse(string.Format(script, operand)));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var next = NextStepAsync(state);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(await next);
        Assert.Equal(1, state.LastExitCode);
        // Allow interpreter overhead, while rejecting the former hundreds of megabytes.
        Assert.InRange(allocated, 0, 4 * 1024 * 1024);
    }

    [Theory]
    [InlineData(4096, 1, true)]
    [InlineData(4097, 1, false)]
    [InlineData(256, 64, true)]
    [InlineData(256, 65, false)]
    public async Task FileTimeComparisons_RespectRawPathLimits(int length, int components, bool supported)
    {
        var prefix = "/" + string.Concat(Enumerable.Repeat("a/", components - 1));
        var operand = prefix + new string('b', length - prefix.Length);
        var state = new Eval(Parser.Parse($"test {operand} -nt ''"));
        var next = await NextStepAsync(state);
        if (supported)
        {
            var metadata = Assert.IsType<PathInfosOperation>(next);
            Assert.Equal(FilePathInfoRequest.Metadata, metadata.Request);
            Assert.Equal(operand, Assert.Single(metadata.Paths).Path);
            metadata.SetResult([TimeInfo(1)]);
            Assert.Null(await NextStepAsync(state));
        }
        else
        {
            Assert.Null(next);
        }
        Assert.Equal(supported ? 0 : 1, state.LastExitCode);
    }

    [Theory]
    [InlineData("test present -nt failed", false)]
    [InlineData("[[ present -ot failed ]]", false)]
    [InlineData("test failed -ot present", false)]
    [InlineData("[[ failed -nt present ]]", false)]
    [InlineData("[[ failed -ot missing ]]", true)]
    [InlineData("test missing -nt failed", true)]
    public async Task FileTimeComparisons_LookupFailuresAreNotMissingPaths(string script, bool otherMissing)
    {
        var state = new Eval(Parser.Parse(script));
        var operation = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        operation.SetResult(operation.Paths.Select(path => path.Path == "/failed"
            ? FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))
            : otherMissing ? FilePathResolutionResult.Success(default) : TimeInfo(0)).ToArray());

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task FileTimeComparisons_FailedLookupsDoNotDiscardSuccessfulComparisons()
    {
        var state = new Eval(Parser.Parse("test ! new -nt denied -a new -nt old -a ! denied -ot old"));
        var operation = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(new[] { "/new", "/denied", "/old" }, operation.Paths.Select(path => path.Path));
        operation.SetResult([TimeInfo(1), FilePathResolutionResult.Failure(new OperationError("unavailable\n")), TimeInfo(0)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("false && test left -nt right", 1)]
    [InlineData("true || [[ left -ot right ]]", 0)]
    [InlineData("if false; then [ left -nt right ]; fi", 0)]
    public async Task FileTimeComparisons_SkippedCommandsDoNotLookUpMetadata(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Fact]
    public async Task FileTimeComparisons_DoNotReuseMetadataAcrossCommands()
    {
        var state = new Eval(Parser.Parse("test old -ot new && test old -ot new"));
        var first = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        first.SetResult([TimeInfo(0), TimeInfo(1)]);
        var second = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        second.SetResult([TimeInfo(2), TimeInfo(1)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(1, state.LastExitCode);
    }

    [Fact]
    public async Task FileTimeComparisons_CancellationStopsBeforeHostPredicatesAndFollowingCommands()
    {
        var state = new Eval(Parser.Parse("test new -nt old -a -r new; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));

        cancellation.Cancel();
        operation.SetResult([TimeInfo(1), TimeInfo(0)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    private static FilePathResolutionResult TimeInfo(int seconds) => FilePathResolutionResult.Success(
        new FilePathInfo(FilePathKind.File,
            new FileMetadata(null, DateTimeOffset.UnixEpoch.AddSeconds(seconds), FileAccessRights.Read | FileAccessRights.Write | FileAccessRights.Execute, null, null, null, null, identity: null)));
}
