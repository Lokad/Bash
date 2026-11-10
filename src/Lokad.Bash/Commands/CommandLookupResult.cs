namespace Lokad.Bash.Commands;

/// <summary>A resolved command, established unavailability, or a failure determining availability.</summary>
public abstract record CommandLookupResult
{
    private CommandLookupResult()
    {
    }

    /// <summary> A command resolved by the host to a runnable name or path. </summary>
    /// <param name="ResolvedName">The name or path that identifies the resolved command.</param>
    public sealed record Found(string ResolvedName) : CommandLookupResult;

    /// <summary>No available command was found, including a path with unsupported script content.</summary>
    public sealed record Missing : CommandLookupResult;

    /// <summary>Lookup failed; the diagnostic has no command or operand prefix.</summary>
    public sealed record Failed(OperationError Error) : CommandLookupResult;
}
