using System.Collections.Generic;

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

        if (_additional != null && _additional.TryGetValue(descriptor, out var previous) && previous == handle)
            return false;

        if (_additional is null)
            _additional = new Dictionary<LogicalDescriptor, FileDescriptor>();
        else if (_additionalShared)
            _additional = new Dictionary<LogicalDescriptor, FileDescriptor>(_additional);

        _additionalShared = false;
        _additional[descriptor] = handle;
        return true;
    }

    /// <summary>Removes an additional binding, or marks a standard stream closed, without closing its handle.</summary>
    public bool Remove(LogicalDescriptor descriptor)
    {
        if (descriptor.Number <= 2)
            return Set(descriptor, FileDescriptor.Closed);
        if (_additional == null || !_additional.ContainsKey(descriptor))
            return false;

        if (_additionalShared)
            _additional = new Dictionary<LogicalDescriptor, FileDescriptor>(_additional);
        _additionalShared = false;
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
