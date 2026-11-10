using System.Collections.Generic;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;
using ShellEvaluation = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash;

/// <summary>Translates command inheritance into the values required to start a Bash evaluation.</summary>
public static class ShellEvaluationStartup
{
    /// <summary>Starts on the same host with captured bindings, borrowed handles, environment and shared work allowance.</summary>
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
            GetExpansionBudget(command), command.Environment, argumentZero, positionalParameters);
    }

    /// <summary>Starts with standard host descriptors, sharing expansion work limits only when a parent is supplied.</summary>
    /// <remarks>Parent mappings, closing responsibility, environment and prepaid argument copies do not transfer.</remarks>
    public static ShellEvaluation CreateForNewHost(
        CommandInvocation? parentInvocation,
        ParsedScript script,
        EvaluationOptions options) =>
        new(script, EvaluationDescriptorSetup.Standard, publishNextStep: null, onComplete: null,
            options, borrowedDescriptors: [],
            parentInvocation is null ? new ExpansionBudget() : GetExpansionBudget(parentInvocation));

    private static ExpansionBudget GetExpansionBudget(CommandInvocation command) =>
        command.TryGetInheritance<BashCommandInheritance>(out var inherited)
            ? inherited.ExpansionBudget.Fork() : new ExpansionBudget(command.AllocationBudget);
}
