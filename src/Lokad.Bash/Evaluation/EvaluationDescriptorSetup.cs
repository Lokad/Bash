using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

/// <summary>Supplies initial standard streams, inherited logical mappings and borrowed handles for an evaluation.</summary>
/// <remarks>
/// The receiving evaluator forks captured bindings; borrowing never transfers handle ownership.
/// Callers must not mutate retained borrowed-handle lists.
/// </remarks>
internal readonly record struct EvaluationDescriptorSetup(
    DescriptorTable.Snapshot Bindings,
    IReadOnlyList<FileDescriptor> BorrowedDescriptors)
{
    public static EvaluationDescriptorSetup Standard { get; } =
        new(DescriptorTable.Snapshot.Standard, []);
}
