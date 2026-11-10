using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

internal sealed partial class DescriptorTable
{
    /// <summary>Stable logical bindings that borrow their referenced host handles.</summary>
    /// <remarks>Additional mappings are never mutated after capture; each writing table makes its own copy.</remarks>
    internal readonly struct Snapshot
    {
        public DescriptorBindings.Snapshot Bindings { get; }
        public FileDescriptor StdIn => Bindings.StdIn;
        public FileDescriptor StdOut => Bindings.StdOut;
        public FileDescriptor StdErr => Bindings.StdErr;
        private readonly int _nextAllocationNumber;

        public static Snapshot Standard { get; } =
            FromInheritedBindings(DescriptorBindings.Snapshot.Standard);

        internal Snapshot(DescriptorTable table)
            : this(table._bindings.Capture(), table._nextAllocationNumber)
        {
        }

        private Snapshot(DescriptorBindings.Snapshot bindings, int nextAllocationNumber)
        {
            Bindings = bindings;
            _nextAllocationNumber = nextAllocationNumber;
        }

        /// <summary>Starts Bash allocation from 10; inherited occupied numbers are skipped when allocating.</summary>
        public static Snapshot FromInheritedBindings(DescriptorBindings.Snapshot bindings) =>
            new(bindings, FirstAllocatedDescriptorNumber);

        public Snapshot WithStandardStreams(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr) =>
            new(Bindings.WithStandardStreams(stdIn, stdOut, stdErr), _nextAllocationNumber);

        /// <summary>Creates an independent mutable owner without copying the captured mappings.</summary>
        public DescriptorTable Fork() => new(Bindings.Fork(), _nextAllocationNumber);

        public FileDescriptor Resolve(LogicalDescriptor descriptor) =>
            Bindings.Resolve(descriptor);

        public bool HasMapping(LogicalDescriptor descriptor) => Bindings.HasMapping(descriptor);

        /// <summary>Adds distinct referenced handles to caller-owned storage without acquiring closing responsibility.</summary>
        public void AddReferencedHandlesTo(ref DescriptorHandleCollector handles) =>
            Bindings.AddReferencedHandlesTo(ref handles);
    }
}
