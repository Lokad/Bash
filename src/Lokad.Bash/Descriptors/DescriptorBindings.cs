using Lokad.Bash.Memory;

namespace Lokad.Bash;

/// <summary>Maps interpreter-local descriptor numbers to host handles without owning their lifetimes.</summary>
/// <remarks>
/// Keep one mutable value per owner; share additional mappings through captured snapshots, not value copies.
/// Standard streams stay inline. Additional bindings share immutable indexed slots across captures.
/// </remarks>
public partial struct DescriptorBindings
{
    private DescriptorMap? _additional;
    private DescriptorMap.Builder? _writer;
    private DescriptorBinding _stdIn;
    private DescriptorBinding _stdOut;
    private DescriptorBinding _stdErr;

    internal readonly long AdditionalMemoryBytes => _writer != null ? DescriptorMap.EstimateMemoryBytes(_writer.SlotCount)
        : _additional == null ? 0 : DescriptorMap.EstimateMemoryBytes(_additional.SlotCount);

    internal readonly long OwnedMemoryBytes => _writer != null ? AdditionalMemoryBytes : 0;

    public readonly FileDescriptor StdIn => _stdIn.Handle;
    public readonly FileDescriptor StdOut => _stdOut.Handle;
    public readonly FileDescriptor StdErr => _stdErr.Handle;

    public DescriptorBindings(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr)
        : this(new DescriptorBinding(stdIn), new DescriptorBinding(stdOut), new DescriptorBinding(stdErr), additional: null)
    {
    }

    private DescriptorBindings(DescriptorBinding stdIn, DescriptorBinding stdOut, DescriptorBinding stdErr,
        DescriptorMap? additional)
    {
        _additional = additional;
        _writer = null;
        _stdIn = stdIn;
        _stdOut = stdOut;
        _stdErr = stdErr;
    }

    public readonly FileDescriptor Resolve(LogicalDescriptor descriptor) => ResolveBinding(descriptor).Handle;

    internal readonly DescriptorBinding ResolveBinding(LogicalDescriptor descriptor) => descriptor.Number <= 2
        ? ResolveBinding(descriptor, _stdIn, _stdOut, _stdErr, null)
        : TryGetAdditionalValue(descriptor, out var binding) ? binding : DescriptorBinding.Closed;

    /// <summary>Updates a binding without closing handles; returns whether its value changed.</summary>
    public bool Set(LogicalDescriptor descriptor, FileDescriptor handle) =>
        Set(descriptor, new DescriptorBinding(handle));

    internal bool Set(LogicalDescriptor descriptor, DescriptorBinding binding) =>
        Set(descriptor, binding, memory: null);

    internal bool Set(LogicalDescriptor descriptor, DescriptorBinding binding, MemoryCapacity? memory)
    {
        switch (descriptor.Number)
        {
            case 0:
                if (IsUnchanged(_stdIn, binding)) return false;
                _stdIn = binding;
                return true;
            case 1:
                if (IsUnchanged(_stdOut, binding)) return false;
                _stdOut = binding;
                return true;
            case 2:
                if (IsUnchanged(_stdErr, binding)) return false;
                _stdErr = binding;
                return true;
        }

        if (!PrepareSet(descriptor, binding, memory))
            return false;
        var writer = _writer ?? new DescriptorMap.Builder(_additional ?? DescriptorMap.Empty);
        writer.Set(descriptor, binding);
        _writer = writer;
        _additional = null;
        return true;
    }

    /// <summary>Admits and prepares writable capacity without publishing a changed binding.</summary>
    internal bool PrepareSet(LogicalDescriptor descriptor, DescriptorBinding binding, MemoryCapacity? memory)
    {
        if (descriptor.Number <= 2)
            return !IsUnchanged(ResolveBinding(descriptor), binding);
        var present = TryGetAdditionalValue(descriptor, out var previous);
        if (present && IsUnchanged(previous, binding))
            return false;
        var requiredSlots = _writer?.RequiredSlots(present) ?? _additional?.RequiredSlots(present) ?? 1;
        var required = DescriptorMap.EstimateMemoryBytes(requiredSlots);
        var owned = OwnedMemoryBytes;
        if (required > owned)
            memory?.Grow(required - owned);
        // No tree is changed here. Every receiving owner admits its complete
        // transitive backing and replacement headroom before any binding is set.
        return true;
    }

    // A caller supplying only the same handle leaves its established access mode in place.
    private static bool IsUnchanged(DescriptorBinding current, DescriptorBinding proposed) =>
        current.Handle == proposed.Handle && (current.IsWriteOnly || !proposed.IsWriteOnly);

    /// <summary>Removes an additional binding, or marks a standard stream closed, without closing its handle.</summary>
    public bool Remove(LogicalDescriptor descriptor)
    {
        if (descriptor.Number <= 2)
            return Set(descriptor, FileDescriptor.Closed);
        if (!HasMapping(descriptor))
            return false;

        var writer = _writer ?? new DescriptorMap.Builder(_additional ?? DescriptorMap.Empty);
        writer.Remove(descriptor);
        _writer = writer;
        _additional = null;
        return true;
    }

    private readonly bool TryGetAdditionalValue(LogicalDescriptor descriptor, out DescriptorBinding binding)
    {
        if (_writer != null) return _writer.TryGetValue(descriptor, out binding);
        if (_additional != null) return _additional.TryGetValue(descriptor, out binding);
        binding = DescriptorBinding.Closed;
        return false;
    }

    public readonly bool HasMapping(LogicalDescriptor descriptor) => descriptor.Number <= 2
        || (_writer?.ContainsKey(descriptor) ?? _additional?.ContainsKey(descriptor) ?? false);

    public readonly bool References(FileDescriptor handle) =>
        StdIn == handle || StdOut == handle || StdErr == handle
            || (_writer?.ContainsValue(handle) ?? _additional?.ContainsValue(handle) ?? false);

    /// <summary>Freezes standard streams and additional mappings without copying or acquiring handle ownership.</summary>
    public Snapshot Capture() => new(ref this);

    private static bool HasMapping(LogicalDescriptor descriptor,
        DescriptorMap? additional) =>
        descriptor.Number <= 2 || additional?.ContainsKey(descriptor) == true;

    private static DescriptorBinding ResolveBinding(LogicalDescriptor descriptor,
        DescriptorBinding stdIn, DescriptorBinding stdOut, DescriptorBinding stdErr,
        DescriptorMap? additional) => descriptor.Number switch
    {
        0 => stdIn,
        1 => stdOut,
        2 => stdErr,
        _ => additional != null && additional.TryGetValue(descriptor, out var binding) ? binding : DescriptorBinding.Closed
    };
}
