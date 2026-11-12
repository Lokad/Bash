using System;
using System.Collections;
using System.Collections.Generic;

namespace Lokad.Bash.Evaluation;

/// <summary>Determines when a scoped positional-parameter replacement is restored.</summary>
internal enum PositionalParametersRestorePolicy
{
    /// <summary>Restore the captured frame regardless of replacements inside the scope.</summary>
    Always,

    /// <summary>Restore only when the scope's frame was not replaced wholesale.</summary>
    UnlessReplaced
}

public sealed partial class Variables
{
    private PositionalParametersFrame _positional = new([], 0);
    private readonly PositionalParametersView _positionalView;

    internal long PositionalMemoryBytes => _positional.EstimatedMemoryBytes;

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
        ReplacePositionalParameters(values, 0);

    /// <summary>Copies the selected argument tail once into an owned frame.</summary>
    internal void ReplacePositionalParameters(IReadOnlyList<ShellValue> values, int start) =>
        _positional = new PositionalParametersFrame(values, start);

    internal PositionalParametersScope EnterPositionalParametersScope(
        IReadOnlyList<ShellValue> values, PositionalParametersRestorePolicy restorePolicy) =>
        new(this, values, restorePolicy);

    /// <summary>Drops leading entries without moving surviving values or changing frame identity.</summary>
    internal bool TryShiftPositionalParameters(int count)
    {
        if (count < 0 || count > _positional.Count)
            return false;
        _positional.Shift(count);
        return true;
    }

    /// <summary>
    /// Owns argument entries. A saved frame is inactive until restored; only the active frame shifts.
    /// No append operation exists, so a head index needs no partial compaction or growth policy.
    /// </summary>
    private sealed class PositionalParametersFrame
    {
        private ShellValue[] _values;
        private int _head;
        private long _valueBytes;

        public int Count => _values.Length - _head;
        public long EstimatedMemoryBytes => checked(_valueBytes + (long)PositionalEntryBytes * _values.Length);
        public ShellValue this[int index] => _values[_head + index];

        public PositionalParametersFrame(IReadOnlyList<ShellValue> values, int start)
        {
            var count = values.Count - start;
            _values = count == 0 ? [] : new ShellValue[count];
            for (var index = 0; index < count; index++)
            {
                var value = values[start + index];
                _values[index] = value;
                _valueBytes = checked(_valueBytes + EstimateValueBytes(value));
            }
        }

        public void Shift(int count)
        {
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
        private readonly PositionalParametersFrame _entered;
        private readonly PositionalParametersRestorePolicy _restorePolicy;
        private bool _isDisposed;

        internal PositionalParametersScope(
            Variables variables, IReadOnlyList<ShellValue> values, PositionalParametersRestorePolicy restorePolicy)
        {
            _variables = variables;
            _saved = variables._positional;
            _entered = new PositionalParametersFrame(values, 0);
            _restorePolicy = restorePolicy;
            variables._positional = _entered;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            if (_restorePolicy == PositionalParametersRestorePolicy.Always || ReferenceEquals(_variables._positional, _entered))
                _variables._positional = _saved;
        }
    }
}
