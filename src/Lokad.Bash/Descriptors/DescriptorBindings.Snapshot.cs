using System.Collections.Generic;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

public partial struct DescriptorBindings
{
    /// <summary>Stable logical bindings, shared without transferring responsibility for host handles.</summary>
    /// <remarks>Interpreter allocation policy remains outside this snapshot.</remarks>
    public readonly struct Snapshot
    {
        private readonly DescriptorMap? _additional;
        private readonly DescriptorBinding _stdIn;
        private readonly DescriptorBinding _stdOut;
        private readonly DescriptorBinding _stdErr;

        public FileDescriptor StdIn => _stdIn.Handle;
        public FileDescriptor StdOut => _stdOut.Handle;
        public FileDescriptor StdErr => _stdErr.Handle;

        public static Snapshot Standard { get; } =
            new(new DescriptorBinding(FileDescriptor.StdIn), new DescriptorBinding(FileDescriptor.StdOut),
                new DescriptorBinding(FileDescriptor.StdErr), additional: null);

        public Snapshot(ref DescriptorBindings bindings)
            : this(bindings._stdIn, bindings._stdOut, bindings._stdErr, bindings._writer?.Capture() ?? bindings._additional)
        {
        }

        private Snapshot(DescriptorBinding stdIn, DescriptorBinding stdOut, DescriptorBinding stdErr,
            DescriptorMap? additional)
        {
            _stdIn = stdIn;
            _stdOut = stdOut;
            _stdErr = stdErr;
            _additional = additional;
        }

        public Snapshot WithStandardStreams(FileDescriptor stdIn, FileDescriptor stdOut, FileDescriptor stdErr) =>
            WithStandardStreams(_stdIn.WithHandle(stdIn), _stdOut.WithHandle(stdOut), _stdErr.WithHandle(stdErr));

        internal Snapshot WithStandardStreams(DescriptorBinding stdIn, DescriptorBinding stdOut, DescriptorBinding stdErr) =>
            new(stdIn, stdOut, stdErr, _additional);

        /// <summary>Creates independent mutable bindings sharing immutable additional mappings.</summary>
        public DescriptorBindings Fork() => new(_stdIn, _stdOut, _stdErr, _additional);

        public FileDescriptor Resolve(LogicalDescriptor descriptor) => ResolveBinding(descriptor).Handle;

        internal DescriptorBinding ResolveBinding(LogicalDescriptor descriptor) =>
            DescriptorBindings.ResolveBinding(descriptor, _stdIn, _stdOut, _stdErr, _additional);

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
                _additional.AddReferencedHandlesTo(ref handles);
            }
        }
    }
}
