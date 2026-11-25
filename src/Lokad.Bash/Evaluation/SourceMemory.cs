using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>A source or parsed-unit owner shared by execution and surviving function namespaces.</summary>
internal sealed class SourceMemory
{
    private const long LegacyCacheEntryBytes = 256;
    // The source reader/executor starts with one reference. Namespace copies and active
    // invocations retain this coarse owner, not individual syntax nodes or value objects.
    private int _references = 1;
    private readonly SourceMemory? _parent;
    private ImmutableDictionary<ReadOnlyMemory<byte>, ParsedScript> _legacySyntax =
        ImmutableDictionary<ReadOnlyMemory<byte>, ParsedScript>.Empty;
    public IMemoryScope Budget { get; }
    public MemoryCapacity SyntaxMemory { get; }
    internal string? DiagnosticPath { get; private set; }

    public SourceMemory(IMemoryScope budget) : this(budget, parent: null) { }

    public SourceMemory(IMemoryScope budget, SourceMemory? parent)
    {
        Budget = budget;
        SyntaxMemory = new MemoryCapacity(budget, 0);
        _parent = parent;
        DiagnosticPath = parent?.DiagnosticPath;
        parent?.Retain();
    }

    /// <summary>Admits the reader path before publication through a root source owner.</summary>
    internal void OwnDiagnosticPath(string path, bool prefixCurrentDirectory)
    {
        var length = path.Length + (prefixCurrentDirectory ? 2L : 0L);
        SyntaxMemory.Grow(24L + 2L * length);
        DiagnosticPath = prefixCurrentDirectory ? "./" + path : path;
    }

    /// <summary>The caller must already hold a live reference while acquiring another.</summary>
    public void Retain() => Interlocked.Increment(ref _references);

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0)
        {
            _legacySyntax = ImmutableDictionary<ReadOnlyMemory<byte>, ParsedScript>.Empty;
            SyntaxMemory.Dispose();
            Budget.Dispose();
            _parent?.Release();
        }
    }

    /// <summary>Prepares one reached backtick body under the owner of its original spelling.</summary>
    /// <remarks>The caller holds a live source reference through parsing and consumption.</remarks>
    internal bool TryPrepareLegacy(CommandSubstitutionSyntax.Legacy legacy, CancellationToken cancellationToken,
        [NotNullWhen(true)] out ParsedScript? script, [NotNullWhen(false)] out ParserFailure? failure)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ReadOnlyMemory equality identifies a physical source slice.
        var key = legacy.Spelling.Bytes;
        var snapshot = Volatile.Read(ref _legacySyntax);
        failure = null;
        if (snapshot.TryGetValue(key, out script))
            return true;

        var parseScope = Budget.CreateScope();
        var published = false;
        try
        {
            using var syntaxMemory = new MemoryCapacity(parseScope, LegacyCacheEntryBytes);
            var input = legacy.Source.Bytes;
            if (legacy.Origin == LegacySubstitutionOrigin.HereDocument)
            {
                // Here-document decoding happened in the active command's temporary storage.
                syntaxMemory.Grow(32L + input.Length);
                input = input.ToArray();
            }
            if (!Parser.TryParseScript(input, new ShellScanContext { SyntaxMemory = syntaxMemory, ReadsCommandString = true },
                    out script, out failure))
                return false;
            syntaxMemory.DetachReservation();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Reserve the replacement path and another snapshot through publication.
                // Each live entry's reservation continues to cover its retained map node.
                using var overlap = Budget.Reserve(LegacyCacheEntryBytes * (2 + BitOperations.Log2((uint)snapshot.Count + 1)));
                var updated = snapshot.Add(key, script);
                var observed = Interlocked.CompareExchange(ref _legacySyntax, updated, snapshot);
                if (ReferenceEquals(snapshot, observed))
                {
                    published = true;
                    return true;
                }
                if (observed.TryGetValue(key, out var installed))
                {
                    script = installed;
                    return true;
                }
                snapshot = observed;
            }
        }
        finally
        {
            // Ancestor disposal refunds published parse scopes with the syntax map.
            if (!published)
                parseScope.Dispose();
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
