using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
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

        IMemoryScope? childMemory = null;
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

            // This fixture supports only the explicit -c form exercised by these tests.
            if (command.Arguments.Count < 2 || !command.Arguments[0].Bytes.Span.SequenceEqual("-c"u8))
                throw new InvalidOperationException("The export fixture requires bash -c source [zero [arguments]].");
            var arguments = command.Arguments.Skip(2).ToArray();
            var child = ShellEvaluationStartup.CreateInherited(command, Parser.Parse(command.Arguments[1].Bytes),
                arguments.Length > 0 ? arguments[0] : command.ArgumentZeroOverride ?? ShellValue.FromText(name),
                arguments.Skip(1).ToArray());
            childMemory = MemoryBudget.CreateScope();
            foreach (var exported in command.Functions)
            {
                if (Parser.Parse(exported.Source).Commands is not [FunctionDefinition definition])
                    throw new InvalidOperationException("Export did not contain one function definition.");
                child.SetExportedFunction(definition);
            }
            var result = await new Engine().RunAsync(child, this, childMemory, cancellationToken);
            return new CommandExecutionResult.Completed(result.ExitCode);
        }
        finally
        {
            try
            {
                foreach (var descriptor in command.DescriptorsToClose)
                    await CloseDescriptorAsync(descriptor);
            }
            finally { childMemory?.Dispose(); }
        }
    }
}
