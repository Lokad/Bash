using System;
using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash;

/// <summary>A whole binding's identity, including literal local names containing byte-valued selectors.</summary>
public readonly struct BindingName : IEquatable<BindingName>
{
    // Keep ordinary identifier lookup allocation-free. Non-UTF-8 literal names retain their bytes.
    private readonly string? _text;
    private readonly ShellValue _bytes;

    private BindingName(string text, ShellValue bytes) { _text = text; _bytes = bytes; }
    private BindingName(ShellValue bytes) => _bytes = bytes;

    public static BindingName Empty => default;
    public bool IsEmpty => _text is { } text ? text.Length == 0 : _bytes.IsEmpty;
    public bool IsIdentifier => _text is { } text && VariableNames.IsValidVariableName(text.AsSpan());
    public int ByteLength => _text is { } text ? Encoding.UTF8.GetByteCount(text) : _bytes.Bytes.Length;

    /// <summary>Cached text, absent for non-UTF-8 names and the default empty spelling.</summary>
    public string? CachedText => _text;

    /// <summary>Whether ToValue needs to allocate byte storage.</summary>
    public bool NeedsValueEncoding => _text != null && _bytes.IsEmpty;

    /// <summary>Retains a byte spelling and normalizes valid text for equality with parsed identifiers.</summary>
    public static BindingName FromValue(ShellValue value) => value.TryGetUtf8Text(out var text)
        ? new BindingName(text, value) : new BindingName(value);

    public static implicit operator BindingName(string text) => new(text, ShellValue.Empty);

    /// <summary>Gets the exact spelling for diagnostics, inspection, or environment transport.</summary>
    public ShellValue ToValue() => _bytes.IsEmpty && _text is { } text ? ShellValue.FromText(text) : _bytes;

    /// <summary>Decodes only names that can cross a text-only host boundary.</summary>
    public bool TryGetText(out string text)
    {
        text = _text ?? string.Empty;
        return _text != null || _bytes.IsEmpty;
    }

    public bool Equals(BindingName other) => IsEmpty ? other.IsEmpty
        : _text is { } text ? other._text != null && StringComparer.Ordinal.Equals(text, other._text)
        : other._text == null && _bytes.Equals(other._bytes);
    public override bool Equals(object? obj) => obj is BindingName other && Equals(other);
    public override int GetHashCode() => IsEmpty ? 0
        : _text is { } text ? StringComparer.Ordinal.GetHashCode(text) : _bytes.GetHashCode();
    public static bool operator ==(BindingName left, BindingName right) => left.Equals(right);
    public static bool operator !=(BindingName left, BindingName right) => !left.Equals(right);
}
