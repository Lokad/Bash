using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

/// <summary>Executes synthetic export consumers without retaining borrowed command payloads.</summary>
internal class ExportTestHost : TestHost
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public Action<CommandInvocation>? BeforeCommand { get; set; }
    public Func<CommandInvocation, CancellationToken, Task>? DuringCommand { get; set; }

    public override async Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> names, ShellPath directory,
        IReadOnlyList<EnvironmentVariable> environment, CancellationToken cancellationToken)
    {
        var results = (await base.ResolveCommandsAsync(names, directory, environment, cancellationToken)).ToArray();
        for (var index = 0; index < names.Count; index++)
            if (names[index] is "true" or "/usr/bin/true" or "/usr/bin/bash")
                results[index] = new CommandLookupResult.Found(names[index]);
        return results;
    }

    public override async Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken cancellationToken)
    {
        var name = command.CommandName;
        if (name is not ("true" or "/usr/bin/true" or "bash" or "/usr/bin/bash"))
            return await base.RunCommandAsync(command, reporting, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeCommand?.Invoke(command);
            if (DuringCommand is { } during)
                await during(command, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (name is "true" or "/usr/bin/true")
            {
                Interlocked.Increment(ref _calls);
                return new CommandExecutionResult.Completed(0);
            }

            return await RunFixtureShellAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var descriptor in command.DescriptorsToClose)
                await CloseDescriptorAsync(descriptor);
        }
    }
}
