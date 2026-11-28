using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Owns the rendered bytes for a reached modern capture reader and indexes its command units.</summary>
internal sealed class RenderedCaptureInput(IMemoryBudget budget) : IByteMemoryWriter, IDisposable
{
    private readonly ByteMemoryBuilder _bytes = new(0, budget);
    private readonly MemoryCapacity _memory = new(budget, 0);
    private readonly Dictionary<ICommand, Range> _units = new(ReferenceEqualityComparer.Instance);
    private List<Range>? _nestedCaptures;
    private List<Range>? _documents;
    private ICommand? _firstCommand;
    private int _unitStart;
    private int _nestedDepth;

    internal int Length => _bytes.Length;
    internal IMemoryBudget Budget => _memory.Budget;
    internal bool InsideSubstitution => _nestedDepth != 0;
    internal ShellSource Source { get; private set; }
    internal ReaderSpelling Spelling { get; private set; }

    internal void BeginCommand(ICommand command) => _firstCommand ??= command;

    internal void FinishUnit()
    {
        if (_firstCommand == null)
            return;
        _memory.PrepareDictionaryCapacity(_units, 48);
        _units.Add(_firstCommand, _unitStart..Length);
        _firstCommand = null;
        _unitStart = Length;
    }

    internal bool TryGetUnit(ICommand command, out Range range) => _units.TryGetValue(command, out range);

    internal int EnterSubstitution() => _nestedDepth++ == 0 ? Length : -1;

    internal void LeaveSubstitution(int start)
    {
        _nestedDepth--;
        if (start < 0 || !_bytes.ToMemory().Span[start..].Contains((byte)'\n'))
            return;
        _nestedCaptures ??= [];
        _memory.PrepareListCapacity(_nestedCaptures, 1);
        _nestedCaptures.Add(start..Length);
    }

    internal void RecordDocumentLines(int start)
    {
        _documents ??= [];
        _memory.PrepareListCapacity(_documents, 1);
        _documents.Add(start..Length);
    }

    internal void Complete()
    {
        FinishUnit();
        Source = ShellSource.FromBytes(_bytes.ToMemory());
        if (_nestedCaptures == null)
            return;
        var context = new ShellScanContext { SyntaxMemory = _memory, TrackReaderSpelling = true, ScriptInput = Source };
        foreach (var capture in _nestedCaptures)
            context.RecordQuietCaptureLines(Source, capture.Start.Value, capture.End.Value);
        if (_documents != null)
            foreach (var document in _documents)
                context.RecordEchoedCaptureDocument(Source, document.Start.Value, document.End.Value);
        Spelling = context.TakeReaderSpelling();
        var stagingBytes = MemoryCapacity.ArrayBackingBytes<Range>(_nestedCaptures.Capacity)
            + MemoryCapacity.ArrayBackingBytes<Range>(_documents?.Capacity ?? 0);
        _nestedCaptures = null;
        _documents = null;
        _memory.Resize(_memory.Bytes - stagingBytes);
    }

    public bool TryAppend(ReadOnlySpan<byte> bytes) => _bytes.TryAppend(bytes);
    public bool TryAppend(byte value) => _bytes.TryAppend(value);
    public bool TryAppend(int count, byte value) => _bytes.TryAppend(count, value);

    public void Dispose()
    {
        _bytes.Dispose();
        _memory.Dispose();
    }
}
