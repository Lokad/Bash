using System;
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
    /// <remarks>
    /// Function sources are borrowed until startup prepares them under the engine's runtime budget.
    /// The supported <c>-c</c> form uses <paramref name="argumentZero"/> as the command reader's name.
    /// It initializes <c>BASH_EXECUTION_STRING</c> from the command argument.
    /// A script operand used as <paramref name="argumentZero"/> supplies the file reader's name.
    /// Recognized leading short and named shell options initialize the reader.
    /// The host supplies the parsed source and handles launch argument diagnostics.
    /// </remarks>
    public static ShellEvaluation CreateInherited(
        CommandInvocation command,
        ParsedScript script,
        ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters) =>
        CreateInherited(command, script, argumentZero, positionalParameters, ShellLaunchArguments.Parse(command.Arguments));

    internal static ShellEvaluation CreateInherited(
        CommandInvocation command, ParsedScript script, ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters, ShellLaunchArguments launch)
    {
        var bindings = (command.TryGetInheritance<BashCommandInheritance>(out var inherited)
                ? inherited.Bindings : DescriptorBindings.Snapshot.Standard)
            .WithStandardStreams(command.StdIn, command.StdOut, command.StdErr);
        var evaluation = new ShellEvaluation(script, new EvaluationOptions(0, command.CurrentDirectory),
            new EvaluationDescriptorSetup(DescriptorTable.Snapshot.FromInheritedBindings(bindings), command.BorrowedDescriptors),
            command.CommandName, command.Environment, argumentZero, positionalParameters, launch.EnabledOptions);
        evaluation.SetInheritedFunctions(command.CommandName, command.Functions, inherited.Imports);
        if (launch.Error == ShellLaunchError.None && launch.ReaderIndex < command.Arguments.Count)
        {
            var reader = command.Arguments[launch.ReaderIndex];
            if (launch.CommandString)
                evaluation.SetInheritedCommandString(argumentZero, reader);
            else if (!reader.IsEmpty && reader.Equals(argumentZero)
                && (reader.Bytes.Span[0] != (byte)'-' || launch.ReaderIndex != 0))
                evaluation.SetInheritedReader(reader, SourceMemory.ReaderKind.Script);
        }
        return evaluation;
    }

    /// <summary>Starts with standard host descriptors and an independent environment.</summary>
    /// <remarks>The caller installs the new host's memory owner when running the evaluation.</remarks>
    public static ShellEvaluation CreateForNewHost(
        ParsedScript script,
        EvaluationOptions options) =>
        CreateForNewHost(script, options, sourceMemory: null);

    /// <summary>Installs the public runner's admitted source owner before command-unit parsing.</summary>
    internal static ShellEvaluation CreateForNewHost(
        ParsedScript script, EvaluationOptions options, SourceMemory? sourceMemory) =>
        new(script, EvaluationDescriptorSetup.Standard, publishNextStep: null, onComplete: null,
            options, new MemoryGovernor(long.MaxValue), sourceMemory);
}
