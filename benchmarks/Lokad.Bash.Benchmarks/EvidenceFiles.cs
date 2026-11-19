using System.Text.Json;

namespace Lokad.Bash.Benchmarks;

internal static class EvidenceFiles
{
    public static void CreateDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(fullPath) ?? throw new IOException("An evidence directory needs a parent.");
        Directory.CreateDirectory(parent);
        // CreateDirectory accepts existing directories. Reserve this name while
        // checking and creating it so concurrent producers cannot share a packet.
        using var claim = new FileStream(fullPath + ".creating", FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        if (Path.Exists(fullPath))
            throw new IOException("Evidence directory already exists: " + fullPath);
        Directory.CreateDirectory(fullPath);
    }

    public static FileStream Create(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

    public static void WriteBytes(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = Create(path);
        stream.Write(bytes);
    }

    public static void WriteJson<T>(string path, T value)
    {
        using var stream = Create(path);
        WriteJson(stream, value);
    }

    public static void WriteJson<T>(Stream stream, T value) => JsonSerializer.Serialize(stream, value, WorkloadCatalog.Json);
}
