using System;
using System.Threading;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

/// <summary>A source or parsed-unit owner shared by execution and surviving function namespaces.</summary>
internal sealed class SourceMemory
{
    // The source reader/executor starts with one reference. Namespace copies and active
    // invocations retain this coarse owner, not individual syntax nodes or value objects.
    private int _references = 1;
    private readonly SourceMemory? _parent;
    public IMemoryScope Budget { get; }
    public MemoryCapacity SyntaxMemory { get; }

    public SourceMemory(IMemoryScope budget) : this(budget, parent: null) { }

    public SourceMemory(IMemoryScope budget, SourceMemory? parent)
    {
        Budget = budget;
        SyntaxMemory = new MemoryCapacity(budget, 0);
        _parent = parent;
        parent?.Retain();
    }

    /// <summary>The caller must already hold a live reference while acquiring another.</summary>
    public void Retain() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            SyntaxMemory.Dispose();
            Budget.Dispose();
            _parent?.Release();
        }
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
