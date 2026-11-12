using System.Collections.Generic;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash;

/// <summary>Translates command inheritance into the values required to start a Bash evaluation.</summary>
public static class ShellEvaluationStartup
{
    /// <summary>Starts on the same host with captured bindings, borrowed handles and environment.</summary>
    public static ShellEvaluation CreateInherited(
        CommandInvocation command,
        ParsedScript script,
        ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters)
    {
        var bindings = (command.TryGetInheritance<BashCommandInheritance>(out var inherited)
                ? inherited.Bindings : DescriptorBindings.Snapshot.Standard)
            .WithStandardStreams(command.StdIn, command.StdOut, command.StdErr);
        return new ShellEvaluation(script, new EvaluationOptions(0, command.CurrentDirectory),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.FromInheritedBindings(bindings), command.BorrowedDescriptors),
            command.Environment, argumentZero, positionalParameters);
    }

    /// <summary>Starts with standard host descriptors and an independent environment.</summary>
    /// <remarks>The caller installs the new host's memory owner when running the evaluation.</remarks>
    public static ShellEvaluation CreateForNewHost(
        ParsedScript script,
        EvaluationOptions options) =>
        new(script, EvaluationDescriptorSetup.Standard, publishNextStep: null, onComplete: null,
            options, borrowedDescriptors: [],
            new MemoryGovernor(long.MaxValue));
}
