using System;
using System.Threading;

namespace Lokad.Bash.Helpers;

/// <summary>
/// Combines lifetime and per-operation cancellation without allocating when either token is sufficient.
/// </summary>
/// <remarks>
/// The combined token reaches the underlying operation. Canceling only a waiter would leave descriptor I/O pending.
/// </remarks>
public readonly struct CancellationTokenScope : IDisposable
{
    private readonly CancellationTokenSource? _source;

    private CancellationTokenScope(CancellationToken token, CancellationTokenSource? source)
    {
        Token = token;
        _source = source;
    }

    /// <summary>Gets the token to pass to the underlying operation.</summary>
    public CancellationToken Token { get; }

    /// <summary>Combines two tokens while preserving the no-allocation single-token cases.</summary>
    public static CancellationTokenScope Combine(CancellationToken lifetime, CancellationToken operation)
    {
        if (!lifetime.CanBeCanceled || lifetime == operation)
            return new CancellationTokenScope(operation, null);
        if (!operation.CanBeCanceled)
            return new CancellationTokenScope(lifetime, null);

        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime, operation);
        return new CancellationTokenScope(source.Token, source);
    }

    /// <summary>Releases the linked source when combining the tokens required one.</summary>
    public void Dispose() => _source?.Dispose();
}
