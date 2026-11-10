using System;
using System.Text;

namespace Lokad.Bash.Memory;

/// <summary>Allocates retained data charged until the enclosing memory scope is disposed.</summary>
/// <remarks>
/// Returned data must not outlive that scope. Owners with a shorter, explicit lifetime
/// should hold and dispose a reservation directly. No managed objects are registered here.
/// </remarks>
public static class ScopedMemory
{
    public static string DecodeUtf8(ReadOnlyMemory<byte> source, IMemoryBudget budget, long extraWorkspaceBytes) =>
        DecodeUtf8(source, budget, extraWorkspaceBytes, out _);

    /// <summary>Returns the reservation so an unpublished decoded result can be refunded.</summary>
    public static string DecodeUtf8(ReadOnlyMemory<byte> source, IMemoryBudget budget, long extraWorkspaceBytes,
        out IMemoryReservation? reservation)
    {
        const int StringOverheadBytes = 24;
        reservation = null;
        if (source.IsEmpty)
            return string.Empty;
        reservation = budget.Reserve(checked(StringOverheadBytes
            + (long)sizeof(char) * Encoding.UTF8.GetCharCount(source.Span) + extraWorkspaceBytes));
        try { return Encoding.UTF8.GetString(source.Span); }
        catch { reservation.Dispose(); throw; }
    }

    public static byte[] Allocate(int length, IMemoryBudget budget)
    {
        var reservation = budget.Reserve(length);
        try { return new byte[length]; }
        catch { reservation.Dispose(); throw; }
    }

    public static ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> source, IMemoryBudget budget) =>
        Copy(source, budget, out _);

    /// <summary>Copies against one owner's prepaid capacity; that owner controls publication and cleanup.</summary>
    public static ReadOnlyMemory<byte> Copy(ReadOnlyMemory<byte> source, MemoryCapacity memory)
    {
        if (source.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;
        var previous = memory.Bytes;
        memory.Grow(source.Length);
        try { return source.ToArray(); }
        catch { memory.Resize(previous); throw; }
    }

    /// <summary>Returns the reservation so a caller can refund a copy that fails to be published.</summary>
    public static ReadOnlyMemory<byte> Copy(
        ReadOnlyMemory<byte> source, IMemoryBudget budget, out IMemoryReservation? reservation)
    {
        reservation = null;
        if (source.IsEmpty)
            return ReadOnlyMemory<byte>.Empty;
        reservation = budget.Reserve(source.Length);
        try { return source.ToArray(); }
        catch { reservation.Dispose(); throw; }
    }
}
