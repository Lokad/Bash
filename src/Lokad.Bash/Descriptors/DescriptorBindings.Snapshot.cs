using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

public partial struct DescriptorBindings
{
    /// <summary>Stable logical bindings, shared without transferring responsibility for host handles.</summary>
    /// <remarks>Interpreter allocation policy remains outside this snapshot.</remarks>
    public readonly struct Snapshot
    {
        private readonly Dictionary<LogicalDescriptor, FileDescriptor>? _additional;

        public FileDescriptor StdIn { get; }
        public FileDescriptor StdOut { get; }
        public FileDescriptor StdErr { get; }

        public static Snapshot Standard { get; } =
            new(FileDescriptor.StdIn, FileDescriptor.StdOut, FileDescriptor.StdErr, additional: null);

        public Snapshot(ref DescriptorBindings bindings)
            : this(bindings.StdIn, bindings.StdOut, bindings.StdErr, bindings._additional)
        {
            bindings._additionalShared = _additional != null;
        }

        private Snapshot(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr,
            Dictionary<LogicalDescriptor, FileDescriptor>? additional)
        {
            StdIn = stdIn;
            StdOut = stdOut;
            StdErr = stdErr;
            _additional = additional;
        }

        public Snapshot WithStandardStreams(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr) =>
            new(stdIn, stdOut, stdErr, _additional);

        /// <summary>Creates independent mutable bindings, copying additional mappings only on write.</summary>
        public DescriptorBindings Fork() => new(StdIn, StdOut, StdErr, _additional);

        public FileDescriptor Resolve(LogicalDescriptor descriptor) =>
            DescriptorBindings.Resolve(descriptor, StdIn, StdOut, StdErr, _additional);

        public bool HasMapping(LogicalDescriptor descriptor) => DescriptorBindings.HasMapping(descriptor, _additional);

        /// <summary>Appends distinct handles to caller-owned storage in encounter order, without acquiring ownership.</summary>
        /// <remarks>Existing entries are retained. The caller must not mutate the list concurrently.</remarks>
        public void AddReferencedHandlesTo(List<FileDescriptor> handles)
        {
            var collector = new DescriptorHandleCollector(handles);
            AddReferencedHandlesTo(ref collector);
        }

        /// <summary>Shares the deduplication index when callers aggregate multiple sets of borrowed handles.</summary>
        public void AddReferencedHandlesTo(ref DescriptorHandleCollector handles)
        {
            handles.Add(StdIn);
            handles.Add(StdOut);
            handles.Add(StdErr);
            if (_additional != null)
            {
                foreach (var binding in _additional)
                    handles.Add(binding.Value);
            }
        }
    }
}
