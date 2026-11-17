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

    internal readonly long AdditionalMemoryBytes => _writer != null ? DescriptorMap.EstimateMemoryBytes(_writer.SlotCount)
        : _additional == null ? 0 : DescriptorMap.EstimateMemoryBytes(_additional.SlotCount);

    internal readonly long OwnedMemoryBytes => _writer != null ? AdditionalMemoryBytes : 0;

    public FileDescriptor StdIn { get; private set; }
    public FileDescriptor StdOut { get; private set; }
    public FileDescriptor StdErr { get; private set; }

    public DescriptorBindings(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr)
        : this(stdIn, stdOut, stdErr, additional: null)
    {
    }

    private DescriptorBindings(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        DescriptorMap? additional)
    {
        _additional = additional;
        _writer = null;
        StdIn = stdIn;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public readonly FileDescriptor Resolve(LogicalDescriptor descriptor) => descriptor.Number <= 2
        ? Resolve(descriptor, StdIn, StdOut, StdErr, null)
        : TryGetAdditionalValue(descriptor, out var handle) ? handle : FileDescriptor.Closed;

    /// <summary>Updates a binding without closing handles; returns whether its value changed.</summary>
    public bool Set(LogicalDescriptor descriptor, FileDescriptor handle)
    {
        switch (descriptor.Number)
        {
            case 0:
                if (StdIn == handle) return false;
                StdIn = handle;
                return true;
            case 1:
                if (StdOut == handle) return false;
                StdOut = handle;
                return true;
            case 2:
                if (StdErr == handle) return false;
                StdErr = handle;
                return true;
        }

        if (!PrepareSet(descriptor, handle, null))
            return false;
        var writer = _writer ?? new DescriptorMap.Builder(_additional ?? DescriptorMap.Empty);
        writer.Set(descriptor, handle);
        _writer = writer;
        _additional = null;
        return true;
    }

    /// <summary>Admits and prepares writable capacity without publishing a changed binding.</summary>
    internal bool PrepareSet(LogicalDescriptor descriptor, FileDescriptor handle, MemoryCapacity? memory)
    {
        if (descriptor.Number <= 2)
            return Resolve(descriptor) != handle;
        var present = TryGetAdditionalValue(descriptor, out var previous);
        if (present && previous == handle)
            return false;
        var requiredSlots = _writer?.RequiredSlots(descriptor) ?? _additional?.RequiredSlots(descriptor) ?? 1;
        var required = DescriptorMap.EstimateMemoryBytes(requiredSlots);
        var owned = OwnedMemoryBytes;
        if (required > owned)
            memory?.Grow(required - owned);
        // No tree is changed here. Every receiving owner admits its complete
        // transitive backing and replacement headroom before any binding is set.
        return true;
    }

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

    private readonly bool TryGetAdditionalValue(LogicalDescriptor descriptor, out FileDescriptor handle)
    {
        if (_writer != null) return _writer.TryGetValue(descriptor, out handle);
        if (_additional != null) return _additional.TryGetValue(descriptor, out handle);
        handle = FileDescriptor.Closed;
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

    private static FileDescriptor Resolve(LogicalDescriptor descriptor,
        FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        DescriptorMap? additional) => descriptor.Number switch
    {
        0 => stdIn,
        1 => stdOut,
        2 => stdErr,
        _ => additional != null && additional.TryGetValue(descriptor, out var handle) ? handle : FileDescriptor.Closed
    };
}
