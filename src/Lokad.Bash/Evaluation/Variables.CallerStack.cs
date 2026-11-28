using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Evaluation;

public sealed partial class Variables
{
    private static readonly BindingName CallerSourceName = "BASH_SOURCE";
    private static readonly BindingName CallerLineName = "BASH_LINENO";
    private List<CallerEntry>? _callerFrames;
    private long _callerLineBytes;

    private readonly record struct CallerEntry(ShellValue Source, ShellValue Line);

    // Both indexed views read the same frames in reverse order.
    private List<CallerEntry>? VisibleCallers => _callerFrames ?? _commandSource?.Variables.VisibleCallers;
    private long VisibleCallerLineBytes => _callerFrames != null
        ? _callerLineBytes : _commandSource?.Variables.VisibleCallerLineBytes ?? 0;
    private long OwnedCallerMemoryBytes => _callerFrames == null
        ? 0 : MemoryCapacity.ArrayBackingBytes<CallerEntry>(_callerFrames.Capacity) + _callerLineBytes;

    internal int CallerCount => VisibleCallers?.Count ?? 0;

    internal ShellValue GetCallerEntry(int index, bool line)
    {
        var frames = VisibleCallers ?? throw new InvalidOperationException("Expected an active caller frame.");
        var frame = frames[frames.Count - 1 - index];
        return line ? frame.Line : frame.Source;
    }

    internal void InitializeCallerArrays()
    {
        StoreBinding(CallerSourceName, new IndexedSlot(new IndexedArray(this, lines: false)));
        StoreBinding(CallerLineName, new IndexedSlot(new IndexedArray(this, lines: true)));
    }

    internal bool IsCallerArray(BindingName name) =>
        (name == CallerSourceName || name == CallerLineName)
        && TryGetBinding(name, detachBorrowed: false, out var slot)
        && slot is IndexedSlot { Value.IsCallerArray: true };

    /// <summary>Publishes one source and line pair until the reader or function returns.</summary>
    internal CallerFrameScope EnterCaller(ShellValue source, int line, EvaluationMemory memory)
    {
        if (_callerFrames == null)
        {
            var borrowed = VisibleCallers;
            var count = borrowed?.Count ?? 0;
            var capacity = Math.Max(4, checked(count + 1));
            memory.Charge(MemoryCapacity.ArrayBackingBytes<CallerEntry>(capacity));
            var frames = new List<CallerEntry>(capacity);
            if (borrowed != null)
                frames.AddRange(borrowed);
            _callerLineBytes = VisibleCallerLineBytes;
            _callerFrames = frames;
        }
        else
            memory.PrepareListCapacity(_callerFrames, 1);

        Span<byte> buffer = stackalloc byte[24];
        var digits = NumberFormatting.FormatNumber(line, buffer);
        var retainedBytes = digits.Length > 1 ? MemoryCapacity.ArrayBackingBytes<byte>(digits.Length) : 0;
        if (retainedBytes != 0)
            memory.Charge(retainedBytes);
        var lineValue = digits.Length == 1 ? ShellValue.FromByte(digits[0]) : ShellValue.FromBytes(digits.ToArray());
        _callerFrames.Add(new CallerEntry(source, lineValue));
        _callerLineBytes = checked(_callerLineBytes + retainedBytes);
        return new CallerFrameScope(this, retainedBytes);
    }

    internal readonly struct CallerFrameScope(Variables? owner, long lineBytes) : IDisposable
    {
        public void Dispose()
        {
            if (owner?._callerFrames is not { } frames)
                return;
            frames.RemoveAt(frames.Count - 1);
            owner._callerLineBytes -= lineBytes;
        }
    }
}
