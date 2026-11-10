namespace Lokad.Bash;

/// <summary>Controls when opening a file snapshot starts content retrieval.</summary>
public enum FileReadOpenMode
{
    /// <summary>Allows retrieval to wait until the first read when the host supports it.</summary>
    Deferred,

    /// <summary>Starts retrieval before the open completes, without requiring the entire file to be buffered.</summary>
    Immediate
}
