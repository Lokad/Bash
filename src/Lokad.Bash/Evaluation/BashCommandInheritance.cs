using System.Collections.Generic;
using Lokad.Bash.Commands;
using Lokad.Bash.Helpers;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>Immutable Bash descriptor inheritance carried independently of command ownership.</summary>
/// <remarks>The bindings are immutable snapshots; tools never receive mutable evaluator state.</remarks>
internal readonly record struct BashCommandInheritance(DescriptorBindings.Snapshot Bindings,
    BashCommandInheritance.FunctionImports? Imports)
{
    /// <summary>Associates import order and damage with the original serialized function list.</summary>
    internal sealed record FunctionImports(IReadOnlyList<ExportedFunction> Functions, ImportEntry[] Entries);

    internal readonly record struct ImportEntry(int Index, FunctionName? DamagedName);

    /// <summary>Captures the evaluator's mappings before the invocation crosses the host boundary.</summary>
    internal static CommandInvocation CreateInheritedCommand(
        string commandName,
        ShellValue? argumentZero,
        IReadOnlyList<ShellValue> arguments,
        ShellPath currentDirectory,
        IReadOnlyList<EnvironmentVariable> environment,
        FileDescriptor stdIn,
        FileDescriptor stdOut,
        FileDescriptor stdErr,
        ref DescriptorHandleCollector borrowedDescriptors,
        DescriptorBindings.Snapshot inheritedBindings,
        IReadOnlyList<ExportedFunction> functions,
        FunctionImports? imports)
    {
        var bindings = inheritedBindings.WithStandardStreams(stdIn, stdOut, stdErr);
        // Select command-owned streams before adding handles that only the nested interpreter borrows.
        var owned = CommandContext.BuildDescriptorsToClose(stdIn, stdOut, stdErr, borrowedDescriptors.Descriptors);
        bindings.AddReferencedHandlesTo(ref borrowedDescriptors);
        return new CommandInvocation(commandName, arguments, currentDirectory, environment, stdIn, stdOut, stdErr,
            new CommandContext<BashCommandInheritance>(owned, borrowedDescriptors.Descriptors,
                new BashCommandInheritance(bindings, imports)))
        { ArgumentZeroOverride = argumentZero, Functions = functions };
    }
}
