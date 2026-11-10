using System.Collections.Generic;

namespace Lokad.Bash.Helpers;

/// <summary>Appends distinct host handles to caller-owned storage in encounter order.</summary>
/// <remarks>The caller must not otherwise mutate the storage while this collector is in use.</remarks>
public struct DescriptorHandleCollector
{
    // Linear scans avoid a hash index for standard streams and small alias sets.
    private const int HashIndexThreshold = 128;
    private readonly List<FileDescriptor> _descriptors;
    private HashSet<FileDescriptor>? _seen;

    public DescriptorHandleCollector(List<FileDescriptor> descriptors)
    {
        _descriptors = descriptors;
        _seen = null;
    }

    public IReadOnlyList<FileDescriptor> Descriptors => _descriptors;

    public void Add(FileDescriptor descriptor)
    {
        if (_seen is null && _descriptors.Count < HashIndexThreshold)
        {
            if (!_descriptors.Contains(descriptor))
                _descriptors.Add(descriptor);
            return;
        }

        _seen ??= new HashSet<FileDescriptor>(_descriptors);
        if (_seen.Add(descriptor))
            _descriptors.Add(descriptor);
    }

    public void AddRange(IReadOnlyList<FileDescriptor> descriptors)
    {
        for (var index = 0; index < descriptors.Count; index++)
            Add(descriptors[index]);
    }
}
