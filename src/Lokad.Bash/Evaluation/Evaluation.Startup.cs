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
    /// values initialize this evaluation's own variables under the host's live-memory owner.
    /// </remarks>
    internal Evaluation(
        ParsedScript script,
        EvaluationOptions options,
        EvaluationDescriptorSetup descriptors,
        IReadOnlyList<EnvironmentVariable> environment,
        ShellValue argumentZero,
        IReadOnlyList<ShellValue> positionalParameters)
        : this(script, descriptors, publishNextStep: null, onComplete: null,
            options, borrowedDescriptors: [], new MemoryGovernor(long.MaxValue))
    {
        foreach (var variable in environment)
            SetExportedVariable(variable.Name, variable.Value);
        _vars.ArgumentZero = argumentZero;
        ReplacePositionalParameters(positionalParameters);
    }
}
