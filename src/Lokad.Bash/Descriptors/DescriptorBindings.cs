using System;
using System.Collections.Generic;
using Lokad.Bash.Memory;

namespace Lokad.Bash;

/// <summary>Maps interpreter-local descriptor numbers to host handles without owning their lifetimes.</summary>
/// <remarks>
/// Keep one mutable value per owner; share additional mappings through captured snapshots, not value copies.
/// Standard streams stay inline. Additional bindings are allocated on demand and copied only after capture.
/// </remarks>
public partial struct DescriptorBindings
{
    private Dictionary<LogicalDescriptor, FileDescriptor>? _additional;
    private bool _additionalShared;

    // Dictionary header, bucket/entry arrays and capacity rather than just live keys.
    internal readonly long AdditionalMemoryBytes => _additional == null ? 0
        : checked(128L + 32L * _additional.EnsureCapacity(0));

    public FileDescriptor StdIn { get; private set; }
    public FileDescriptor StdOut { get; private set; }
    public FileDescriptor StdErr { get; private set; }

    public DescriptorBindings(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr)
        : this(stdIn, stdOut, stdErr, additional: null)
    {
    }

    private DescriptorBindings(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        Dictionary<LogicalDescriptor, FileDescriptor>? additional)
    {
        _additional = additional;
        _additionalShared = additional != null;
        StdIn = stdIn;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public readonly FileDescriptor Resolve(LogicalDescriptor descriptor) =>
        Resolve(descriptor, StdIn, StdOut, StdErr, _additional);

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
        var additional = _additional ?? throw new InvalidOperationException("A changed descriptor requires writable storage.");
        additional[descriptor] = handle;
        return true;
    }

    /// <summary>Admits and prepares writable capacity without publishing a changed binding.</summary>
    internal bool PrepareSet(LogicalDescriptor descriptor, FileDescriptor handle, MemoryCapacity? memory)
    {
        if (descriptor.Number <= 2)
            return Resolve(descriptor) != handle;
        var previous = FileDescriptor.Closed;
        var present = _additional != null && _additional.TryGetValue(descriptor, out previous);
        if (present && previous == handle)
            return false;
        PrepareAdditionalCapacity(checked((_additional?.Count ?? 0) + (present ? 0 : 1)), memory);
        return true;
    }

    private void PrepareAdditionalCapacity(int required, MemoryCapacity? memory)
    {
        var capacity = _additional?.EnsureCapacity(0) ?? 0;
        if (_additional != null && !_additionalShared && required <= capacity)
            return;
        // New dictionary capacities round to primes; a twofold bound covers that
        // rounding. Replacements are charged while their old arrays are still live.
        var requested = _additionalShared ? required : (int)Math.Min(int.MaxValue, Math.Max(required, 2L * capacity));
        memory?.Grow(checked(128L + 32L * (2L * requested + 1)));
        if (_additional == null)
            _additional = new Dictionary<LogicalDescriptor, FileDescriptor>(requested);
        else if (_additionalShared)
        {
            var replacement = new Dictionary<LogicalDescriptor, FileDescriptor>(requested);
            foreach (var binding in _additional)
                replacement.Add(binding.Key, binding.Value);
            _additional = replacement;
        }
        else
            _additional.EnsureCapacity(requested);
        _additionalShared = false;
    }

    /// <summary>Removes an additional binding, or marks a standard stream closed, without closing its handle.</summary>
    public bool Remove(LogicalDescriptor descriptor)
    {
        if (descriptor.Number <= 2)
            return Set(descriptor, FileDescriptor.Closed);
        if (_additional == null || !_additional.ContainsKey(descriptor))
            return false;

        PrepareAdditionalCapacity(_additional.Count, null);
        return _additional.Remove(descriptor);
    }

    public readonly bool HasMapping(LogicalDescriptor descriptor) => HasMapping(descriptor, _additional);

    public readonly bool References(FileDescriptor handle) =>
        StdIn == handle || StdOut == handle || StdErr == handle || _additional?.ContainsValue(handle) == true;

    /// <summary>Freezes standard streams and additional mappings without copying or acquiring handle ownership.</summary>
    public Snapshot Capture() => new(ref this);

    private static bool HasMapping(LogicalDescriptor descriptor,
        Dictionary<LogicalDescriptor, FileDescriptor>? additional) =>
        descriptor.Number <= 2 || additional?.ContainsKey(descriptor) == true;

    private static FileDescriptor Resolve(LogicalDescriptor descriptor,
        FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
        Dictionary<LogicalDescriptor, FileDescriptor>? additional) => descriptor.Number switch
    {
        0 => stdIn,
        1 => stdOut,
        2 => stdErr,
        _ => additional?.GetValueOrDefault(descriptor, FileDescriptor.Closed) ?? FileDescriptor.Closed
    };
}
