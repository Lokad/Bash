using System;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Helpers;

/// <summary>Shares diagnostic buffer admission between evaluator and host completion paths.</summary>
internal static class ShellDiagnostic
{
    internal const int SmallBytes = ByteMemoryBuilder.DefaultCapacity;
    internal const string MemoryRefusalMessage = "shared memory budget exhausted";
    internal static readonly ReadOnlyMemory<byte> MemoryRefusal =
        Utf8Text.Encode("shell: " + MemoryRefusalMessage + "\n");

    internal static bool TryReserve(IMemoryBudget budget, long length, out IMemoryReservation? reservation)
    {
        if (length > ByteMemoryBuilder.MaxMemoryBuilderBytes)
            throw new ByteMemoryBuilder.LimitExceededException();

        // Short records, including the refusal message, fit within caller headroom.
        reservation = null;
        return length <= SmallBytes || budget.TryReserve(length, out reservation);
    }
}
