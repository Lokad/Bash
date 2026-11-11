using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("test -t 0 -a ! -t 1 -a -t 3 -a ! -t invalid -a ! -t 99 3<&0", false)]
    [InlineData("test -t 0 -a ! -t 1 -a -t 3 -a ! -t invalid -a ! -t 99 3<&0", true)]
    [InlineData("[[ -t 0 && ! -t 1 && -t 3 && ! -t invalid && ! -t 99 ]] 3<&0", false)]
    [InlineData("[[ -t 0 && ! -t 1 && -t 3 && ! -t invalid && ! -t 99 ]] 3<&0", true)]
    public async Task TerminalStatuses_DispatchAliasesAndPreserveResultOrder(string script, bool logging)
    {
        var basic = new TestOperationVisitor([]);
        basic.EnqueueTerminalStatuses([true, false, true, false]);
        IBashHost host = logging ? new LoggingBashHost(basic) : basic;
        var state = new Eval(Parser.Parse(script));

        await new Engine().RunAsync(state, host, CancellationToken.None);

        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(new[] { FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdIn, FileDescriptor.Closed },
            Assert.Single(basic.TerminalStatusBatches));
        Assert.Empty(basic.PathInfoBatches);
        Assert.Empty(basic.ClosedDescriptors);
    }

    [Fact]
    public async Task TerminalStatuses_CancellationStopsWaitingForHostResults()
    {
        var state = new Eval(Parser.Parse("test -t 0; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<TerminalStatusesOperation>(await state.GetNextStep(CancellationToken.None));

        cancellation.Cancel();
        operation.SetResult([true]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Theory]
    [InlineData("test -t 9 9> out")]
    [InlineData("[[ -t 9 ]] 9> out")]
    public async Task FileTests_ResolveLogicalDescriptorsBeforeHostDispatch(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var open = await NextOpenFileAsync(state);
        open.SetResult(FileOpenResult.Opened(new FileDescriptor(42), readSize: null));

        var operation = await NextTerminalStatusesAsync(state);
        var request = Assert.Single(operation.Descriptors);
        Assert.Equal(new FileDescriptor(42), request);
        operation.SetResult([true]);

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(new FileDescriptor(42), close.Descriptor);
        close.SetResult(null);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -d before -a ! -t invalid -a ! -d missing -a ! -e '\r' -a -f after -a ! -t -1")]
    [InlineData("[[ -d before && ! -t invalid && ! -d missing && ! -e '\r' && -f after && ! -t -1 ]]")]
    [InlineData("[[ ! -t invalid && -d before && ! -d missing && -f after ]]")]
    public async Task FileTests_KeepResultPositionsWhenInvalidOperandsAreOmitted(string script)
    {
        var state = new Eval(Parser.Parse(script));

        var operation = await NextPathKindsAsync(state);
        Assert.Equal(new[] { "/before", "/missing", "/after" },
            operation.Paths.Select(path => path.Path));
        operation.SetResult([PathKindResult(FilePathKind.Directory), PathKindResult(FilePathKind.Missing), PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -t invalid", 1)]
    [InlineData("test -t -1", 1)]
    [InlineData("test -t 2147483648", 1)]
    [InlineData("[[ ! -t invalid ]]", 0)]
    [InlineData("test -d ''", 1)]
    [InlineData("test -e '\r'", 1)]
    [InlineData("[[ ! -e '\r' ]]", 0)]
    [InlineData("test left -ef '\r'", 1)]
    [InlineData("[[ '\r' -nt right ]]", 1)]
    [InlineData("test left -ot '\r'", 1)]
    public async Task FileTests_InvalidOperandsEvaluateLocally(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -d .", "/work/.")]
    [InlineData("[[ -d ../../.. ]]", "/work/../../..")]
    public async Task FileTests_PreserveTraversalSyntax(string script, string expectedPath)
    {
        var state = new Eval(Parser.Parse(script), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath("/work") });

        var operation = await NextPathKindsAsync(state);
        Assert.Equal(expectedPath, Assert.Single(operation.Paths).Path);
        operation.SetResult([PathKindResult(FilePathKind.Directory)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task FileTests_CancellationStopsWaitingForHostResults()
    {
        var state = new Eval(Parser.Parse("[[ ! -t invalid && -e path ]]; after=1"));
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = Assert.IsType<PathInfosOperation>(await state.GetNextStep(CancellationToken.None));
        Assert.Equal(FilePathInfoRequest.KindOnly, operation.Request);

        cancellation.Cancel();
        operation.SetResult([PathKindResult(FilePathKind.Directory)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(state.Variables.ContainsKey("after"));
    }
    [Theory]
    [InlineData("test -e same -a -t 0 -a -f same -a ! -d same -a -d dir -a ! -e missing")]
    [InlineData("[[ -e same && -t 0 && -f same && ! -d same && -d dir && ! -e missing ]]")]
    public async Task FileTests_BatchDistinctPathsAndKeepHostPredicatePositions(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var paths = await NextPathKindsAsync(state);
        Assert.Equal(new[] { "/same", "/dir", "/missing" }, paths.Paths.Select(path => path.Path));
        paths.SetResult([PathKindResult(FilePathKind.File), PathKindResult(FilePathKind.Directory),
            PathKindResult(FilePathKind.Missing)]);

        var hostTests = await NextTerminalStatusesAsync(state);
        var request = Assert.Single(hostTests.Descriptors);
        Assert.Equal(FileDescriptor.StdIn, request);
        hostTests.SetResult([true]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -e special -a -a special -a ! -f special -a ! -d special -a -p special")]
    [InlineData("[[ -e special && -a special && ! -f special && ! -d special && -p special ]]")]
    public async Task FileTests_SpecialFilesExistWithoutBeingRegularFilesOrDirectories(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var paths = await NextPathKindsAsync(state);
        Assert.Equal("/special", Assert.Single(paths.Paths).Path);
        paths.SetResult([PathKindResult(FilePathKind.Fifo)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -e denied", 1)]
    [InlineData("[[ ! -f denied ]]", 0)]
    [InlineData("test ! -d denied -a -e found", 0)]
    public async Task FileTests_UnavailableKindsAreFalseWithoutLosingOtherResults(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));
        var paths = await NextPathKindsAsync(state);
        var results = paths.Paths.Select(path => path.Path == "/denied"
            ? FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))
            : PathKindResult(FilePathKind.File)).ToArray();
        paths.SetResult(results);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    [Theory]
    [InlineData("false && test -e skipped", 1)]
    [InlineData("true || [[ -d skipped ]]", 0)]
    [InlineData("if false; then test -f skipped; fi", 0)]
    public async Task FileTests_SkippedCommandsDoNotRequestPathKinds(string script, int exitCode)
    {
        var state = new Eval(Parser.Parse(script));

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(exitCode, state.LastExitCode);
    }

    private static FilePathResolutionResult PathKindResult(FilePathKind kind) =>
        FilePathResolutionResult.Success(FilePathInfo.FromKind(kind));

    [Theory]
    [InlineData("-b", FilePathKind.BlockDevice)]
    [InlineData("-c", FilePathKind.CharacterDevice)]
    [InlineData("-p", FilePathKind.Fifo)]
    [InlineData("-S", FilePathKind.Socket)]
    [InlineData("-h", FilePathKind.SymbolicLink)]
    [InlineData("-L", FilePathKind.SymbolicLink)]
    public async Task FileTests_SpecialPredicatesMatchOnlyTheirReportedKind(string predicate, FilePathKind expectedKind)
    {
        foreach (var kind in Enum.GetValues<FilePathKind>())
        foreach (var script in new[] { $"test {predicate} path", $"[ {predicate} path ]", $"[[ {predicate} path ]]" })
        {
            var state = new Eval(Parser.Parse(script));
            var paths = await NextPathKindsAsync(state);
            Assert.Equal("/path", Assert.Single(paths.Paths).Path);
            paths.SetResult([PathKindResult(kind)]);

            Assert.Null(await NextStepAsync(state));
            Assert.Equal(kind == expectedKind ? 0 : 1, state.LastExitCode);
        }
    }

    [Theory]
    [InlineData("test -b block -a -e block -a ! -f block -a -c char -a -p pipe -a -S socket -a -d dir -a -f regular -a ! -p missing -a ! -S denied -a -t 0")]
    [InlineData("[ -b block -a -e block -a ! -f block -a -c char -a -p pipe -a -S socket -a -d dir -a -f regular -a ! -p missing -a ! -S denied -a -t 0 ]")]
    [InlineData("[[ -b block && -e block && ! -f block && -c char && -p pipe && -S socket && -d dir && -f regular && ! -p missing && ! -S denied && -t 0 ]]")]
    public async Task FileTests_SpecialKindsShareDeduplicatedLookupsAndKeepDescriptorTestsSeparate(string script)
    {
        var state = new Eval(Parser.Parse(script));
        var paths = await NextPathKindsAsync(state);
        Assert.Equal(new[] { "/block", "/char", "/pipe", "/socket", "/dir", "/regular", "/missing", "/denied" },
            paths.Paths.Select(path => path.Path));
        paths.SetResult([
            PathKindResult(FilePathKind.BlockDevice), PathKindResult(FilePathKind.CharacterDevice),
            PathKindResult(FilePathKind.Fifo), PathKindResult(FilePathKind.Socket),
            PathKindResult(FilePathKind.Directory), PathKindResult(FilePathKind.File),
            PathKindResult(FilePathKind.Missing), FilePathResolutionResult.Failure(new OperationError("Permission denied\n"))]);

        var descriptors = await NextTerminalStatusesAsync(state);
        var descriptor = Assert.Single(descriptors.Descriptors);
        Assert.Equal(FileDescriptor.StdIn, descriptor);
        descriptors.SetResult([true]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }
}
