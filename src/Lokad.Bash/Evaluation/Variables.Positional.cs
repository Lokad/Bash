using System;
using System.Collections;
using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

/// <summary>Determines when a scoped positional-parameter replacement is restored.</summary>
internal enum PositionalParametersRestorePolicy
{
    /// <summary>Restore the captured frame regardless of replacements inside the scope.</summary>
    Always,

    /// <summary>Keep a global replacement by set since source entry or the last argument restoration.</summary>
    UnlessReplaced
}

public sealed partial class Variables
{
    private PositionalParametersFrame _positional = PositionalParametersFrame.Empty;
    private readonly PositionalParametersView _positionalView;
    private bool _globalPositionalsReplaced;

    internal long PositionalMemoryBytes => _positional.EstimatedMemoryBytes;
    internal bool GlobalPositionalsReplaced => _globalPositionalsReplaced;

    /// <summary>
    /// Gets a stable live read-only view of positional parameters ($1, $2, ...), or replaces their values.
    /// Replacement copies the entries and shares their immutable value bytes.
    /// </summary>
    public IReadOnlyList<ShellValue> PositionalParameters
    {
        get => _positionalView;
        set => ReplacePositionalParameters(value);
    }

    internal void ReplacePositionalParameters(IReadOnlyList<ShellValue> values) =>
        SetPositionalFrame(PositionalParametersFrame.FromValues(values, 0), replacedBySet: false);

    /// <summary>Copies the selected tail and records global replacements for source return.</summary>
    internal void ReplacePositionalParametersFromSet(IReadOnlyList<ShellValue> values, int start, bool inFunction) =>
        SetPositionalFrame(PositionalParametersFrame.FromValues(values, start), replacedBySet: !inFunction || _globalPositionalsReplaced);

    internal void ClearPositionalReplacement() => _globalPositionalsReplaced = false;

    private void SetPositionalFrame(PositionalParametersFrame frame, bool replacedBySet)
    {
        _positional = frame;
        _globalPositionalsReplaced = replacedBySet;
        _savedLocalBindings?.RefreshSavedMemory();
    }

    /// <summary>Installs arguments while retaining the caller's global replacement marker until unwind.</summary>
    internal PositionalParametersScope EnterFunctionPositionals(IReadOnlyList<ShellValue> values) =>
        new(this, values, 0, PositionalParametersRestorePolicy.Always, _globalPositionalsReplaced);

    /// <summary>Saves the caller frame and copies only the selected argument tail.</summary>
    internal PositionalParametersScope EnterSourcePositionals(
        IReadOnlyList<ShellValue> values, int start, PositionalParametersRestorePolicy restorePolicy) =>
        new(this, values, start, restorePolicy, replacedBySet: false);

    /// <summary>Drops leading entries without moving surviving values or changing frame identity.</summary>
    internal bool TryShiftPositionalParameters(long count)
    {
        if (count < 0 || count > _positional.Count)
            return false;
        _positional.Shift((int)count);
        _savedLocalBindings?.RefreshSavedMemory();
        return true;
    }

    /// <summary>
    /// Owns argument entries. A saved frame is inactive until restored; only the active frame shifts.
    /// No append operation exists, so a head index needs no partial compaction or growth policy.
    /// </summary>
    private sealed class PositionalParametersFrame
    {
        public static readonly PositionalParametersFrame Empty = new([], 0);

        private ShellValue[] _values;
        private int _head;
        private long _valueBytes;

        public int Count => _values.Length - _head;
        public long ValueBytes => _valueBytes;
        public long EstimatedMemoryBytes => checked(_valueBytes + (long)PositionalEntryBytes * _values.Length);
        public ShellValue this[int index] => _values[_head + index];

        private PositionalParametersFrame(ShellValue[] values, long valueBytes)
        {
            _values = values;
            _valueBytes = valueBytes;
        }

        public static PositionalParametersFrame FromValues(IReadOnlyList<ShellValue> values, int start)
        {
            var count = values.Count - start;
            if (count == 0)
                return Empty;

            var entries = new ShellValue[count];
            var valueBytes = 0L;
            for (var index = 0; index < count; index++)
            {
                var value = values[start + index];
                entries[index] = value;
                valueBytes = checked(valueBytes + EstimateValueBytes(value));
            }
            return new PositionalParametersFrame(entries, valueBytes);
        }

        public void Shift(int count)
        {
            // Empty frames can be shared. Their only valid shift leaves the frame untouched.
            if (count == 0)
                return;

            var end = _head + count;
            while (_head < end)
            {
                _valueBytes -= EstimateValueBytes(_values[_head]);
                _values[_head++] = ShellValue.Empty;
            }
            if (_head == _values.Length)
            {
                _values = [];
                _head = 0;
            }
        }

        public PositionalParametersFrame CopyActiveValues() => Count == 0
            ? Empty : new PositionalParametersFrame(_values.AsSpan(_head).ToArray(), _valueBytes);
    }

    /// <summary>A live view follows frame switches without exposing owned arrays to callers.</summary>
    private sealed class PositionalParametersView(Variables owner) : IReadOnlyList<ShellValue>
    {
        public int Count => owner._positional.Count;
        public ShellValue this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return owner._positional[index];
            }
        }

        public IEnumerator<ShellValue> GetEnumerator()
        {
            var frame = owner._positional;
            for (var index = 0; index < frame.Count; index++)
                yield return frame[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Retains and restores an inactive owned frame without copying caller arguments.</summary>
    /// <remarks>Dispose nested scopes innermost first; repeated disposal does not restore again.</remarks>
    internal sealed class PositionalParametersScope : IDisposable
    {
        private readonly Variables _variables;
        private readonly PositionalParametersFrame _saved;
        private readonly PositionalParametersRestorePolicy _restorePolicy;
        private bool _isDisposed;

        internal PositionalParametersScope(
            Variables variables, IReadOnlyList<ShellValue> values, int start, PositionalParametersRestorePolicy restorePolicy,
            bool replacedBySet)
        {
            _variables = variables;
            _saved = variables._positional;
            _restorePolicy = restorePolicy;
            variables.SetPositionalFrame(PositionalParametersFrame.FromValues(values, start), replacedBySet);
        }

        /// <summary>Installs a copy of the inactive caller frame for a terminating EXIT action.</summary>
        internal void CopySavedParametersTo(Variables target, EvaluationMemory memory)
        {
            memory.Charge(checked((long)PositionalEntryBytes * _saved.Count));
            target.SetPositionalFrame(_saved.CopyActiveValues(), replacedBySet: false);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            if (_restorePolicy == PositionalParametersRestorePolicy.Always || !_variables._globalPositionalsReplaced)
                _variables.SetPositionalFrame(_saved, replacedBySet: false);
            else
                _variables.ClearPositionalReplacement();
        }
    }
}
