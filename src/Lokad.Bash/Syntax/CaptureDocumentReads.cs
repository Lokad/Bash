using System;
using System.Collections.Generic;
using System.Threading;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Collects the documents encountered in rendered capture spelling, borrowing the retained syntax.</summary>
internal sealed class CaptureDocumentReads(IMemoryBudget budget) : IByteMemoryWriter, IDisposable
{
    private readonly MemoryCapacity _memory = new(budget, 0);
    private readonly Dictionary<CommandSubstitutionSyntax.Parsed, ReadSequence> _captures =
        new(ReferenceEqualityComparer.Instance);
    private List<Redirection>? _pending;
    private bool _hasOpeningBrace;

    internal readonly record struct ReadSequence(IReadOnlyList<Redirection> Documents, bool HasOpeningBrace);

    internal IMemoryBudget Budget => _memory.Budget;

    internal ReadSequence Get(CommandSubstitutionSyntax.Parsed capture, CancellationToken cancellationToken)
    {
        if (_captures.TryGetValue(capture, out var documents))
            return documents;

        try
        {
            FunctionDefinitionFormatter.CollectCaptureDocuments(capture, this, cancellationToken);
            documents = new ReadSequence(_pending is { } pending ? pending : Array.Empty<Redirection>(), _hasOpeningBrace);
            _memory.PrepareDictionaryCapacity(_captures, 48);
            _captures.Add(capture, documents);
            return documents;
        }
        finally
        {
            _pending = null;
            _hasOpeningBrace = false;
        }
    }

    internal void Add(Redirection document)
    {
        _pending ??= [];
        _memory.PrepareListCapacity(_pending, 1);
        _pending.Add(document);
        if (_hasOpeningBrace)
            return;
        foreach (var part in document.Target.Parts)
            if (part.GetUnexpandedValue().Bytes.Span.Contains((byte)'{'))
            {
                _hasOpeningBrace = true;
                return;
            }
        _hasOpeningBrace = document.DelimiterSyntax is { } delimiter && delimiter.Value.AsSpan().Contains((byte)'{');
    }

    // The formatter supplies document references at collection boundaries.
    bool IByteMemoryWriter.TryAppend(ReadOnlySpan<byte> bytes)
    {
        _hasOpeningBrace = _hasOpeningBrace || bytes.Contains((byte)'{');
        return true;
    }

    bool IByteMemoryWriter.TryAppend(byte value)
    {
        _hasOpeningBrace |= value == (byte)'{';
        return true;
    }

    bool IByteMemoryWriter.TryAppend(int count, byte value)
    {
        _hasOpeningBrace |= count > 0 && value == (byte)'{';
        return true;
    }

    public void Dispose() => _memory.Dispose();
}
