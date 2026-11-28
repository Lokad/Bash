using System.Buffers;

namespace Lokad.Bash.Tests;

internal sealed class ObservedByteMemory(byte[] bytes) : MemoryManager<byte>
{
    internal int Reads { get; private set; }

    public override Span<byte> GetSpan()
    {
        Reads++;
        return bytes;
    }

    public override MemoryHandle Pin(int elementIndex) => throw new NotSupportedException();
    public override void Unpin() => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { }
}
