using System.Collections.Generic;
using Lokad.Bash.Commands;

namespace Lokad.Bash.Evaluation;

/// <summary>Immutable Bash descriptor inheritance carried independently of command ownership.</summary>
/// <remarks>The bindings are immutable snapshots; tools never receive mutable evaluator state.</remarks>
internal readonly record struct BashCommandInheritance(DescriptorBindings.Snapshot Bindings)
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
        IReadOnlyList<ExportedFunction> functions)
    {
        var bindings = inheritedBindings.WithStandardStreams(stdIn, stdOut, stdErr);
        var nestedBorrowed = new List<FileDescriptor>(borrowedDescriptors);
        bindings.AddReferencedHandlesTo(nestedBorrowed);
        return CommandInvocation.CreateWithInheritance(commandName, arguments, currentDirectory, environment,
            stdIn, stdOut, stdErr, borrowedDescriptors, nestedBorrowed,
            new BashCommandInheritance(bindings),
            argumentZero, functions);
    }
}
