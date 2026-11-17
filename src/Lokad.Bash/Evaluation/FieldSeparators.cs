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

    /// <summary>The separator bytes, including any malformed UTF-8.</summary>
    private readonly ShellValue _value;

    // A published snapshot never grows. A child can share completed indexes, but
    // new backing belongs to a replacement snapshot in that child's variable scope.
    private readonly HashSet<Rune>? _characters;
    private readonly ByteMembership? _byteMembership;

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

    /// <summary>Gets the configured bytes without decoding or copying them.</summary>
    public ReadOnlyMemory<byte> Bytes => _value.Bytes;

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
        var prepared = new FieldSeparators(_value, characters, byteMembership);
        if (ReferenceEquals(variables.CachedFieldSeparators, this))
            variables.CachedFieldSeparators = prepared;
        return prepared;
    }

    /// <summary>Queries prepared scalar membership; default and empty separators need no index.</summary>
    public bool Contains(Rune value) => ReferenceEquals(this, Default) ? HasWhitespaceSemantics(value)
        : !IsEmpty && (_characters ?? throw new InvalidOperationException("Scalar membership was not prepared.")).Contains(value);

    /// <summary>Queries prepared byte membership, including UTF-8 continuation bytes.</summary>
    public bool Contains(byte value) => ReferenceEquals(this, Default) ? value is (byte)' ' or (byte)'\t' or (byte)'\n'
        : !IsEmpty && (_byteMembership ?? throw new InvalidOperationException("Byte membership was not prepared.")).Contains(value);

    private readonly record struct ByteMembership(ulong First, ulong Second, ulong Third, ulong Fourth)
    {
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
