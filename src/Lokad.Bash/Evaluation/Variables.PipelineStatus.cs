using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    private static readonly BindingName PipelineStatusName = "PIPESTATUS";

    /// <summary>Publishes completed foreground statuses from the caller's admitted storage.</summary>
    internal void SetPipelineStatuses(IReadOnlyList<int> statuses, CancellationToken cancellationToken)
    {
        TryGetBinding(PipelineStatusName, out var slot);
        // Bash leaves associative replacements alone. An existing initialized indexed
        // array accepts shell metadata even when readonly; other readonly bindings stay intact.
        if (slot is AssociativeSlot
            || IsReadonlyBinding(PipelineStatusName) && slot is not IndexedSlot { Value.IsInitialized: true })
            return;
        var values = new ShellValue[statuses.Count];
        for (var index = 0; index < statuses.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[index] = ShellValue.FromText(Integers.FormatNumber(statuses[index]));
        }
        ReplaceValue(PipelineStatusName, new IndexedSlot(IndexedArray.FromSequential(values)));
    }
}
