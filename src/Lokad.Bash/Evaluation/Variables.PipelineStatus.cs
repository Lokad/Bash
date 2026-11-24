using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    internal static readonly BindingName PipelineStatusName = "PIPESTATUS";

    /// <summary>Publishes completed foreground statuses from the caller's admitted storage.</summary>
    internal void SetPipelineStatuses(IReadOnlyList<int> statuses, Evaluation.EvaluationContext context, CancellationToken cancellationToken)
    {
        var name = PipelineStatusName;
        var owner = this;
        // Only the replacement is published. Reading the previous shape and attributes
        // does not require a command-local copy of the old array.
        TryGetBinding(name, detachBorrowed: false, out var slot);
        if (slot is NamerefSlot)
        {
            if (TryResolveBinding(name, selectOwner: null, out var target)
                && target.Reference.Kind == VariableReferenceKind.Name
                && target.Owner.TryGetBinding(target.Name, detachBorrowed: false, out var resolved))
            {
                name = target.Name;
                owner = target.Owner;
                slot = resolved;
            }
            else
            {
                // An unresolved nameref leaves the local alias intact and creates global metadata.
                owner = context.GetGlobalBindingOwner(name, this);
                slot = null;
            }
        }

        // Indexed arrays accept shell metadata even when readonly. A declaration-only array
        // retains this backing privately until an explicit assignment makes it visible.
        if (slot is not (null or IndexedSlot))
            return;

        var previous = (slot as IndexedSlot)?.Value;
        var array = IndexedArray.FromForegroundStatuses(statuses, previous, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        owner.StoreBinding(name, new IndexedSlot(array) { Attributes = slot?.Attributes ?? BindingAttributes.None });
    }
}
