using System.Collections.Generic;
using Lokad.Bash.Commands;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Evaluation
{
    /// <summary>Initializes evaluation from captured interpreter values, independently of command hosting.</summary>
    /// <remarks>
    /// Captured descriptor bindings are forked and their handles remain borrowed. Environment and positional
    /// entries initialize caller-prepared state before the engine installs its runtime budget.
    /// </remarks>
    internal Evaluation(
        ParsedScript script,
        EvaluationOptions options,
        EvaluationDescriptorSetup descriptors,
        IReadOnlyList<EnvironmentVariable> environment,
        ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters)
        : this(script, descriptors, publishNextStep: null, onComplete: null,
            options, new MemoryGovernor(long.MaxValue), sourceMemory: null)
    {
        foreach (var variable in environment)
            SetExportedVariable(variable.Name, variable.Value);
        _vars.ArgumentZero = argumentZero;
        ReplacePositionalParameters(positionalParameters);
    }
}
