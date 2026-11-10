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

    private ShellPath()
    {
        Path = "/";
    }

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
            return slash == 0 ? Root : new ShellPath(Path[..slash]);
        }
    }

    /// <summary> The root path '/' </summary>
    public static ShellPath Root = new();

    /// <summary> The '/tmp' path </summary>
    public static readonly ShellPath Tmp = new("/tmp");

    /// <summary> True if the path ends with the given extension (e.g. ".nvn"). </summary>
    public bool HasExtension(string ext) => Path.EndsWith(ext, StringComparison.Ordinal);

    public ShellPath(string path)
    {
        if (!IsValid(path))
            throw new ArgumentException($"'{path}' is not a valid absolute shell path.", nameof(path));
        Path = path;
    }

    /// <summary> Returns true if <paramref name="path"/> satisfies all ShellPath constraints. </summary>
    public static bool IsValid(string path)
    {
        if (path.Length == 0 || path[0] != '/') return false;
        if (path == "/") return true;
        if (path[^1] == '/') return false;

        var segments = path.AsSpan(1);
        foreach (var range in segments.Split('/'))
        {
            var seg = segments[range];
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

    /// <summary>
    /// Returns the child path formed by appending <paramref name="name"/> to this path,
    /// or null if <paramref name="name"/> contains characters forbidden by <see cref="ShellPath"/>.
    /// </summary>
    public ShellPath? TryChild(string name)
    {
        var combined = IsRoot ? "/" + name : Path + "/" + name;
        return IsValid(combined) ? new ShellPath(combined) : null;
    }

    /// <summary>
    /// Returns the child path formed by appending <paramref name="name"/> to this path.
    /// Throws <see cref="ArgumentException"/> if <paramref name="name"/> is not a valid path segment.
    /// </summary>
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
