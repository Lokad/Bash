using System.Buffers;
using System.Runtime.InteropServices;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Syntax;

/// <summary>Tracks physical lines collected by a capture while its header is still buffered.</summary>
internal sealed class HereDocumentReadAhead(ShellSource source)
{
    private readonly List<Range> _bodies = [];
    private readonly List<HeaderAdvance> _headerAdvances = [];
    private int _headerEnd;
    private SourceLineCursor _lines;

    internal int CaptureCount => _headerAdvances.Count;

    internal int FindBodyStart(int afterCloser)
    {
        if (_headerEnd <= afterCloser)
            _headerEnd = FindNextLineStart(source, afterCloser);
        return SkipBodies(_headerEnd);
    }

    internal static int FindNextLineStart(ShellSource input, int position)
    {
        var newline = input.AsSpan()[position..].IndexOf((byte)'\n');
        return newline < 0 ? input.Length : position + newline + 1;
    }

    internal void Add(int afterCloser, int beginning, int end, MemoryCapacity? memory)
    {
        if (beginning != end)
        {
            if (_bodies.Count != 0 && _bodies[^1].End.Value == beginning)
                _bodies[^1] = _bodies[^1].Start..end;
            else
            {
                memory?.PrepareListCapacity(_bodies, 1);
                _bodies.Add(beginning..end);
            }
        }

        var lines = source.AsSpan(beginning, end - beginning).Count((byte)'\n');
        if (beginning != end && source[end - 1] != (byte)'\n')
            lines++;
        if (_headerAdvances.Count != 0 && _headerAdvances[^1].End == _headerEnd)
            lines += _headerAdvances[^1].Lines;
        memory?.PrepareListCapacity(_headerAdvances, 1);
        _headerAdvances.Add(new HeaderAdvance(afterCloser, _headerEnd, lines));
    }

    internal int GetBufferedLineAdvance(ShellSource view, int position)
    {
        if (!TryGetSourceOffset(view, out var offset))
            return 0;
        position += offset;
        var low = 0;
        var high = _headerAdvances.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_headerAdvances[middle].Start <= position)
                low = middle + 1;
            else
                high = middle;
        }
        return low > 0 && position < _headerAdvances[low - 1].End ? _headerAdvances[low - 1].Lines : 0;
    }

    internal int GetLine(int position) => _lines.GetLine(source, position);

    internal int SkipBodies(ShellSource view, int position)
    {
        if (_bodies.Count == 0 || !TryGetSourceOffset(view, out var offset))
            return position;
        return Math.Min(view.Length, SkipBodies(offset + position) - offset);
    }

    internal ShellSource CopyWithoutBodies(ShellSource view, ShellScanContext context)
    {
        if (_bodies.Count == 0 || !TryGetSourceOffset(view, out var offset))
            return view;
        var end = offset + view.Length;
        var first = FindFirstBodyEndingAfter(offset);
        if (first == _bodies.Count || _bodies[first].Start.Value >= end)
            return view;

        using var buffer = context.CreateByteBuilder(0);
        AppendWithoutBodies(buffer, offset, end, first);
        return ShellSource.FromBytes(buffer.ToCompactMemory(context.SyntaxMemory));
    }

    internal void AppendWithoutBodies(ByteMemoryBuilder buffer, ShellSource view)
    {
        if (!TryGetSourceOffset(view, out var offset))
        {
            buffer.Append(view.AsSpan());
            return;
        }
        AppendWithoutBodies(buffer, offset, offset + view.Length, FindFirstBodyEndingAfter(offset));
    }

    private void AppendWithoutBodies(ByteMemoryBuilder buffer, int offset, int end, int first)
    {
        var position = offset;
        for (var index = first; index < _bodies.Count; index++)
        {
            var body = _bodies[index];
            if (body.Start.Value >= end)
                break;
            if (body.Start.Value > position)
                buffer.Append(source.AsSpan(position, body.Start.Value - position));
            position = Math.Min(end, body.End.Value);
        }
        buffer.Append(source.AsSpan(position, end - position));
    }

    internal bool TryGetSourceOffset(ShellSource view, out int offset)
    {
        if (view.Bytes.Equals(source.Bytes))
        {
            offset = 0;
            return true;
        }
        if (MemoryMarshal.TryGetArray(source.Bytes, out var origin) && MemoryMarshal.TryGetArray(view.Bytes, out var slice)
            && ReferenceEquals(origin.Array, slice.Array))
        {
            offset = slice.Offset - origin.Offset;
            return offset >= 0 && view.Length <= source.Length - offset;
        }
        if (MemoryMarshal.TryGetMemoryManager<byte, MemoryManager<byte>>(source.Bytes, out var owner, out var start, out _)
            && MemoryMarshal.TryGetMemoryManager<byte, MemoryManager<byte>>(view.Bytes, out var other, out var index, out _)
            && ReferenceEquals(owner, other))
        {
            offset = index - start;
            return offset >= 0 && view.Length <= source.Length - offset;
        }

        offset = 0;
        return false;
    }

    private int SkipBodies(int position)
    {
        var first = FindFirstBodyEndingAfter(position);
        return first < _bodies.Count && _bodies[first].Start.Value <= position ? _bodies[first].End.Value : position;
    }

    private int FindFirstBodyEndingAfter(int position)
    {
        var low = 0;
        var high = _bodies.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_bodies[middle].End.Value <= position)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private readonly record struct HeaderAdvance(int Start, int End, int Lines);
}
