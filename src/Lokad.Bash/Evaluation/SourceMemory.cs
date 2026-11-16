using System;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>One sourced buffer's admission, shared by its execution and surviving function namespaces.</summary>
internal sealed class SourceMemory(IMemoryScope budget)
{
    // The source reader/executor starts with one reference. Namespace copies and active
    // invocations retain this coarse owner, not individual syntax nodes or value objects.
    private int _references = 1;
    public IMemoryScope Budget { get; } = budget;

    /// <summary>The caller must already hold a live reference while acquiring another.</summary>
    public void Retain() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
            Budget.Dispose();
    }

    /// <summary>One command's borrow, retained through preparation, execution and cleanup.</summary>
    public readonly struct Borrow : IDisposable
    {
        private readonly SourceMemory? _owner;
        public Borrow(SourceMemory? owner)
        {
            _owner = owner;
            owner?.Retain();
        }
        public void Dispose() => _owner?.Release();
    }
}
