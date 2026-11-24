using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lokad.Bash;

/// <summary>
/// An already-resolved, well-formed absolute path, intended to be used whenever
/// a path is expected by C# code.
/// </summary>
/// <remarks>
/// Construction guarantees the path is absolute and canonical: it starts with '/',
/// contains no double slashes, no trailing slash (root '/' excepted), no '.' or '..'
/// segments, and no control characters (U+0000–U+001F, U+007F). Arbitrary non-ASCII UTF-8 is allowed.
/// </remarks>
[JsonConverter(typeof(JsonConverter))]
public sealed class ShellPath
{
    public string Path { get; }

    /// <summary> Is this the root path? </summary>
    public bool IsRoot => Path == "/";

    /// <summary> The final segment of the path (the file or directory name). </summary>
    public string FileName => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary> The parent path, or null when this path is the root path. </summary>
    public ShellPath? Parent
    {
        get
        {
            if (IsRoot)
                return null;

            var slash = Path.LastIndexOf('/');
            // A prefix ending before a component separator is already canonical.
            return slash == 0 ? Root : new ShellPath(Path[..slash], validate: false);
        }
    }

    /// <summary> The root path '/' </summary>
    public static ShellPath Root = new("/", validate: false);

    /// <summary> The '/tmp' path </summary>
    public static readonly ShellPath Tmp = new("/tmp");

    /// <summary> True if the path ends with the given extension (e.g. ".nvn"). </summary>
    public bool HasExtension(string ext) => Path.EndsWith(ext, StringComparison.Ordinal);

    public ShellPath(string path) : this(path, validate: true) { }

    private ShellPath(string path, bool validate)
    {
        if (validate && !IsValid(path))
            throw new ArgumentException($"'{path}' is not a valid absolute shell path.", nameof(path));
        Path = path;
    }

    internal static ShellPath? TryCreate(string path) => IsValid(path) ? new ShellPath(path, validate: false) : null;

    /// <summary> Returns true if <paramref name="path"/> satisfies all ShellPath constraints. </summary>
    public static bool IsValid(string path)
    {
        if (path.Length == 0 || path[0] != '/') return false;
        return path.Length == 1 || HasValidComponents(path.AsSpan(1));
    }

    private static bool HasValidComponents(ReadOnlySpan<char> suffix)
    {
        if (suffix[^1] == '/') return false;
        foreach (var range in suffix.Split('/'))
        {
            var seg = suffix[range];
            if (seg.Length == 0) return false;   // double slash
            if (seg.SequenceEqual(".") || seg.SequenceEqual("..")) return false;
            foreach (var c in seg)
                if (IsForbiddenChar(c)) return false;
        }
        return true;
    }

    // Forbidden: control chars and DEL.
    internal static bool IsForbiddenChar(char c) =>
        c is <= '\x1f' or '\x7f';

    /// <summary> True if this path is a strict parent of <paramref name="other"/>. </summary>
    public bool IsParentOf(ShellPath other) =>
        Path.Length == 1
            ? other.Path.Length > 1  // "/" is parent of everything except itself
            : other.Path.Length > Path.Length
              && other.Path[Path.Length] == '/'
              && other.Path.StartsWith(Path, StringComparison.Ordinal);

    /// <summary> True if this path equals <paramref name="other"/> or is a strict parent of it. </summary>
    public bool IsEqualToOrParentOf(ShellPath other) =>
        Path == other.Path || IsParentOf(other);

    /// <summary>Appends a relative path suffix, such as <c>child/leaf</c>, to this path.</summary>
    /// <param name="name">A canonical relative path, or an empty string when this path is the root.</param>
    /// <returns>The resulting path, or <see langword="null"/> if the suffix is invalid.</returns>
    public ShellPath? TryChild(string name)
    {
        if (string.IsNullOrEmpty(name))
            return IsRoot ? new ShellPath("/", validate: false) : null;
        if (!HasValidComponents(name.AsSpan()))
            return null;

        var combined = IsRoot ? "/" + name : Path + "/" + name;
        return new ShellPath(combined, validate: false);
    }

    /// <summary>Appends a relative path suffix, such as <c>child/leaf</c>, to this path.</summary>
    /// <param name="name">A canonical relative path, or an empty string when this path is the root.</param>
    /// <returns>The resulting path.</returns>
    /// <exception cref="ArgumentException">The suffix is invalid.</exception>
    public ShellPath Child(string name) =>
        TryChild(name) ?? throw new ArgumentException($"'{name}' is not a valid child path segment.", nameof(name));

    public override string ToString() => Path;

    public override bool Equals(object? obj) => obj is ShellPath other && Path == other.Path;
    public override int GetHashCode() => Path.GetHashCode(StringComparison.Ordinal);

    public sealed class JsonConverter : JsonConverter<ShellPath>
    {
        public override ShellPath? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            return new ShellPath(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, ShellPath? value, JsonSerializerOptions options)
        {
            if (value is null) writer.WriteNullValue();
            else writer.WriteStringValue(value.Path);
        }
    }
}
