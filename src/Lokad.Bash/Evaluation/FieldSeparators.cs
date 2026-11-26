using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Evaluation;

/// <summary>Represents the shell's internal field separators.</summary>
internal sealed class FieldSeparators
{
    /// <summary>The separators used when <c>IFS</c> is unset.</summary>
    private const string DefaultValue = " \t\n";

    /// <summary>The shared separators used when <c>IFS</c> is unset.</summary>
    internal static readonly FieldSeparators Default = new(ShellValue.FromText(DefaultValue), null, null);
    internal static readonly FieldSeparators Empty = new(ShellValue.Empty, null, null);

    /// <summary>The separator bytes, including any malformed UTF-8.</summary>
    private readonly ShellValue _value;

    // The membership indexes are immutable after publication. New index backing
    // belongs to a replacement snapshot in the active variable scope.
    private readonly HashSet<Rune>? _characters;
    private readonly ByteMembership? _byteMembership;
    private volatile CaseQuoteBytes _caseQuoteDelimiters;
    private byte[]? _compactFirst;

    /// <summary>Initializes separators from an <c>IFS</c> value.</summary>
    private FieldSeparators(ShellValue value, HashSet<Rune>? characters, ByteMembership? byteMembership)
    {
        _value = value;
        _characters = characters;
        _byteMembership = byteMembership;
    }

    /// <summary>Retains value bytes and actual membership capacity through the variable owner.</summary>
    internal long EstimatedMemoryBytes => checked(Variables.EstimateValueBytes(_value)
        + (_characters != null || _byteMembership.HasValue ? 256L : 0)
        + 16L * (_characters?.EnsureCapacity(0) ?? 0));

    /// <summary>Gets whether no separator characters are configured.</summary>
    public bool IsEmpty => _value.IsEmpty;

    /// <summary>Gets the first UTF-8 character or malformed byte; requires non-empty IFS.</summary>
    public ShellValue First
    {
        get
        {
            return ShellValue.FromBytes(Bytes[..Utf8Text.FirstCharacterLength(Bytes.Span)]);
        }
    }

    /// <summary>Gets the first separator from nonempty IFS without retaining the rest of its value.</summary>
    internal ShellValue CompactFirst
    {
        get
        {
            var cached = Volatile.Read(ref _compactFirst);
            if (cached != null)
                return ShellValue.FromBytes(cached);

            var first = First;
            if (first.Bytes.Length == Bytes.Length)
                return first;
            if (first.Bytes.Span is [var single])
                return ShellValue.FromByte(single);

            // The snapshot retains at most one compact UTF-8 scalar and may be shared by children.
            var compact = first.Bytes.Span.ToArray();
            return ShellValue.FromBytes(Interlocked.CompareExchange(ref _compactFirst, compact, null) ?? compact);
        }
    }

    /// <summary>Gets the configured bytes without decoding or copying them.</summary>
    public ReadOnlyMemory<byte> Bytes => _value.Bytes;

    /// <summary>Finds which case quote markers can act as delimiters in this IFS value.</summary>
    internal CaseQuoteBytes CaseQuoteDelimiters
    {
        get
        {
            var delimiters = _caseQuoteDelimiters;
            if (delimiters == CaseQuoteBytes.None)
            {
                // Publish both bits together. A concurrent read may compute the same flags.
                delimiters = CaseQuoting.GetCaseQuoteBytes(Bytes.Span) | CaseQuoteBytes.Captured;
                _caseQuoteDelimiters = delimiters;
            }
            return delimiters & ~CaseQuoteBytes.Captured;
        }
    }

    /// <summary>
    /// Returns the current <c>IFS</c> value, or the shell default when it is unset.
    /// </summary>
    public static FieldSeparators FromVariables(Variables variables)
    {
        if (!variables.TryGetScalar("IFS", out var value))
            return variables.CachedFieldSeparators = Default;

        // Resolve aliases every time; immutable memory identity detects changes without value scans.
        var cached = variables.CachedFieldSeparators;
        if (!cached.Bytes.Equals(value.Bytes))
            variables.CachedFieldSeparators = cached = new FieldSeparators(value, null, null);
        return cached;
    }

