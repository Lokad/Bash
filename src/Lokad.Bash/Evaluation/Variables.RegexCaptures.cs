namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    internal static readonly BindingName RegexCaptureName = "BASH_REMATCH";

    /// <summary>Clears captures after a completed match attempt with no match.</summary>
    internal void ClearRegexCaptures()
    {
        // Element removal can leave an empty array with backing that publication releases.
        if (_bindings.TryGetValue(RegexCaptureName, out var prior)
            && prior is IndexedSlot { Attributes: BindingAttributes.None, Value: { IsInitialized: true, EstimatedMemoryBytes: 0 } })
            return;

        SetRegexCaptures(new IndexedArray(isInitialized: true));
    }

    /// <summary>Publishes the owned, admitted array on the selected global owner.</summary>
    internal void SetRegexCaptures(IndexedArray captures) => StoreBinding(RegexCaptureName, new IndexedSlot(captures));
}
