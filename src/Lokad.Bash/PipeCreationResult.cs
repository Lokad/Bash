using Lokad.Bash.Files;

namespace Lokad.Bash;

/// <summary>Both owned pipe endpoints or a failure without endpoints. The default is an unknown failure.</summary>
public readonly record struct PipeCreationResult
{
    private readonly OperationError? _error;

    private PipeCreationResult(PipeDescriptors? descriptors, OperationError? error)
    {
        Descriptors = descriptors;
        _error = error;
    }

    /// <summary>The acquired pair, or null when creation failed.</summary>
    public PipeDescriptors? Descriptors { get; }

    /// <summary>The failure diagnostic, or null exactly when both endpoints were acquired.</summary>
    public OperationError? Error => Descriptors.HasValue ? null : _error ?? new OperationError("Cannot create pipe\n");

    /// <summary>Transfers ownership of both endpoints to the caller.</summary>
    public static PipeCreationResult Created(PipeDescriptors descriptors) => new(descriptors, null);

    /// <summary>Reports a failure without transferring any endpoints.</summary>
    public static PipeCreationResult Failed(OperationError error) => new(null, error);
}