    /// <summary>Admits and prepares scalar membership in the active variable owner, preserving shared snapshots.</summary>
    internal FieldSeparators PrepareCharacters(Variables variables, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (_characters != null || ReferenceEquals(this, Default) || IsEmpty)
            return this;
        cancellationToken.ThrowIfCancellationRequested();
        var baseline = memory.Workspace.Bytes;
        var completed = false;
        try
        {
            memory.Charge(256);
            cancellationToken.ThrowIfCancellationRequested();
            var characters = new HashSet<Rune>();
            var capacity = 0;
            var remaining = Bytes.Span;
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = Rune.DecodeFromUtf8(remaining, out var character, out var length);
                if (status == OperationStatus.Done)
                {
                    if (characters.Count == capacity && !characters.Contains(character))
                    {
                        var requested = capacity == 0 ? 3 : checked(2 * capacity);
                        // Cover buckets, entries, prime rounding and both generations.
                        memory.Charge(checked(128L + 32L * requested));
                        cancellationToken.ThrowIfCancellationRequested();
                        capacity = characters.EnsureCapacity(requested);
                        // EnsureCapacity has finished using its previous arrays.
                        memory.Workspace.Resize(checked(baseline + 256L + 16L * capacity));
                    }
                    characters.Add(character);
                }
                remaining = remaining[(status == OperationStatus.Done ? length : 1)..];
            }
            cancellationToken.ThrowIfCancellationRequested();
            var prepared = Publish(variables, characters, _byteMembership);
            completed = true;
            return prepared;
        }
        finally
        {
            // The synchronous builder has no outstanding users after failure.
            // Relinquish partial backing before the command reports its refusal.
            if (!completed) memory.Workspace.Resize(baseline);
        }
    }

    /// <summary>Prepares fixed byte membership, including bytes inside valid UTF-8 separators.</summary>
    internal FieldSeparators PrepareBytes(Variables variables, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (_byteMembership.HasValue || ReferenceEquals(this, Default) || IsEmpty)
            return this;
        cancellationToken.ThrowIfCancellationRequested();
        memory.Charge(256);
        cancellationToken.ThrowIfCancellationRequested();
        Span<ulong> bits = stackalloc ulong[4];
        bits.Clear();
        foreach (var value in Bytes.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bits[value >> 6] |= 1UL << (value & 63);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Publish(variables, _characters, new ByteMembership(bits[0], bits[1], bits[2], bits[3]));
    }

    private FieldSeparators Publish(Variables variables, HashSet<Rune>? characters, ByteMembership? byteMembership)
    {
        var prepared = new FieldSeparators(_value, characters, byteMembership)
        {
            _caseQuoteDelimiters = _caseQuoteDelimiters,
            _compactFirst = Volatile.Read(ref _compactFirst),
        };
        if (ReferenceEquals(variables.CachedFieldSeparators, this))
            variables.CachedFieldSeparators = prepared;
        return prepared;
    }

    /// <summary>Queries prepared scalar membership; default and empty separators need no index.</summary>
    public bool Contains(Rune value) => ReferenceEquals(this, Default) ? HasWhitespaceSemantics(value)
        : !IsEmpty && (_characters ?? throw new InvalidOperationException("Scalar membership was not prepared.")).Contains(value);

    /// <summary>Queries prepared byte membership, including UTF-8 continuation bytes.</summary>
    public bool Contains(byte value) => PreparedBytes.Contains(value);

    private ByteMembership PreparedBytes =>
        ReferenceEquals(this, Default) ? ByteMembership.Default
            : IsEmpty ? default : _byteMembership ?? throw new InvalidOperationException("Byte membership was not prepared.");

    /// <summary>Copies byte membership without borrowing the variable's IFS value or cache.</summary>
    internal static ByteMembership CaptureSourceBytes(
        ref FieldSeparators separators, Variables variables, EvaluationMemory memory, CancellationToken cancellationToken)
    {
        if (separators.Bytes.Span is [var single])
        {
            // A single delimiter fits directly in the capture without warming the variable cache.
            var bit = 1UL << (single & 63);
            return (single >> 6) switch
            {
                0 => new(bit, 0, 0, 0),
                1 => new(0, bit, 0, 0),
                2 => new(0, 0, bit, 0),
                _ => new(0, 0, 0, bit),
            };
        }
        separators = separators.PrepareBytes(variables, memory, cancellationToken);
        return separators.PreparedBytes;
    }

    internal readonly record struct ByteMembership(ulong First, ulong Second, ulong Third, ulong Fourth)
    {
        internal static readonly ByteMembership Default = new((1UL << ' ') | (1UL << '\t') | (1UL << '\n'), 0, 0, 0);

        public bool Contains(byte value) => ((value >> 6) switch
        {
            0 => First,
            1 => Second,
            2 => Third,
            _ => Fourth
        } & (1UL << (value & 63))) != 0;
    }

    /// <summary>
    /// Returns whether a character has whitespace semantics when present in <c>IFS</c>.
    /// </summary>
    public static bool HasWhitespaceSemantics(Rune value) => value.Value is ' ' or '\t' or '\n';
}
