using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    internal static readonly BindingName RegexCaptureName = "BASH_REMATCH";

    /// <summary>Publishes admitted captures on the selected global owner, replacing prior kind and attributes.</summary>
    internal void SetRegexCaptures(IReadOnlyList<ShellValue> captures)
    {
        // GNU replaces this special binding directly, including readonly, export and nameref attributes.
        var replacement = new IndexedSlot(IndexedArray.FromSequential(captures));
        ClearBinding(RegexCaptureName);
        StoreBinding(RegexCaptureName, replacement);
    }
}
