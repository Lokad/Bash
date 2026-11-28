using System;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

/// <summary>A source or parsed-unit owner shared by execution and surviving function namespaces.</summary>
internal sealed class SourceMemory
{
    internal enum ReaderKind { Script, CommandString, ImportedFunction }

    internal static readonly ShellValue EnvironmentDiagnosticName = ShellValue.FromText("environment");
    private const long LegacyCacheEntryBytes = 256;
    // The source reader/executor starts with one reference. Namespace copies and active
    // invocations retain this coarse owner, not individual syntax nodes or value objects.
    private int _references = 1;
    private readonly SourceMemory? _parent;
    private ImmutableDictionary<ReadOnlyMemory<byte>, ParsedScript> _legacySyntax =
        ImmutableDictionary<ReadOnlyMemory<byte>, ParsedScript>.Empty;
    public IMemoryScope Budget { get; }
    public MemoryCapacity SyntaxMemory { get; }
    private ShellValue? _diagnosticName;
    internal ReaderKind Kind { get; }

    public SourceMemory(IMemoryScope budget) : this(budget, parent: null) { }

    public SourceMemory(IMemoryScope budget, SourceMemory? parent) : this(budget, parent, parent?.Kind ?? ReaderKind.Script) { }

    internal SourceMemory(IMemoryScope budget, SourceMemory? parent, ReaderKind kind)
    {
        Budget = budget;
        SyntaxMemory = new MemoryCapacity(budget, 0);
        _parent = parent;
        Kind = kind;
        _diagnosticName = parent?._diagnosticName;
        parent?.Retain();
    }

    internal ShellValue? GetDiagnosticName(bool inFunction) =>
        Kind == ReaderKind.ImportedFunction || (inFunction && Kind == ReaderKind.CommandString)
            ? EnvironmentDiagnosticName : _diagnosticName;

    /// <summary>Copies a reader's identity without extending its source lifetime.</summary>
    internal static SourceMemory CreateIndependentReader(IMemoryBudget budget, SourceMemory? reader)
    {
        var owner = new SourceMemory(budget.CreateScope(), parent: null, reader?.Kind ?? ReaderKind.Script);
        try
        {
            if (reader?._diagnosticName is { } name)
                owner.OwnDiagnosticName(name);
            return owner;
        }
        catch
        {
            owner.Release();
            throw;
        }
    }

    /// <summary>Encodes and owns the reader's host path before publication.</summary>
    internal void OwnDiagnosticName(string path, bool prefixCurrentDirectory)
    {
        var encoding = Utf8Text.StrictUtf8;
        var prefix = prefixCurrentDirectory ? "./"u8 : [];
        var length = checked(prefix.Length + encoding.GetByteCount(path));
        SyntaxMemory.Grow(MemoryCapacity.ArrayBackingBytes<byte>(length));
        var bytes = new byte[length];
        prefix.CopyTo(bytes);
        encoding.GetBytes(path.AsSpan(), bytes.AsSpan(prefix.Length));
        _diagnosticName = ShellValue.FromBytes(bytes);
    }

    /// <summary>Copies a shell reader name for a source whose lifetime is independent of its caller.</summary>
    internal void OwnDiagnosticName(ShellValue name)
    {
        SyntaxMemory.Grow(MemoryCapacity.ArrayBackingBytes<byte>(name.Bytes.Length));
        _diagnosticName = ShellValue.FromBytes(name.Bytes.ToArray());
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
            if (!Parser.TryParseLegacyCapture(input, syntaxMemory, out script, out failure))
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
