using System;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    private static readonly BindingName PipelineStatusName = "PIPESTATUS";

    /// <summary>Publishes completed foreground statuses without following a user-created nameref.</summary>
    internal void SetPipelineStatuses(IReadOnlyList<int> statuses, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        _bindings.TryGetValue(PipelineStatusName, out var slot);
        // Bash leaves associative replacements alone. An existing initialized indexed
        // array accepts shell metadata even when readonly; other readonly bindings stay intact.
        if (slot is AssociativeSlot
            || IsReadonlyBinding(PipelineStatusName) && slot is not IndexedSlot { Value.IsInitialized: true })
            return;
        if (statuses.Count >= Array.MaxLength)
            throw new ByteMemoryBuilder.LimitExceededException();
        memory.Charge(checked(512L + 256L * statuses.Count));
        var values = new ShellValue[statuses.Count];
        for (var index = 0; index < statuses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[index] = ShellValue.FromText(Integers.FormatNumber(statuses[index]));
        }
        ReplaceValue(PipelineStatusName, new IndexedSlot(IndexedArray.FromSequential(values)));
    }
}
