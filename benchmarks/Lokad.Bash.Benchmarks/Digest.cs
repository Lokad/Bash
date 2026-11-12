using System.Security.Cryptography;

namespace Lokad.Bash.Benchmarks;

internal sealed record ContentDigest(long Bytes, string Sha256)
{
    public static ContentDigest Of(Blob blob) => new(blob.Length, blob.Sha256);
}

/// <summary>Consumes output without retaining a growing transcript.</summary>
internal sealed class Digest : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public long Bytes { get; private set; }
    public void Append(ReadOnlySpan<byte> bytes) { _hash.AppendData(bytes); Bytes = checked(Bytes + bytes.Length); }
    public ContentDigest Snapshot() => new(Bytes, Convert.ToHexStringLower(_hash.GetCurrentHash()));
    public void Dispose() => _hash.Dispose();
}
