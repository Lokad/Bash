namespace Lokad.Bash;

/// <summary>Controls whether completed output may remain in host file buffers.</summary>
public enum FileWriteRetention
{
    /// <summary>The host may retain small completed outputs in memory.</summary>
    AllowInMemory,

    /// <summary>Publish new output to backing storage and release its file buffers on close.</summary>
    /// <remarks>
    /// Buffering while writing is allowed. Existing input content and storage-owned memory are
    /// outside this policy; access, creation and concurrent-write semantics remain unchanged.
    /// </remarks>
    ReleaseOnClose
}
