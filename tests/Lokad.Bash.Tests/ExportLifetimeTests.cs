using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ExportLifetimeTests
{
    [Theory]
    [InlineData(32 * 1024, false)]
    [InlineData(256 * 1024, true)]
    public async Task ImportedDefinitionsUseTheBudgetInstalledBeforeExecution(int allowance, bool admitted)
    {
        const int BodyBytes = 64 * 1024;
        var governor = new MemoryGovernor(allowance);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        var evaluation = new Eval(Parser.Parse("/usr/bin/true; printf '%s' \"$?\""));
        var definition = Assert.IsType<FunctionDefinition>(Assert.Single(
            Parser.Parse("f(){ : '" + new string('x', BodyBytes) + "'; }").Commands));
        // Hosts import before Engine replaces the evaluator's initial placeholder budget.
        evaluation.SetExportedFunction(definition);
        host.BeforeCommand = _ => Assert.True(governor.ReservedBytes >= BodyBytes);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await new Engine().RunAsync(evaluation, host, owner, cancellation.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(admitted ? "0" : "1", host.StandardOutput.TextContent);
        Assert.Equal(admitted ? 1 : 0, host.Calls);
        if (admitted)
        {
            Assert.Empty(host.StandardError.Content.ToArray());
            Assert.True(governor.ReservedBytes >= BodyBytes);
        }
        else
            Assert.Contains("shared memory budget exhausted", host.StandardError.TextContent);
        var replacement = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }").Commands));
        evaluation.SetExportedFunction(replacement);
        Assert.True(governor.ReservedBytes < BodyBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, "replace")]
    [InlineData(true, "replace")]
    [InlineData(false, "unset")]
    [InlineData(true, "unset")]
    [InlineData(false, "unchanged")]
    [InlineData(true, "unchanged")]
    [InlineData(false, "attributes")]
    [InlineData(true, "attributes")]
    public async Task RepeatedExportsUseLiveMemoryRatherThanTheirHistory(bool prepared, string kind)
    {
        const int Count = 8193;
        var governor = new MemoryGovernor(64 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        var body = kind switch
        {
            "replace" => "f(){ :; }; export -f f; /usr/bin/true",
            "unset" => "f(){ :; }; export -f f; /usr/bin/true; unset -f f",
            "unchanged" => "/usr/bin/true",
            "attributes" => "export -fn f; export -f f; /usr/bin/true",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var source = $"f(){{ :; }}; export -f f; i=0; while ((i<{Count})); do {body} || break; ((i+=1)); done; unset -f f; printf '%s' \"$i\"";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Count.ToString(System.Globalization.CultureInfo.InvariantCulture), host.StandardOutput.TextContent);
        Assert.Equal(Count, host.Calls);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstPipelinePublicationKeepsTheCreatorsStorageAdmitted(bool prepared, bool concurrent)
    {
        const int BodyBytes = 16 * 1024;
        var governor = new MemoryGovernor(256 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new ExportTestHost { MemoryBudget = owner };
        var snapshots = new Dictionary<string, long>();
        host.BeforeAppend = (_, bytes, _) =>
        {
            if (bytes.Span.SequenceEqual("@published"u8) || bytes.Span.SequenceEqual("@reused"u8)
                || bytes.Span.SequenceEqual("@retired"u8))
                snapshots[System.Text.Encoding.ASCII.GetString(bytes.Span)] = governor.ReservedBytes;
        };
        var stages = concurrent ? "/usr/bin/true | /usr/bin/true" : "/usr/bin/true | :";
        var source = "f(){ : '" + new string('x', BodyBytes) + "'; }; export -f f; " + stages
            + "; printf @published; /usr/bin/true; printf @reused; unset -f f; printf @retired";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = prepared
            ? await new Engine().RunAsync(new Eval(Parser.Parse(source)), host, owner, cancellation.Token)
            : await BashScript.RunAsync(host, source, EvaluationOptions.Default, cancellation.Token);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.True(snapshots["@published"] >= BodyBytes);
        Assert.True(snapshots["@reused"] >= BodyBytes);
        Assert.True(snapshots["@retired"] + BodyBytes <= snapshots["@published"]);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public async Task ReplacedExportsStayAdmittedUntilPendingUsersAndCleanupEnd(bool close, bool cancel, bool fail, bool conditional)
    {
        const int BodyBytes = 16 * 1024;
        var governor = new MemoryGovernor(1024 * 1024);
        using var owner = governor.CreateScope();
        await using var host = new PendingHost(close) { MemoryBudget = owner };
        var body = conditional ? "[[ '" + new string('x', BodyBytes) + "' == x # inert ]] $( '\n ]]"
            : ": '" + new string('x', BodyBytes) + "'";
        var source = "f(){ " + body + "; }; export -f f; /usr/bin/true >/out";
        Assert.True(Parser.TryParse(source, out var parsed, out var error), error?.Message);
        var evaluation = new Eval(parsed);
        var replacement = Assert.IsType<FunctionDefinition>(Assert.Single(Parser.Parse("f(){ :; }").Commands));
        byte[] expected = [];
        host.BeforeCommand = command =>
        {
            expected = Assert.Single(command.Functions).Source.ToArray();
            evaluation.SetExportedFunction(replacement);
        };
        host.DuringCommand = async (command, token) =>
        {
            if (!close)
            {
                host.Started.TrySetResult();
                await host.Release.Task;
                Assert.Equal(expected, Assert.Single(command.Functions).Source.ToArray());
                token.ThrowIfCancellationRequested();
                if (fail) throw new InvalidOperationException("Synthetic command failure.");
            }
        };
        host.FailClose = fail && close;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = new Engine().RunAsync(evaluation, host, owner, cancellation.Token);
        try
        {
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(governor.ReservedBytes >= BodyBytes);
            if (cancel)
            {
                cancellation.Cancel();
                Assert.False(running.IsCompleted);
                Assert.True(governor.ReservedBytes >= BodyBytes);
            }
        }
        finally
        {
            host.Release.TrySetResult();
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
            else if (fail)
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await running);
            else
                Assert.Equal(0, (await running).ExitCode);
        }
        Assert.True(governor.ReservedBytes < BodyBytes);
        Assert.Equal(0, host.OpenFileCount);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    private sealed class PendingHost(bool close) : ExportTestHost
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailClose { get; set; }
        public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
        {
            if (close && descriptor.Id > FileDescriptor.StdErr.Id)
            {
                Started.TrySetResult();
                await Release.Task;
            }
            var result = await base.CloseDescriptorAsync(descriptor);
            if (FailClose) throw new InvalidOperationException("Synthetic close failure.");
            return result;
        }
    }
}
