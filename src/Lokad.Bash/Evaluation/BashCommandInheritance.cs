using System.Collections.Generic;
using Lokad.Bash.Commands;

namespace Lokad.Bash.Evaluation;

/// <summary>Immutable Bash inheritance carried by commands, independently of ownership and copy credit.</summary>
/// <remarks>The bindings are immutable snapshots; tools never receive mutable evaluator state.</remarks>
internal readonly record struct BashCommandInheritance(DescriptorBindings.Snapshot Bindings, ExpansionBudget ExpansionBudget)
{
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
        IReadOnlyList<FileDescriptor> borrowedDescriptors,
        DescriptorBindings.Snapshot inheritedBindings,
        ExpansionBudget expansionBudget,
        long reservedArgumentCharacters,
        IReadOnlyList<ExportedFunction> functions)
    {
        var bindings = inheritedBindings.WithStandardStreams(stdIn, stdOut, stdErr);
        var nestedBorrowed = new List<FileDescriptor>(borrowedDescriptors);
        bindings.AddReferencedHandlesTo(nestedBorrowed);
        return CommandInvocation.CreateWithInheritance(commandName, arguments, currentDirectory, environment,
            stdIn, stdOut, stdErr, borrowedDescriptors, nestedBorrowed,
            expansionBudget.AllocationBudget, reservedArgumentCharacters, new BashCommandInheritance(bindings, expansionBudget),
            argumentZero, functions);
    }
}
