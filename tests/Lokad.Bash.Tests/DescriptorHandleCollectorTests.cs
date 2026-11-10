using Lokad.Bash.Helpers;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class DescriptorHandleCollectorTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(256)]
    public void MultipleSourcesPreserveEncounterOrderAndDeduplicateHandles(int count)
    {
        var source = Enumerable.Range(10, count).Select(id => new FileDescriptor(id)).ToArray();
        var last = new FileDescriptor(10 + count);
        var handles = new List<FileDescriptor> { FileDescriptor.Closed };
        var collector = new DescriptorHandleCollector(handles);

        collector.Add(FileDescriptor.DevNull);
        collector.AddRange(source);
        collector.AddRange(source.Reverse().ToArray());
        collector.Add(FileDescriptor.Closed);
        collector.Add(last);

        Assert.Equal(new[] { FileDescriptor.Closed, FileDescriptor.DevNull }.Concat(source).Append(last), handles);
    }
}
