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
    internal static readonly FieldSeparators Default = new(ShellValue.FromText(DefaultValue));

    /// <summary>The separator bytes, including any malformed UTF-8.</summary>
    private readonly ShellValue _value;

    // Queries of IsEmpty/First need no indexes. Cloned scopes share completed indexes;
    // concurrent first readers may build independently without waiting on another evaluator.
    private HashSet<Rune>? _characters;
    private SearchValues<byte>? _byteMembership;

    /// <summary>Initializes separators from an <c>IFS</c> value.</summary>
    private FieldSeparators(ShellValue value)
    {
        _value = value;
    }

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
            variables.CachedFieldSeparators = cached = new FieldSeparators(value);
        return cached;
    }

    /// <summary>Returns whether the configured separators contain a character.</summary>
    public bool Contains(Rune value)
    {
        var characters = Volatile.Read(ref _characters);
        if (characters == null)
        {
            var built = new HashSet<Rune>();
            var remaining = Bytes.Span;
            while (!remaining.IsEmpty)
            {
                var status = Rune.DecodeFromUtf8(remaining, out var character, out var length);
                if (status == OperationStatus.Done)
                    built.Add(character);
                remaining = remaining[(status == OperationStatus.Done ? length : 1)..];
            }
            characters = Interlocked.CompareExchange(ref _characters, built, null) ?? built;
        }
        return characters.Contains(value);
    }

    /// <summary>Tests a malformed input byte against all configured bytes, including UTF-8 continuation bytes.</summary>
    public bool Contains(byte value)
    {
        var membership = Volatile.Read(ref _byteMembership);
        if (membership == null)
        {
            var built = SearchValues.Create(Bytes.Span);
            membership = Interlocked.CompareExchange(ref _byteMembership, built, null) ?? built;
        }
        return membership.Contains(value);
    }

    /// <summary>
    /// Returns whether a character has whitespace semantics when present in <c>IFS</c>.
    /// </summary>
    public static bool HasWhitespaceSemantics(Rune value) => value.Value is ' ' or '\t' or '\n';
}
