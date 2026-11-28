using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class SourceMemoryLifetimeTests
{
    private const int PaddingBytes = 32768;

    [Theory]
    [InlineData("eval")]
    [InlineData("source")]
    [InlineData("import")]
    public async Task PublishedFunctionKeepsTheCompletedSyntaxReservation(string reader)
    {
        const string Definition = "probe(){ printf body; }\n";
        var input = Encoding.UTF8.GetBytes(Definition);
        var governor = new MemoryGovernor(1024 * 1024);
        long syntaxBytes;
        using (var parserOwner = governor.CreateScope())
        using (var syntax = new MemoryCapacity(parserOwner, 0))
        {
            var parser = new ShellScanContext { SyntaxMemory = syntax, ReadsCommandString = reader != "import" };
            if (reader == "import")
            {
                syntax.Grow(32L + input.Length);
                Assert.True(Parser.TryParseScript(input, parser, out _, out _));
            }
            else
                Assert.IsType<CommandUnitParseResult.Parsed>(Parser.TryParseCommandUnits(input, parser).First());
            syntaxBytes = syntax.Bytes;
        }

        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        using var owner = budget.CreateScope();
        await using var host = new TestHost { MemoryBudget = budget };
        if (reader == "source")
            host.AddFile("/fixture", Definition);
        var invocation = reader switch
        {
            "source" => "source /fixture; ",
            "eval" => "eval '" + Definition + "'; ",
            _ => "",
        };
        var evaluation = new Eval(Parser.Parse(invocation + "printf loaded; probe; unset -f probe"));
        if (reader == "import")
            evaluation.SetInheritedFunctions("bash", [new ExportedFunction(input)]);

        IMemoryReservation? retained = null;
        host.BeforeAppend = (_, content, _) =>
        {
            if (content.Span.SequenceEqual("loaded"u8))
                retained = Assert.Single(scopes.SelectMany(scope => scope.Reservations), item => item.Bytes == syntaxBytes);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new Engine().RunAsync(evaluation, host, owner, deadline.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("loadedbody", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.NotNull(retained);
        Assert.Equal(0, retained.Bytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task DeferredCaptureKeepsTheCompletedSyntaxThroughExecution()
    {
        const string Body = "printf body\n";
        var governor = new MemoryGovernor(1024 * 1024);
        long syntaxBytes;
        using (var parserOwner = governor.CreateScope())
        using (var syntax = new MemoryCapacity(parserOwner, 0))
        {
            var parser = new ShellScanContext { SyntaxMemory = syntax };
            Assert.True(Parser.TryParseScript(Encoding.UTF8.GetBytes(Body), parser, out _, out _));
            syntaxBytes = syntax.Bytes;
        }

        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        using var owner = budget.CreateScope();
        await using var host = new TestHost { MemoryBudget = budget };
        var capture = new Word([WordPart.FromText(WordPartKind.CommandSubstitutionQuoted, Body)]);
        var command = Assert.IsType<SimpleCommand>(Assert.Single(Parser.Parse("printf '%s'").Commands));
        var evaluation = new Eval(new ParsedScript([command with { Words = [.. command.Words, capture] }]));
        evaluation.Memory.Budget = owner;

        IMemoryReservation? retained = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await evaluation.GetNextStep(deadline.Token) is { } operation)
        {
            if (operation is RunCapturedEvaluation)
                retained = Assert.Single(scopes.SelectMany(scope => scope.Reservations), item => item.Bytes == syntaxBytes);
            await ShellOperationDispatcher.DispatchAsync(operation, host, deadline.Token);
        }
        await evaluation.Completion;

        Assert.Equal(0, evaluation.LastExitCode);
        Assert.Equal("body", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.TextContent);
        Assert.NotNull(retained);
        Assert.Equal(0, retained.Bytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(4096, false, false)]
    [InlineData(4096, false, true)]
    [InlineData(16384, false, true)]
    [InlineData(4096, true, true)]
    public async Task FragmentedSourceUsesItsParserBufferForImmutableWindows(int finalBytes, bool cancel, bool sizeKnown)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        using var owner = budget.CreateScope();
        var first = Records("value=prefix", 384);
        var middle = Records("value+=-middle", 64);
        await using var host = new WindowedSourceHost(first.Length, middle.Length)
            { MemoryBudget = budget, PreparedReadSizeKnown = sizeKnown };
        const string Definition = "probe(){ printf '%s' \"$value\" $'\\377\\r'; }\n#";
        host.AddFile("/a", first + middle + Definition + new string('x', finalBytes - Definition.Length - 1) + "\n");
        var sourceBytesAtRead = new List<long>();
        host.BeforeRead = () => sourceBytesAtRead.Add(SourceBackingBytes());
        var state = new Eval(Parser.Parse("source /a; probe; unset -f probe"));
        state.Memory.Budget = owner;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        host.BeforeReadBuffer = _ =>
        {
            if (cancel && host.ReadCallCount == 3)
                cancellation.Cancel();
        };
        var completion = state.EnsureRunning(cancellation.Token);
        var token = deadline.Token;
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(token));
        await ShellOperationDispatcher.DispatchAsync(open, host, token);

        var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep(token));
        var batch = Assert.IsType<RecordedScope>(read.DestinationBudget);
        var admissions = new List<long>();
        batch.ReservationAdmitted = admissions.Add;
        var dispatch = ShellOperationDispatcher.DispatchAsync(read, host, cancellation.Token);
        if (cancel)
        {
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
        }
        else
            await dispatch;
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep(token));
        var liveSourceBytes = SourceBackingBytes();
        await ShellOperationDispatcher.DispatchAsync(close, host, token);

        if (cancel)
        {
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(token));
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.False(state.Variables.ContainsKey("value"));
            Assert.Empty(host.StandardOutput.Content.ToArray());
        }
        else
        {
            var write = Assert.IsType<WriteOperation>(await state.GetNextStep(token));
            Assert.Equal<byte>([.. "prefix-middle"u8, 0xff, 13], write.Content.ToArray());
            await ShellOperationDispatcher.DispatchAsync(write, host, token);
            Assert.Null(await state.GetNextStep(token));
            await completion.WaitAsync(token);
            Assert.Equal(0, state.LastExitCode);
        }

        const int ParserCapacity = 64 * 1024;
        var inputBytes = first.Length + middle.Length + finalBytes;
        var initialCapacity = sizeKnown ? Math.Min(inputBytes, ParserCapacity) : ParserCapacity;
        var fits = inputBytes <= initialCapacity;
        Assert.Empty(admissions);
        Assert.Equal(new long[] { 0, initialCapacity, initialCapacity }, sourceBytesAtRead);
        var retained = fits ? initialCapacity : inputBytes;
        Assert.Equal(cancel ? 0 : retained, liveSourceBytes);
        Assert.Equal(3, host.ReadCallCount);
        Assert.Equal(0, host.OpenFileCount);
        Assert.DoesNotContain(scopes, scope => scope.OwnBytes >= first.Length);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);

        long SourceBackingBytes() => scopes.Where(scope => scope.OwnBytes >= first.Length).Sum(scope => scope.OwnBytes);

        static string Records(string assignment, int width) =>
            assignment + " #" + new string('x', width - assignment.Length - 3) + "\n"
            + string.Concat(Enumerable.Repeat("#" + new string('x', width - 2) + "\n", 127));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 4096)]
    [InlineData(false, 4096)]
    public async Task TerminalSourceWindowFillsTheParserBufferWithOneRead(bool terminated, int extraLines)
    {
        var governor = new MemoryGovernor(16 * 1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        using var owner = budget.CreateScope();
        await using var host = new TestHost { MemoryBudget = budget };
        var body = "probe(){ printf '%s' $'\\377\\r'; }\n" + string.Concat(Enumerable.Repeat("#\n", extraLines))
            + "#" + new string('x', PaddingBytes) + (terminated ? "\n" : "x");
        host.AddFile("/a", body);
        var beforeRead = 0L;
        host.BeforeRead = () => beforeRead = governor.ReservedBytes;
        var state = new Eval(Parser.Parse("source /a; probe; unset -f probe"));
        state.Memory.Budget = owner;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = deadline.Token;

        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep(token));
        await ShellOperationDispatcher.DispatchAsync(open, host, token);
        var read = Assert.IsType<ReadLineOperation>(await state.GetNextStep(token));
        var batch = Assert.IsType<RecordedScope>(read.DestinationBudget);
        await ShellOperationDispatcher.DispatchAsync(read, host, token);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep(token));

        Assert.Equal(read.FileDescriptor, close.Descriptor);
        Assert.Equal(1, host.ReadCallCount);
        Assert.Equal(0, batch.OwnBytes);
        var source = Assert.Single(scopes, scope => scope.OwnBytes >= PaddingBytes);
        Assert.Equal(body.Length, source.OwnBytes);
        Assert.Equal(body.Length, governor.PeakBytes - beforeRead);
        await ShellOperationDispatcher.DispatchAsync(close, host, token);

        var write = Assert.IsType<WriteOperation>(await state.GetNextStep(token));
        Assert.True(write.FileDescriptor == FileDescriptor.StdOut, Encoding.UTF8.GetString(write.Content.Span));
        Assert.Equal(new byte[] { 0xff, 13 }, write.Content.ToArray());
        // The source owns its displayed path after the descriptor closes and while the function runs.
        Assert.Equal(body.Length + MemoryCapacity.ArrayBackingBytes<byte>("/a"u8.Length), source.OwnBytes);
        Assert.Equal(0, host.OpenFileCount);
        await ShellOperationDispatcher.DispatchAsync(write, host, token);
        Assert.Null(await state.GetNextStep(token));
        await state.Completion;
        Assert.Equal(0, state.LastExitCode);
        Assert.Equal(0, source.OwnBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class WindowedSourceHost(int firstMaximum, int secondMaximum) : TestHost
    {
        public override ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request,
            CancellationToken cancellationToken)
        {
            var maximum = ReadCallCount switch
            {
                0 => firstMaximum,
                1 => secondMaximum,
                _ => request.MaximumBytes,
            };
            return base.ReadAsync(descriptor, new DescriptorReadRequest(request.MinimumBytes, Math.Min(maximum, request.MaximumBytes)),
                cancellationToken);
        }
    }

    public static IEnumerable<object[]> Lifetimes()
    {
        var cases = new (string Name, string Script, string A, string B, string[] Marks, int[] Owners)[]
        {
            ("normalized conditional definition", "source /a; printf @loaded; f; unset -f f; printf @retired",
                "f(){ [[ $(printf x) == x # inert ]] $( '\n ]]; }", "", ["@loaded", "@retired"], [1, 0]),
            ("shared definitions", "source /a; printf @loaded; unset -f f; printf @one; g; unset -f g; printf @retired",
                "f(){ :; }; g(){ :; }", "", ["@loaded", "@one", "@retired"], [1, 1, 0]),
            ("active unset", "source /a; f; printf @retired",
                "f(){ unset -f f; printf @active; }", "", ["@active", "@retired"], [1, 0]),
            ("active eval replacement", "source /a; f; printf @retired; f",
                "f(){ eval 'f(){ :; }'; printf @active; }", "", ["@active", "@retired"], [1, 0]),
            ("active source replacement", "source /a; f; printf @remaining; f; unset -f f; printf @retired",
                "f(){ source /b; printf @active; }", "f(){ :; }", ["@active", "@remaining", "@retired"], [2, 1, 0]),
            ("nested definition from another source", "source /a; printf @loaded; source /b; printf @nested; g; unset -f g; printf @retired",
                "f(){ g(){ :; }; }", "f; unset -f f", ["@loaded", "@nested", "@retired"], [1, 1, 0]),
            ("nested definition through tested loop", "source /a; f; printf @nested; g; unset -f g; printf @retired",
                "f(){ unset -f f; while :; do if :; then g(){ :; }; fi; break; done; }", "", ["@nested", "@retired"], [1, 0]),
            ("eval owns separate syntax", "source /a; printf @retired; f",
                "eval 'f(){ :; }'", "", ["@retired"], [0]),
            ("unreached definition", "source /a; printf @retired",
                "if false; then f(){ :; }; fi", "", ["@retired"], [0]),
            ("subshell inherited namespace", "source /a; (unset -f f); printf @parent; f; unset -f f; printf @retired",
                "f(){ :; }", "", ["@parent", "@retired"], [1, 0]),
            ("subshell local namespace", "(source /a; printf @child); printf @retired",
                "f(){ :; }", "", ["@child", "@retired"], [1, 0]),
            ("unused capture namespace", "source /a; value=$(:); printf @parent; unset -f f; printf @retired",
                "f(){ :; }", "", ["@parent", "@retired"], [1, 0]),
            ("invoked capture namespace", "source /a; value=$(f); printf @parent; unset -f f; printf @retired",
                "f(){ printf x; }", "", ["@parent", "@retired"], [1, 0]),
            ("invoked pipeline namespace", "source /a; f | :; printf @parent; unset -f f; printf @retired",
                "f(){ :; }", "", ["@parent", "@retired"], [1, 0]),
            ("unset pipeline namespaces", "source /a; { unset -f f; } | { unset -f f; }; printf @parent; unset -f f; printf @retired",
                "f(){ :; }", "", ["@parent", "@retired"], [1, 0]),
            ("pipeline local namespace", "source /a | :; printf @retired",
                "f(){ :; }", "", ["@retired"], [0]),
            ("active syntax in pipeline", "source /a; f; printf @retired",
                "f(){ unset -f f; { g(){ :; }; printf @child >/marker; } | :; }", "", ["@child", "@retired"], [1, 0]),
            ("prefixed eval invocation", "prefix=1 source /a; prefix=2 eval 'f'; printf @retired",
                "f(){ unset -f f; printf @active; }", "", ["@active", "@retired"], [1, 0]),
            ("attribute copies and readonly refusal", "source /a; export -f f; printf @exported; export -nf f; printf @ordinary; readonly -f f; source /a 2>/errors; printf @readonly",
                "f(){ :; }", "", ["@exported", "@ordinary", "@readonly"], [1, 1, 1]),
            ("return after publication", "source /a || :; printf @loaded; f; unset -f f; printf @retired",
                "f(){ :; }; return 7", "", ["@loaded", "@retired"], [1, 0]),
            ("late syntax after publication", "source /a 2>/errors || :; printf @loaded; f; unset -f f; printf @retired",
                "f(){ :; }\n(\n", "", ["@loaded", "@retired"], [1, 0])
        };
        foreach (var prepared in new[] { false, true })
        foreach (var sample in cases)
            yield return [prepared, sample.Name, sample.Script, sample.A, sample.B, sample.Marks, sample.Owners];
    }

    [Theory]
    [MemberData(nameof(Lifetimes))]
    public async Task SourceReservationsFollowLiveSyntax(
        bool prepared, string name, string script, string a, string b, string[] marks, int[] owners)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new TestHost { MemoryBudget = budget };
        host.AddFile("/a", a + "\n#" + new string('a', PaddingBytes) + "\n");
        if (b.Length > 0) host.AddFile("/b", b + "\n#" + new string('b', 2 * PaddingBytes) + "\n");
        var snapshots = new ConcurrentDictionary<string, long[]>();
        host.BeforeAppend = (_, content, _) =>
        {
            if (!content.IsEmpty && content.Span[0] == (byte)'@')
                snapshots[System.Text.Encoding.ASCII.GetString(content.Span)] = scopes
                    .Where(s => s.Depth > 1 && s.OwnBytes >= PaddingBytes)
                    .Select(s => s.OwnBytes).Order().ToArray();
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = budget.CreateScope();

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, Evaluation.EvaluationOptions.Default, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(marks.Length, snapshots.Count);
        for (var index = 0; index < marks.Length; index++)
        {
            Assert.True(snapshots.TryGetValue(marks[index], out var live), name + ": " + marks[index]);
            Assert.Equal(owners[index], live.Length);
        }
        if (name == "nested definition from another source")
            Assert.Equal(snapshots["@loaded"], snapshots["@nested"]);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("slice")]
    [InlineData("joined")]
    public async Task EvalRetainsOnlyAdmittedSourceUntilFunctionRetirement(string form)
    {
        const string definition = "f(){ printf active; }\n#";
        const int sourceLength = PaddingBytes + 1;
        var multipleOperands = form == "joined";
        var payload = new string('x', sourceLength - definition.Length - (multipleOperands ? 2 : 0));
        var evalOperands = multipleOperands ? """ "$definition" '' "$payload" """ : """ "$script" """;
        var evaluation = new Eval(Parser.Parse("eval --" + evalOperands
            + "; unset script definition payload; printf loaded; f; unset -f f; printf retired"));
        if (multipleOperands)
        {
            evaluation.SetVariable("definition", ShellValue.FromText(definition));
            evaluation.SetVariable("payload", ShellValue.FromText(payload));
        }
        else if (form == "slice")
        {
            const int offset = 64;
            var backing = new byte[sourceLength + 2 * offset];
            Encoding.UTF8.GetBytes(definition + payload, backing.AsSpan(offset, sourceLength));
            evaluation.SetVariable("script", ShellValue.FromBytes(backing.AsMemory(offset, sourceLength)));
        }
        else
            evaluation.SetVariable("script", ShellValue.FromText(definition + payload));

        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        using var owner = budget.CreateScope();
        await using var host = new TestHost { MemoryBudget = budget };
        var observed = new List<long>();
        host.BeforeAppend = (_, _, _) => observed.Add(scopes.Where(scope => scope.Depth > 1 && scope.OwnBytes >= sourceLength)
            .Sum(scope => scope.OwnBytes));

        var result = await new Engine().RunAsync(evaluation, host, owner, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("loadedactiveretired", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(new long[] { sourceLength, sourceLength, 0 }, observed);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "plain")]
    [InlineData(true, "plain")]
    [InlineData(false, "replace")]
    [InlineData(true, "replace")]
    [InlineData(false, "unset")]
    [InlineData(true, "unset")]
    [InlineData(false, "eval")]
    [InlineData(true, "eval")]
    [InlineData(false, "eval-unset")]
    [InlineData(true, "eval-unset")]
    [InlineData(false, "subshell")]
    [InlineData(true, "subshell")]
    public async Task RepeatedSourcesReuseTheSharedAllowance(bool prepared, string kind)
    {
        var body = kind switch
        {
            "plain" => ":",
            "replace" or "subshell" => "f(){ :; }",
            "unset" => "f(){ :; }; unset -f f",
            "eval" => "eval 'f(){ :; }'",
            "eval-unset" => "eval 'f(){ :; }; unset -f f'",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var governor = new MemoryGovernor(65536);
        await using var host = new TestHost { MemoryBudget = governor };
        host.AddFile("/unit", body + "\n#" + new string('a', 2048) + "\n");
        var invocation = kind == "subshell" ? "(source /unit)" : "source /unit";
        var script = $"i=0; while ((i<128)); do {invocation} || break; ((i+=1)); done; unset -f f; printf '%s' \"$i\"";
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = governor.CreateScope();

        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancel.Token)
            : await BashScript.RunAsync(host, script, Evaluation.EvaluationOptions.Default, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("128", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ActiveSourceSurvivesPendingWorkAndCancellation(bool prepared, bool close, bool cancel)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new PendingHost(close, "pending"u8.ToArray()) { MemoryBudget = budget };
        host.AddFile("/a", "f(){ unset -f f; printf pending; }\n#" + new string('a', PaddingBytes) + "\n");
        const string Script = "source /a; f >/out; :";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = budget.CreateScope();
        var running = prepared
            ? new Engine().RunAsync(new Eval(Parser.Parse(Script)), host, owner, cancellation.Token)
            : BashScript.RunAsync(host, Script, Evaluation.EvaluationOptions.Default, cancellation.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
            if (cancel)
            {
                cancellation.Cancel();
                Assert.False(running.IsCompleted);
                Assert.Single(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
            }
        }
        finally
        {
            host.Release.TrySetResult();
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
            else
                Assert.Equal(0, (await running).ExitCode);
        }
        Assert.DoesNotContain(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerboseKeepsTheLoadedSourceThroughACanceledWrite(bool prepared)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        var body = "#" + new string('a', PaddingBytes) + "\nprintf unreached\n";
        await using var host = new PendingHost(close: false, Encoding.UTF8.GetBytes(body)) { MemoryBudget = budget };
        host.AddFile("/child", body);
        const string script = "set -v; . ./child";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = budget.CreateScope();
        var running = prepared
            ? new Engine().RunAsync(new Eval(Parser.Parse(script)), host, owner, cancellation.Token)
            : BashScript.RunAsync(host, script, Evaluation.EvaluationOptions.Default, cancellation.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var admitted = Assert.Single(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(admitted.OwnBytes >= PaddingBytes);
        }
        finally
        {
            host.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        }

        Assert.DoesNotContain(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChildBorrowsTheActiveSourceThroughACanceledCallerWrite(bool prepared)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new PendingHost(close: false, "0 source\n"u8.ToArray()) { MemoryBudget = budget };
        host.AddFile("/a", "(caller >/out)\n#" + new string('a', PaddingBytes) + "\n");
        const string Script = "source /a";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var owner = budget.CreateScope();
        var running = prepared
            ? new Engine().RunAsync(new Eval(Parser.Parse(Script)), host, owner, cancellation.Token)
            : BashScript.RunAsync(host, Script, Evaluation.EvaluationOptions.Default, cancellation.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var active = Assert.Single(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Assert.True(active.OwnBytes >= PaddingBytes);
        }
        finally
        {
            host.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        }

        Assert.DoesNotContain(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        // A prepared evaluation can leave retained values with its supplied owner.
        Assert.Equal(prepared ? Assert.IsType<RecordedScope>(owner).OwnBytes : 0L, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedNestedDispatchRetiresUndispatchedNamespaces(bool capture)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new TestHost { MemoryBudget = budget };
        host.AddFile("/a", "f(){ :; }\n#" + new string('a', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        var command = capture ? "value=$(f)" : "f | :";
        var evaluation = new Eval(Parser.Parse($"source /a; {command}; unset -f f"));
        evaluation.Memory.Budget = owner;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var refused = false;
        while (await evaluation.GetNextStep(cancel.Token) is { } operation)
        {
            if (operation is RunPipelineEvaluations pipeline)
            {
                Assert.False(capture);
                Assert.All(pipeline.Stages, stage => Assert.False(stage.Evaluation.HasStarted));
                foreach (var stage in pipeline.Stages)
                    foreach (var descriptor in stage.DescriptorsToClose)
                        Assert.Null(await host.CloseDescriptorAsync(descriptor));
                operation.ClaimExecution();
                Assert.True(pipeline.TrySetException(new MemoryBudgetExceededException()));
                refused = true;
            }
            else if (operation is RunCapturedEvaluation captured)
            {
                Assert.True(capture);
                Assert.False(captured.Stage.Evaluation.HasStarted);
                foreach (var descriptor in captured.Stage.DescriptorsToClose)
                    Assert.Null(await host.CloseDescriptorAsync(descriptor));
                operation.ClaimExecution();
                Assert.True(captured.TrySetException(new MemoryBudgetExceededException()));
                refused = true;
            }
            else
                await ShellOperationDispatcher.DispatchAsync(operation, host, cancel.Token);
        }
        await evaluation.Completion;

        Assert.True(refused);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.DoesNotContain(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public async Task RefusedPipelinePreparationClosesBothEndsAndRetiresTheSourceBeforeCallerDisposal()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new TestHost();
        host.AddFile("/a", "f(){ printf unexpected; }\n#" + new string('a', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        var evaluation = new Eval(Parser.Parse("source /a; f | :; status=$?; unset -f f; printf '%s' \"$status\""));
        evaluation.Memory.Budget = owner;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IMemoryReservation? hold = null;
        var endpoints = new HashSet<FileDescriptor>();
        var closed = new HashSet<FileDescriptor>();
        try
        {
            while (await evaluation.GetNextStep(deadline.Token) is { } operation)
            {
                Assert.IsNotType<RunPipelineEvaluations>(operation);
                if (operation is CreatePipeOperation create)
                {
                    Assert.Empty(endpoints);
                    Assert.Single(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
                    var result = await host.CreatePipeAsync(deadline.Token, deadline.Token);
                    var pipe = Assert.IsType<PipeDescriptors>(result.Descriptors);
                    endpoints.Add(pipe.Read);
                    endpoints.Add(pipe.Write);
                    // At the pending pipe operation the evaluator has finished its prior admissions.
                    // Refuse the next preparation growth while giving the host its ordinary budget.
                    evaluation.Memory.ReleaseUnusedCapacity();
                    hold = governor.Reserve(governor.LimitBytes - governor.ReservedBytes);
                    create.ClaimExecution();
                    create.SetResult(result);
                    continue;
                }
                if (operation is CloseDescriptorOperation close && endpoints.Contains(close.Descriptor))
                {
                    Assert.True(closed.Add(close.Descriptor));
                    Assert.Single(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
                    if (closed.Count == endpoints.Count)
                        hold?.Dispose();
                }
                await ShellOperationDispatcher.DispatchAsync(operation, host, deadline.Token);
            }
            await evaluation.Completion;
        }
        finally { hold?.Dispose(); }

        Assert.Equal(2, endpoints.Count);
        Assert.True(endpoints.SetEquals(closed));
        Assert.Equal("1", host.StandardOutput.TextContent);
        Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        Assert.DoesNotContain(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedRootRetainsOnlySourceOwnersNeededBySurvivingDefinitions(bool trap)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        await using var host = new TestHost { MemoryBudget = budget };
        var body = trap ? "f(){ unset -f f; trap 'g(){ :; }' EXIT; exit; }" : "f(){ :; }";
        host.AddFile("/a", body + "\n#" + new string('a', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(new Eval(Parser.Parse(trap ? "source /a; f" : "source /a")),
            host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(trap ? 0 : 1, scopes.Count(s => s.Depth > 1 && s.OwnBytes >= PaddingBytes));
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("type", null)]
    [InlineData("type -a", null)]
    [InlineData("type -t", "function\nfile\n")]
    [InlineData("type -at", "function\nfile\nfile\n")]
    [InlineData("type -p", "/bin/other\n")]
    [InlineData("command -v", "f\n/bin/other\n")]
    public async Task CommandQueryKeepsTheSelectedOutputAcrossHostLookup(string query, string? expectedOutput)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        var evaluation = new Eval(Parser.Parse($"source /a; {query} f other; unset -f f"));
        var replacement = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ printf new; }").Commands));
        var liveAtLookup = -1;
        var liveAtWrite = -1;
        await using var host = new LookupHost(() =>
        {
            evaluation.SetExportedFunction(replacement);
            liveAtLookup = scopes.Count(s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        }, () => liveAtWrite = scopes.Count(s => s.Depth > 1 && s.OwnBytes >= PaddingBytes)) { MemoryBudget = budget };
        host.AddFile("/a", "f(){ printf old; }\n#" + new string('a', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        var stdout = host.GetOutput(FileDescriptor.StdOut);
        if (expectedOutput == null)
        {
            Assert.Equal(1, liveAtLookup);
            Assert.Equal(1, liveAtWrite);
            Assert.Contains("old", stdout);
            Assert.DoesNotContain("new", stdout);
        }
        else
        {
            Assert.Equal(0, liveAtLookup);
            Assert.Equal(0, liveAtWrite);
            Assert.Equal(expectedOutput, stdout);
        }
        Assert.Empty(host.GetOutput(FileDescriptor.StdErr));
        Assert.DoesNotContain(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData("export -f a", "-Fx", "declare -fx a\ndeclare -fx z\n")]
    [InlineData("readonly -f a z", "-Fr", "declare -fr a\n")]
    public async Task FunctionListingChecksReachedAttributesAfterHostWrites(string attributes, string filter, string expected)
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        var evaluation = new Eval(Parser.Parse($"a(){{ :; }}\nsource /definitions\n{attributes}\ndeclare {filter}\n"));
        var replacement = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("z(){ :; }").Commands));
        var inserted = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("b(){ :; }").Commands));
        await using var host = new TestHost { MemoryBudget = budget };
        host.AddFile("/definitions", "z(){ :; }\n#" + new string('z', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        var updated = false;
        host.BeforeAppend = (descriptor, _, _) =>
        {
            if (descriptor != FileDescriptor.StdOut || updated)
                return;
            updated = true;
            Assert.Single(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
            evaluation.SetExportedFunction(replacement);
            evaluation.SetExportedFunction(inserted);
            Assert.DoesNotContain(scopes, scope => scope.Depth > 1 && scope.OwnBytes >= PaddingBytes);
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.True(updated);
        Assert.Equal(expected, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class LookupHost(Action lookup, Action beforeWrite) : MockFileSystem
    {
        public override Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
            IReadOnlyList<string> names, ShellPath directory,
            IReadOnlyList<EnvironmentVariable> environment, CancellationToken cancellationToken)
        {
            lookup();
            return Task.FromResult<IReadOnlyList<CommandLookupResult>>(names
                .Select(name => (CommandLookupResult)new CommandLookupResult.Found("/bin/" + name)).ToArray());
        }

        public override ValueTask<DescriptorWriteResult> WriteAsync(
            FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (descriptor == FileDescriptor.StdOut)
                beforeWrite();
            return base.WriteAsync(descriptor, content, cancellationToken);
        }
    }

    private sealed class PendingHost(bool close, ReadOnlyMemory<byte> marker) : MockFileSystem
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileDescriptor? _output;

        public override async ValueTask<DescriptorWriteResult> WriteAsync(
            FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (content.Span.SequenceEqual(marker.Span))
            {
                _output = descriptor;
                if (!close)
                {
                    Started.TrySetResult();
                    await Release.Task.ConfigureAwait(false);
                }
            }
            // Completion consumes the borrowed payload even when cancellation arrived meanwhile.
            return await base.WriteAsync(descriptor, content, CancellationToken.None).ConfigureAwait(false);
        }

        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            if (close && descriptor == _output)
            {
                Started.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            return await base.CloseDescriptorAsync(descriptor).ConfigureAwait(false);
        }
    }

    // Observe actual budget owners, not evaluator fields or GC reachability. Host effects
    // keep their own allowance; the large child admissions here are the synthetic sources.
    private class RecordingBudget(IMemoryBudget inner, int depth, ConcurrentBag<RecordedScope> scopes) : IMemoryBudget
    {
        private readonly ConcurrentBag<IMemoryReservation> _reservations = [];
        public int Depth => depth;
        public IEnumerable<IMemoryReservation> Reservations => _reservations;
        public long OwnBytes => _reservations.Sum(r => r.Bytes);
        public Action<long>? ReservationAdmitted { get; set; }
        public IMemoryScope CreateScope()
        {
            var scope = new RecordedScope(inner.CreateScope(), depth + 1, scopes);
            scopes.Add(scope);
            return scope;
        }
        public bool TryReserve(long bytes, [NotNullWhen(true)] out IMemoryReservation? reservation)
        {
            if (!inner.TryReserve(bytes, out reservation)) return false;
            _reservations.Add(reservation);
            ReservationAdmitted?.Invoke(bytes);
            return true;
        }
    }

    private sealed class RecordedScope(IMemoryScope inner, int depth, ConcurrentBag<RecordedScope> scopes)
        : RecordingBudget(inner, depth, scopes), IMemoryScope
    {
        public void Dispose() => inner.Dispose();
    }
}
