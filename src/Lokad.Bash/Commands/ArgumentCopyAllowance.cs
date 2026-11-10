using System;

namespace Lokad.Bash.Commands;

/// <summary>
/// Spends prepaid argument copies once, then charges the shared allocation budget.
/// Name aliases share this state; child commands have their own credit and failure latch.
/// </summary>
public class ArgumentCopyAllowance(CommandAllocationBudget allocationBudget, long reservedCharacters)
{
    private long _reservedCharacters = reservedCharacters;

    public CommandAllocationBudget AllocationBudget { get; } = allocationBudget;
    public bool LimitExceeded { get; private set; }

    public bool TryCharge(long characters)
    {
        var prepaid = Math.Min(characters, _reservedCharacters);
        if (LimitExceeded || !AllocationBudget.Values.TryCharge(0, characters - prepaid))
        {
            LimitExceeded = true;
            return false;
        }
        _reservedCharacters -= prepaid;
        return true;
    }
}
