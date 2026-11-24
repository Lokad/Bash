using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    internal static readonly BindingName RegexCaptureName = "BASH_REMATCH";

    /// <summary>Publishes admitted captures on the selected global owner, replacing prior kind and attributes.</summary>
    internal void SetRegexCaptures(IReadOnlyList<ShellValue> captures)
    {
        // Element removal can leave an empty array with backing that publication releases.
        if (captures.Count == 0 && _bindings.TryGetValue(RegexCaptureName, out var prior)
            && prior is IndexedSlot { Attributes: BindingAttributes.None, Value: { IsInitialized: true, EstimatedMemoryBytes: 0 } })
            return;

        StoreBinding(RegexCaptureName, new IndexedSlot(IndexedArray.FromSequential(captures)));
    }
}
