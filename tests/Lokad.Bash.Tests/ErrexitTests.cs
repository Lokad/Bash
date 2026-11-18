using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ErrexitTests
{
    private const string ReportOption = "flag(){ if [[ -o errexit ]]; then printf on; else printf off; fi; }; ";

    [Theory]
    [InlineData("-e", "+e")]
    [InlineData("-e", "+o errexit")]
    [InlineData("-o errexit", "+e")]
    [InlineData("-o errexit", "+o errexit")]
    public async Task OptionSpellingsEnableAndDisableTheSameState(string enable, string disable)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync($"set {enable}; printf '%s' \"$-\"; set {disable}; false; printf ':continued:%s' \"${{-}}\"",
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e:continued:", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Fact]
    public async Task InspectionReportsSupportedStateWithoutReplacingPositionals()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(
            "set -- keep; set -o; set -e; set +o; set +e; set -o; printf '%s' \"$1\"", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("errexit\toff\nset -o errexit\nerrexit\toff\nkeep", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
    }

    [Theory]
    [InlineData("-u")]
    [InlineData("+x")]
    [InlineData("-o unknown")]
    [InlineData("+o pipefail")]
    public async Task UnsupportedOptionsReturnAnErrorAndDiagnostic(string option)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("set " + option, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(host.StandardOutput.Content.ToArray());
        Assert.StartsWith("set: ", host.StandardError.TextContent);
        Assert.Contains("option", host.StandardError.TextContent);
    }

    [Theory]
    [InlineData("set +e; f(){ local -; set -e; flag; }; f; flag", 0, "onoff")]
    [InlineData("set -e; f(){ local -; set +o errexit; flag; }; f; flag; false; printf unreached", 1, "offon")]
    [InlineData("set +e; f(){ set -e; local -; set +e; flag; }; f; flag", 0, "offon")]
    [InlineData("set +e; f(){ local -; set -e; local -; flag; }; f; flag", 0, "onoff")]
    [InlineData("set +e; g(){ local -; set +e; flag; }; f(){ local -; set -e; g; flag; }; f; flag", 0, "offonoff")]
    [InlineData("""set +e; f(){ local -; set -e; false; flag; return 7; }; if f; then printf taken; else printf '%s:' "$?"; fi; flag""", 0, "on7:off")]
    [InlineData("""set +e; f(){ local -; set -e; flag; return 7; }; ! f; printf '%s:' "$?"; flag""", 0, "on0:off")]
    [InlineData("set +e; f(){ prefix=yes eval 'local -; set -e'; flag; }; f; flag", 0, "onoff")]
    [InlineData("set +e; f(){ (local -; set -e; flag); set -e; }; f; flag", 0, "onon")]
    [InlineData("""set +e; f(){ x=$(local -; set -e; flag); printf '<%s>' "$x"; set -e; }; f; flag""", 0, "<on>on")]
    [InlineData("set +e; f(){ local -; set -e; g(){ local -; set +e; flag; }; g | cat; flag; }; f; flag", 0, "offonoff")]
    [InlineData("""set +e; f(){ set -- -; command local "$@"; set -e; flag; }; f; flag""", 0, "onoff")]
    [InlineData("""set +e; f(){ builtin local -- "-" value=kept; set -e; printf '<%s>' "$value"; }; f; flag""", 0, "<kept>off")]
    [InlineData("set +e; f(){ local -f -; set -e; flag; }; f; flag", 0, "onoff")]
    public async Task LocalDashRestoresSavedOptionsAtTheOwningFunctionBoundary(string script, int status, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption + script, CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task LocalDashReachedThroughPrefixedSourceOutlivesTheSourceReturn()
    {
        await using var host = new TestHost();
        host.AddFile("/unit", "local -; set -e; return 0\n");
        var result = await host.RunAsync(ReportOption
            + "set +e; f(){ prefix=yes source ./unit; flag; }; f; flag", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("onoff", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("set -e; exit 7", 7, "7:on")]
    [InlineData("set -e; false", 1, "1:on")]
    [InlineData("set -e; return 0", 0, "0:off")]
    public async Task ExitTrapObservesTheOptionStateAtShellTermination(string body, int status, string output)
    {
        var memory = new MemoryGovernor(128 * 1024);
        await using var host = new TestHost { MemoryBudget = memory };
        var result = await host.RunAsync(
            ReportOption + """set +e; trap 'printf '%s:' "$?"; flag' EXIT; f(){ local -; """ + body + "; }; f",
            CancellationToken.None);

        Assert.Equal(status, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal(0, memory.ReservedBytes);
    }

    [Fact]
    public async Task LocalDashInspectionFollowsFunctionAndChildScope()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(ReportOption + """
            set +e
            g(){ local -p -; printf 'g=%s;' "$?"; declare -p -; }
            f(){ local value=kept -; local; g; (local -p -); set -e; }
            f
            flag
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("local -\ndeclare -- value=\"kept\"\ng=1;declare -- -\ndeclare -- -\noff", host.StandardOutput.TextContent);
        Assert.Equal("local: -: not found\n", host.StandardError.TextContent);
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task DeclareNamedInspectionCanSeeTheSnapshotWithoutListingIt()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("""
            f() {
                local -
                declare -p | while IFS= read -r line; do
                    if [[ $line == 'declare -- -' ]]; then printf unexpected; fi
                done
                declare -p -
            }
            f
            """, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("declare -- -\n", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ErrexitWaitsForDescriptorCleanupAndRunsExitTrapOnce()
    {
        var memory = new MemoryGovernor(64 * 1024);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new TestOperationVisitor([])
        {
            MemoryBudget = memory,
            RecordAppends = true,
            BeforeDescriptorClose = _ => closeEntered.SetResult()
        };
        host.EnqueueOpenFileDescriptor(9);
        host.EnqueueAppendResult(DescriptorWriteResult.Open);
        host.EnqueueCloseDescriptorTask(closeComplete.Task);
        var running = BashScript.RunAsync(host,
            "set -e; exec 3>/out; trap 'printf trap:%s \"$?\"' EXIT; false; printf wrong",
            EvaluationOptions.Default, CancellationToken.None);
        try
        {
            await closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(running.IsCompleted);
            Assert.True(memory.ReservedBytes > 0);
            var output = Assert.Single(host.Appends);
            Assert.Equal(FileDescriptor.StdOut, output.Descriptor);
            Assert.Equal("trap:1"u8.ToArray(), output.Content);
        }
        finally
        {
            closeComplete.TrySetResult();
        }

        Assert.Equal(1, (await running.WaitAsync(TimeSpan.FromSeconds(5))).ExitCode);
        Assert.Equal(new FileDescriptor(9), Assert.Single(host.ClosedDescriptors));
        Assert.Single(host.Appends);
        Assert.Equal(0, memory.ReservedBytes);
    }
}
