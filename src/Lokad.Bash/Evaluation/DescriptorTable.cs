namespace Lokad.Bash.Evaluation;

/// <summary>Owns mutable logical bindings; redirection scopes separately own host-handle lifetimes.</summary>
/// <remarks>
/// Shared bindings keep standard streams inline and copy additional mappings only after capture.
/// This table adds Bash's allocation cursor; redirection scopes retain closing responsibility.
/// A table has one evaluator owner; captured snapshots may be read concurrently by child evaluations.
/// </remarks>
internal sealed partial class DescriptorTable
{
    private const int FirstAllocatedDescriptorNumber = 10;
    private DescriptorBindings _bindings;
    private int _nextAllocationNumber;

    public FileDescriptor StdIn => _bindings.StdIn;
    public FileDescriptor StdOut => _bindings.StdOut;
    public FileDescriptor StdErr => _bindings.StdErr;

    public DescriptorTable()
        : this(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr)
    {
    }

    public DescriptorTable(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr)
        : this(new DescriptorBindings(stdIn, stdOut, stdErr), nextAllocationNumber: FirstAllocatedDescriptorNumber)
    {
    }

    private DescriptorTable(DescriptorBindings bindings, int nextAllocationNumber)
    {
        _bindings = bindings;
        _nextAllocationNumber = nextAllocationNumber;
    }

    /// <summary>Captures stable bindings without copying mappings or transferring handle ownership.</summary>
    public Snapshot Capture() => new(this);

    /// <summary>Forks independent mutable bindings, sharing captured mappings until a write.</summary>
    public DescriptorTable Clone() => Capture().Fork();

    public FileDescriptor Resolve(LogicalDescriptor descriptor) =>
        _bindings.Resolve(descriptor);

    public void Set(LogicalDescriptor descriptor, FileDescriptor handle)
    {
        if (!_bindings.Set(descriptor, handle))
            return;
        if (handle.IsClosed && descriptor.Number >= FirstAllocatedDescriptorNumber && descriptor.Number < _nextAllocationNumber)
            _nextAllocationNumber = descriptor.Number;
        else if (descriptor.Number == _nextAllocationNumber)
            TryFindAvailableDescriptor(out _);
    }

    /// <summary>Finds the first free script descriptor from 10; host handle numbers do not participate.</summary>
    public bool TryFindAvailableDescriptor(out LogicalDescriptor descriptor)
    {
        while (true)
        {
            descriptor = new LogicalDescriptor(_nextAllocationNumber);
            if (Resolve(descriptor).IsClosed)
                return true;
            if (_nextAllocationNumber == int.MaxValue)
                return false;
            _nextAllocationNumber++;
        }
    }

    public bool HasMapping(LogicalDescriptor descriptor) => _bindings.HasMapping(descriptor);

    public bool References(FileDescriptor handle) =>
        _bindings.References(handle);
}
