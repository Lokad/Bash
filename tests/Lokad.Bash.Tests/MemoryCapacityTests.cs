using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class MemoryCapacityTests
{
    public enum CapacityState { Unused, Prepaid, Released }

    [Theory]
    [InlineData(CapacityState.Unused, false)]
    [InlineData(CapacityState.Unused, true)]
    [InlineData(CapacityState.Prepaid, false)]
    [InlineData(CapacityState.Prepaid, true)]
    [InlineData(CapacityState.Released, false)]
    [InlineData(CapacityState.Released, true)]
    public void AdjustmentsRejectADisposedAncestor(CapacityState state, bool wrappedBudget)
    {
        var governor = new MemoryGovernor(1024);
        using var independent = governor.Reserve(128);
        using var parent = governor.CreateScope();
        IMemoryBudget budget = wrappedBudget ? new CountingMemoryBudget(parent) : parent;
        using var child = budget.CreateScope();
        using var capacity = new MemoryCapacity(child, state == CapacityState.Unused ? 0 : 256);
        if (state == CapacityState.Released)
        {
            capacity.Resize(0);
            capacity.ReleaseUnusedCapacity();
        }
        var logicalBytes = state == CapacityState.Prepaid ? 256 : 0;
        Assert.Equal(logicalBytes, capacity.Bytes);
        Assert.Equal(128 + logicalBytes, governor.ReservedBytes);

        parent.Dispose();

        Assert.Throws<ObjectDisposedException>(() => capacity.TryResize(0));
        Assert.Throws<ObjectDisposedException>(() => capacity.Resize(logicalBytes));
        Assert.Throws<ObjectDisposedException>(() => capacity.Grow(0));
        Assert.Throws<ObjectDisposedException>(() => capacity.Grow(1));
        Assert.Throws<ObjectDisposedException>(capacity.TrimExcess);
        Assert.Throws<ObjectDisposedException>(capacity.ReleaseUnusedCapacity);
        Assert.Throws<ObjectDisposedException>(capacity.DetachReservation);
        Assert.Equal(logicalBytes, capacity.Bytes);
        capacity.Dispose();
        capacity.Dispose();
        Assert.Equal(0, capacity.Bytes);
        Assert.Equal(128, governor.ReservedBytes);
    }

    [Fact]
    public void LiveAdjustmentsReusePrepaidCapacityAndDetachmentKeepsTheScopeCharge()
    {
        var governor = new MemoryGovernor(1024);
        var budget = new CountingMemoryBudget(governor);
        using var owner = budget.CreateScope();
        using var capacity = new MemoryCapacity(owner, 0);

        Assert.True(capacity.TryResize(0));
        capacity.Resize(0);
        capacity.Grow(0);
        capacity.TrimExcess();
        capacity.ReleaseUnusedCapacity();
        Assert.Equal(0, budget.AdmissionCalls);
        Assert.Equal(0, governor.ReservedBytes);

        capacity.Grow(256);
        var paid = budget.AdmissionCalls;
        Assert.Equal(1, paid);
        Assert.True(capacity.TryResize(128));
        capacity.Resize(128);
        capacity.Grow(0);
        capacity.TrimExcess();
        Assert.Equal(paid, budget.AdmissionCalls);
        Assert.Equal(128, capacity.Bytes);
        Assert.Equal(256, governor.ReservedBytes);

        capacity.DetachReservation();
        Assert.Equal(0, capacity.Bytes);
        Assert.Throws<ObjectDisposedException>(capacity.ReleaseUnusedCapacity);
        capacity.Dispose();
        Assert.Equal(128, governor.ReservedBytes);
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void AdaptedEmptyReservationGrowsWithOneAdmissionAndRejectsAfterDisposal()
    {
        var governor = new MemoryGovernor(1024);
        var budget = new CountingMemoryBudget(governor);
        using var owner = budget.CreateScope();
        Assert.True(MemoryCapacity.TryReserve(owner, 0, out var admitted));
        using var reservation = Assert.IsAssignableFrom<IMemoryReservation>(admitted);
        var calls = budget.AdmissionCalls;

        Assert.True(reservation.TryResize(256));
        Assert.Equal(calls + 1, budget.AdmissionCalls);
        Assert.False(reservation.TryResize(2048));
        Assert.Equal(256, reservation.Bytes);
        Assert.Equal(256, governor.ReservedBytes);

        owner.Dispose();
        Assert.Equal(0, reservation.Bytes);
        Assert.Throws<ObjectDisposedException>(() => reservation.TryResize(0));
        Assert.Equal(0, governor.ReservedBytes);
    }
}
