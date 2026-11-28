using System;
using System.Collections.Generic;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Evaluation;

internal enum CallerArrayKind { Source, Line, FunctionName }
internal enum CallerFrameKind { File, Source, Function }

public sealed partial class Variables
{
    private static readonly BindingName CallerSourceName = "BASH_SOURCE";
    private static readonly BindingName CallerLineName = "BASH_LINENO";
    private static readonly BindingName CallerFunctionName = "FUNCNAME";
    private static readonly ShellValue SourcedInvocation = ShellValue.FromText("source");
    private static readonly ShellValue MainInvocation = ShellValue.FromText("main");
    private List<CallerEntry>? _callerFrames;
    private long _callerLineBytes;
    private long _callerNameBindingVersion;
    private int _callerFunctionDepth;
    private bool _keepCallerNamesVisible;
    internal bool UsesBashCallerOutput { get; set; }

    private readonly record struct CallerEntry(ShellValue Source, ShellValue Line, ShellValue Name);

    // The indexed views read the same frames in reverse order.
    private List<CallerEntry>? VisibleCallers => _callerFrames ?? _commandSource?.Variables.VisibleCallers;
    private long VisibleCallerLineBytes => _callerFrames != null
        ? _callerLineBytes : _commandSource?.Variables.VisibleCallerLineBytes ?? 0;
    private int VisibleCallerFunctionDepth => _callerFrames != null
        ? _callerFunctionDepth : _commandSource?.Variables.VisibleCallerFunctionDepth ?? 0;
    private bool KeepCallerNamesVisible => _callerFrames != null
        ? _keepCallerNamesVisible : _commandSource?.Variables.KeepCallerNamesVisible == true;
    private long OwnedCallerMemoryBytes => _callerFrames == null
        ? 0 : MemoryCapacity.ArrayBackingBytes<CallerEntry>(_callerFrames.Capacity) + _callerLineBytes;

    internal int CallerCount => VisibleCallers?.Count ?? 0;
    internal bool HasActiveCallerFunction => VisibleCallerFunctionDepth != 0;
    internal bool CallerNamesVisible => HasActiveCallerFunction || KeepCallerNamesVisible;

    internal void RetainCallerNameVisibilityOnDiscard() => _keepCallerNamesVisible = true;

    internal ShellValue GetCallerEntry(int index, CallerArrayKind kind)
    {
        var frames = VisibleCallers ?? throw new InvalidOperationException("Expected an active caller frame.");
        var frame = frames[frames.Count - 1 - index];
        return kind switch
        {
            CallerArrayKind.Line => frame.Line,
            CallerArrayKind.FunctionName => frame.Name,
            _ => frame.Source
        };
    }

    internal void InitializeCallerArrays()
    {
        StoreBinding(CallerSourceName, new IndexedSlot(new IndexedArray(this, CallerArrayKind.Source)));
        StoreBinding(CallerLineName, new IndexedSlot(new IndexedArray(this, CallerArrayKind.Line)));
        StoreBinding(CallerFunctionName, new IndexedSlot(new IndexedArray(this, CallerArrayKind.FunctionName)));
    }

    internal bool IsCallerArray(BindingName name) =>
        (name == CallerSourceName || name == CallerLineName || name == CallerFunctionName)
        && TryGetBinding(name, detachBorrowed: false, out var slot)
        && slot is IndexedSlot { Value.IsCallerArray: true };

    internal bool IsProtectedCallerArray(BindingName name) => name != CallerFunctionName && IsCallerArray(name);

    internal IndexedArray? FunctionNamesForCaller => ReadFunctionNameSlot(detachBorrowed: false, out _)?.Value;

    internal void KeepImportedFunctionName(BindingName name)
    {
        if (name == CallerFunctionName && IsCallerArray(name))
            ClearBinding(name);
    }

    private IndexedSlot? ReadFunctionNameSlot(bool detachBorrowed, out BindingName name)
    {
        name = CallerFunctionName;
        if (!TryGetBinding(name, detachBorrowed, out var value))
            return null;
        if (value is not NamerefSlot)
            return value as IndexedSlot;
        if (!TryResolveBinding(name, selectOwner: null, out var binding)
            || binding.Reference.Kind != VariableReferenceKind.Name)
            return null;
        name = binding.Name;
        return TryGetBinding(name, detachBorrowed, out value) ? value as IndexedSlot : null;
    }

    /// <summary>Publishes one frame until the reader or function returns.</summary>
    internal CallerFrameScope EnterCaller(ShellValue source, int line, CallerFrameKind kind, ShellValue name, EvaluationMemory memory)
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
            _callerFunctionDepth = VisibleCallerFunctionDepth;
            _keepCallerNamesVisible = KeepCallerNamesVisible;
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
        var functionName = kind switch
        {
            CallerFrameKind.File => MainInvocation,
            CallerFrameKind.Source => SourcedInvocation,
            _ => name
        };
        var names = ReadFunctionNameSlot(detachBorrowed: true, out var bindingName);
        if (names != null && names.CallerNameBindingVersion == 0)
            names.CallerNameBindingVersion = ++_callerNameBindingVersion;
        if (names?.Value.UsesSparseCallerNames == true)
        {
            using var change = TrackBindingMemory(bindingName);
            names.Value.PushCallerName(functionName, memory);
        }
        _callerFrames.Add(new CallerEntry(source, lineValue, functionName));
        _callerLineBytes = checked(_callerLineBytes + retainedBytes);
        if (kind == CallerFrameKind.Function)
        {
            if (_callerFunctionDepth == 0)
                _keepCallerNamesVisible = false;
            _callerFunctionDepth++;
        }
        return new CallerFrameScope(this, retainedBytes, kind, bindingName, names?.CallerNameBindingVersion ?? 0);
    }

    internal readonly struct CallerFrameScope(
        Variables? owner, long lineBytes, CallerFrameKind kind, BindingName bindingName, long nameVersion) : IDisposable
    {
        public void Dispose()
        {
            if (owner?._callerFrames is not { } frames)
                return;
            // A same-binding compound assignment can replace the array while the frame is active.
            // Scope recreation and a retargeted nameref select a different binding.
            if (nameVersion != 0 && owner.ReadFunctionNameSlot(detachBorrowed: false, out var selected) is { } names
                && names.Value.UsesSparseCallerNames && names.CallerNameBindingVersion == nameVersion && selected == bindingName)
            {
                using var change = owner.TrackBindingMemory(selected);
                names.Value.PopCallerName();
            }
            frames.RemoveAt(frames.Count - 1);
            owner._callerLineBytes -= lineBytes;
            if (kind == CallerFrameKind.Function)
                owner._callerFunctionDepth--;
        }
    }
}
