using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
        await using var host = new PendingHost(close) { MemoryBudget = budget };
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

    [Fact]
    public async Task CommandQueryRetainsItsSelectedDefinitionAcrossHostLookup()
    {
        var governor = new MemoryGovernor(1024 * 1024);
        var scopes = new ConcurrentBag<RecordedScope>();
        var budget = new RecordingBudget(governor, depth: 0, scopes);
        var evaluation = new Eval(Parser.Parse("source /a; type f other; unset -f f"));
        var replacement = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ printf new; }").Commands));
        var liveAtLookup = -1;
        await using var host = new LookupHost(() =>
        {
            evaluation.SetExportedFunction(replacement);
            liveAtLookup = scopes.Count(s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        }) { MemoryBudget = budget };
        host.AddFile("/a", "f(){ printf old; }\n#" + new string('a', PaddingBytes) + "\n");
        using var owner = budget.CreateScope();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancel.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, liveAtLookup);
        Assert.Contains("old", host.GetOutput(FileDescriptor.StdOut));
        Assert.DoesNotContain("new", host.GetOutput(FileDescriptor.StdOut));
        Assert.Empty(host.GetOutput(FileDescriptor.StdErr));
        Assert.DoesNotContain(scopes, s => s.Depth > 1 && s.OwnBytes >= PaddingBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class LookupHost(Action lookup) : MockFileSystem
    {
        public override Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
            IReadOnlyList<string> names, ShellPath directory,
            IReadOnlyList<EnvironmentVariable> environment, CancellationToken cancellationToken)
        {
            lookup();
            return Task.FromResult<IReadOnlyList<CommandLookupResult>>(names
                .Select(name => (CommandLookupResult)new CommandLookupResult.Found("/bin/" + name)).ToArray());
        }
    }

    private sealed class PendingHost(bool close) : MockFileSystem
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private FileDescriptor? _output;

        public override async ValueTask<DescriptorWriteResult> WriteAsync(
            FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (content.Span.SequenceEqual("pending"u8))
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
        public long OwnBytes => _reservations.Sum(r => r.Bytes);
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
            return true;
        }
    }

    private sealed class RecordedScope(IMemoryScope inner, int depth, ConcurrentBag<RecordedScope> scopes)
        : RecordingBudget(inner, depth, scopes), IMemoryScope
    {
        public void Dispose() => inner.Dispose();
    }
}
