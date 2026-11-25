using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Theory]
    [InlineData("test -t 0 -a ! -t 1 -a -t 3 -a ! -t -1 -a ! -t 99 3<&0", false)]
    [InlineData("test -t 0 -a ! -t 1 -a -t 3 -a ! -t -1 -a ! -t 99 3<&0", true)]
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
    [InlineData("test -d before -a ! -t 2147483648 -a ! -d missing -a ! -e '\r' -a -f after -a ! -t -1")]
    [InlineData("test ! -t -1 -a -d before -a ! -d missing -a -f after")]
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

    [Fact]
    public async Task BracketTestResumesWithTheOriginalOperandsAfterAFileLookup()
    {
        var state = new Eval(Parser.Parse("a=([3]=present); i=3; [ -e first -a -v 'a[i++]' -a -e last ]"));
        var first = await NextPathKindsAsync(state);
        Assert.Equal("/first", Assert.Single(first.Paths).Path);
        Assert.Equal(ShellValue.FromText("3"), state.Variables["i"]);
        first.SetResult([PathKindResult(FilePathKind.File)]);

        var last = await NextPathKindsAsync(state);
        Assert.Equal("/last", Assert.Single(last.Paths).Path);
        Assert.Equal(ShellValue.FromText("4"), state.Variables["i"]);
        last.SetResult([PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task ConstantGroupsKeepBothFileLookupsAndTheVariableSelector()
    {
        var state = new Eval(Parser.Parse("a=(present); i=0; "
            + "test \\( -n yes -a -z '' \\) -o \\( -e first -a -v 'a[i++]' -a -e last \\)"));

        var first = await NextPathKindsAsync(state);
        Assert.Equal("/first", Assert.Single(first.Paths).Path);
        Assert.Equal(ShellValue.FromText("0"), state.Variables["i"]);
        first.SetResult([PathKindResult(FilePathKind.Missing)]);

        var last = await NextPathKindsAsync(state);
        Assert.Equal("/last", Assert.Single(last.Paths).Path);
        Assert.Equal(ShellValue.FromText("1"), state.Variables["i"]);
        last.SetResult([PathKindResult(FilePathKind.File)]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" '!'")]
    public async Task MixedTestRunsParsedPredicatesBeforeReportingASyntaxError(string unfinishedTerm)
    {
        var state = new Eval(Parser.Parse("a=([3]=present); i=3; test -e first -a -v 'a[i++]' -a" + unfinishedTerm));
        var first = await NextPathKindsAsync(state);
        Assert.Equal("/first", Assert.Single(first.Paths).Path);
        Assert.Equal(ShellValue.FromText("3"), state.Variables["i"]);
        first.SetResult([PathKindResult(FilePathKind.File)]);

        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(ShellValue.FromText("4"), state.Variables["i"]);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: test: argument expected\n"u8.ToArray(), diagnostic.Content.ToArray());
        diagnostic.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Fact]
    public async Task TerminalGrammarErrorKeepsEarlierVariableTestsAndSkipsLaterOnes()
    {
        var state = new Eval(Parser.Parse("a=([0]=present); i=0; test -v 'a[i++]' -a -t invalid -a -v 'a[i++]'"));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(ShellValue.FromText("1"), state.Variables["i"]);
        Assert.Equal("line 1: test: too many arguments\n", Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -t invalid", 1)]
    [InlineData("test yes -a yes -a -t", 0)]
    [InlineData("test -t $'\\xff'", 1)]
    [InlineData("test -t -1", 1)]
    [InlineData("test -t 2147483648", 1)]
    [InlineData("[[ ! -t invalid ]]", 0)]
    [InlineData("[[ ! -t $'\\xff' ]]", 0)]
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
    [InlineData("[ yes -a -t $'\\r+0\\t' ]", 0)]
    [InlineData("test -t '+0'", 0)]
    [InlineData("[[ -t '+0' ]]", 0)]
    public async Task TerminalPredicates_RequestTheSelectedDescriptor(string script, int descriptor)
    {
        var state = new Eval(Parser.Parse(script));
        var operation = await NextTerminalStatusesAsync(state);
        Assert.Equal(new FileDescriptor(descriptor), Assert.Single(operation.Descriptors));
        operation.SetResult([true]);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -t invalid -a yes", "test: too many arguments\n")]
    [InlineData("[ yes -o -t '' ]", "[: too many arguments\n")]
    [InlineData("test -t -x -a yes", "test: syntax error: `-x' unexpected\n")]
    [InlineData("test yes -a \\( -t invalid \\)", "test: `)' expected, found invalid\n")]
    public async Task TerminalPredicates_LeaveNonnumericTokensForTheExpression(string script, string message)
    {
        var state = new Eval(Parser.Parse(script));
        var diagnostic = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, diagnostic.FileDescriptor);
        Assert.Equal("line 1: " + message, Text(diagnostic.Content));
        diagnostic.SetResult(DescriptorWriteResult.Open);
        Assert.Null(await NextStepAsync(state));
        Assert.Equal(2, state.LastExitCode);
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

    [Theory]
    [InlineData("test -t 0", true)]
    [InlineData("test ! -t -1 -a -e path", false)]
    [InlineData("[ -e path -a -v 'a[0]' ]", false)]
    public async Task FileTests_RetainTheirAllowanceUntilTheHostSettles(string command, bool terminal)
    {
        var state = new Eval(Parser.Parse(command + "; after=1"));
        var budget = new MemoryGovernor(128 * 1024);
        using var owner = budget.CreateScope();
        state.Memory.Budget = owner;
        using var cancellation = new CancellationTokenSource();
        var completion = state.EnsureRunning(cancellation.Token);
        var operation = await state.GetNextStep(CancellationToken.None);

        var reserved = budget.ReservedBytes;
        cancellation.Cancel();
        var completedEarly = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromMilliseconds(100))) == completion;
        var pending = budget.ReservedBytes;
        if (terminal)
            Assert.IsType<TerminalStatusesOperation>(operation).SetResult([true]);
        else
            Assert.IsType<PathInfosOperation>(operation).SetResult([PathKindResult(FilePathKind.Directory)]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);
        Assert.False(completedEarly);
        Assert.Equal(reserved, pending);
        Assert.True(budget.ReservedBytes < reserved);
        Assert.False(state.Variables.ContainsKey("after"));
    }

    [Fact]
    public async Task ConstantTestChainUsesTheAdmittedCommandWorkspace()
    {
        var expression = string.Join(" -a ", Enumerable.Repeat("-n yes", 1_024));
        var state = new Eval(Parser.Parse("test " + expression));
        var budget = new MemoryGovernor(256 * 1024);
        var host = new TestOperationVisitor([]) { MemoryBudget = budget };

        using (var owner = budget.CreateScope())
        {
            await new Engine().RunAsync(state, host, owner, CancellationToken.None);
            Assert.True(budget.ReservedBytes < budget.PeakBytes);
        }

        Assert.Equal(0, state.LastExitCode);
        Assert.Empty(host.PathInfoBatches);
        Assert.Empty(host.TerminalStatusBatches);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("-e", "relative", 512, true)]
    [InlineData("-r", "relative", 512, true)]
    [InlineData("-s", "relative", 512, true)]
    [InlineData("-e", "relative", 2048, false)]
    [InlineData("-r", "relative", 2048, false)]
    [InlineData("-s", "relative", 2048, false)]
    [InlineData("-e", "absolute", 512, false)]
    [InlineData("-r", "absolute", 512, false)]
    [InlineData("-s", "absolute", 512, false)]
    [InlineData("-e", "repeated", 512, false)]
    [InlineData("-r", "repeated", 512, false)]
    [InlineData("-s", "repeated", 512, false)]
    public async Task FileTests_AdmitDistinctQualifiedPathsBeforeAnyLookup(
        string predicate, string spelling, int limitKiB, bool refused)
    {
        const int Count = 512;
        var directory = "/" + string.Join('/', Enumerable.Repeat(new string('d', 128), 8));
        var operands = Enumerable.Range(0, Count).Select(index => spelling switch
        {
            "absolute" => $"/entry{index:D4}",
            "repeated" => "entry0000",
            _ => $"entry{index:D4}"
        }).ToArray();
        var script = "test -e /guard -a " + string.Join(" -a ", operands.Select(path => predicate + " " + path));
        var state = new Eval(Parser.Parse(script), options:
            EvaluationOptions.Default with { CurrentDirectory = new ShellPath(directory) });
        var budget = new MemoryGovernor(limitKiB * 1024);
        using var owner = budget.CreateScope();
        var host = new TestOperationVisitor([]) { MemoryBudget = owner, RecordAppends = true };
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueuePathInfo(FilePathInfo.FromKind(FilePathKind.File));
        var info = new FilePathInfo(FilePathKind.File, new FileMetadata(1, DateTimeOffset.UnixEpoch,
            FileAccessRights.Read, null, null, null, null, null));
        var paths = operands.Distinct().Select(path => path[0] == '/' ? path : directory + "/" + path).ToArray();
        foreach (var _ in paths)
            host.EnqueuePathInfo(info);

        await new Engine().RunAsync(state, host, CancellationToken.None);

        if (refused)
        {
            Assert.Empty(host.PathInfoBatches);
            var diagnostic = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdErr, diagnostic.Descriptor);
            Assert.Equal("test: shared memory budget exhausted\n", Text(diagnostic.Content));
        }
        else
        {
            Assert.Equal(new[] { "/guard" }.Concat(paths), host.PathInfoRequests.Select(path => path.Path));
            Assert.Equal(predicate == "-e" ? 1 : 2, host.PathInfoBatches.Count);
            Assert.Empty(host.Appends);
        }
        Assert.Equal(refused ? 1 : 0, state.LastExitCode);
        owner.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Theory]
    [InlineData("test -e same -a -t 0 -a -f same -a ! -d same -a -d dir -a ! -e missing")]
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

    [Fact]
    public async Task FileTests_SeparateRequestKindsWhenComparisonsShareMetadata()
    {
        var state = new Eval(Parser.Parse("test ! -t 2147483648 -a -t 0 -a -s file -a file -ef alias"
            + " -a -r file -a file -nt missing -a -e file -a ! -s empty"));
        var kinds = await NextPathKindsAsync(state);
        Assert.Equal("/file", Assert.Single(kinds.Paths).Path);
        kinds.SetResult([PathKindResult(FilePathKind.File)]);

        var metadata = await NextMetadataAsync(state);
        Assert.Equal(new[] { "/file", "/alias", "/missing" }, metadata.Paths.Select(path => path.Path));
        var identity = new FileIdentity(new object(), "shared");
        var file = FilePathResolutionResult.Success(new FilePathInfo(FilePathKind.File,
            new FileMetadata(null, DateTimeOffset.UnixEpoch, FileAccessRights.Read, null, null, null, null, identity)));
        metadata.SetResult([file, file, default]);

        var sizes = Assert.IsType<PathInfosOperation>(await NextStepAsync(state));
        Assert.Equal(FilePathInfoRequest.MetadataWithExactSize, sizes.Request);
        Assert.Equal(new[] { "/file", "/empty" }, sizes.Paths.Select(path => path.Path));
        sizes.SetResult([SizeInfo(FilePathKind.File, 1), SizeInfo(FilePathKind.File, 0)]);

        var terminals = await NextTerminalStatusesAsync(state);
        Assert.Equal(FileDescriptor.StdIn, Assert.Single(terminals.Descriptors));
        terminals.SetResult([true]);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("test -e special -a -a special -a ! -f special -a ! -d special -a -p special")]
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
