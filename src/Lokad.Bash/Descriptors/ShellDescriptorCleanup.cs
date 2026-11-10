using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;

namespace Lokad.Bash;

/// <summary>Closes owned descriptors without allowing cleanup failures to replace earlier failures.</summary>
/// <remarks>
/// Calls the host without cancellation. Callers retain ownership tracking and must not close borrowed handles.
/// </remarks>
public static class ShellDescriptorCleanup
{
    /// <summary>Completes an owned handle's cleanup, capturing its failure without retrying.</summary>
    public static ValueTask<DescriptorCloseResult> TryCloseOwnedAsync(
        IBashHost host,
        FileDescriptor descriptor) =>
        CloseAsync(host, descriptor, earlierException: null);

    /// <summary>Attempts one uncancelled close, preserving a supplied earlier exception even when closure succeeds.</summary>
    /// <returns>Whether cleanup succeeded and the first exception, if any. The handle is retired even on failure.</returns>
    public static async ValueTask<DescriptorCloseResult> CloseAsync(
        IBashHost host,
        FileDescriptor descriptor,
        ExceptionDispatchInfo? earlierException)
    {
        try
        {
            var error = await host.CloseDescriptorAsync(descriptor)
                .ConfigureAwait(false);
            return new DescriptorCloseResult(error == null, error, earlierException);
        }
        catch (Exception exception)
        {
            return new DescriptorCloseResult(
                Closed: false,
                Error: null,
                earlierException ?? ExceptionDispatchInfo.Capture(exception));
        }
    }
}

/// <summary>The outcome of closing an owned shell descriptor during cleanup.</summary>
/// <param name="Closed">Whether cleanup succeeded, independently of the preserved exception; failure still retires the handle.</param>
/// <param name="Error">An expected cleanup failure, independent of an earlier exception.</param>
/// <param name="Exception">The supplied earlier exception, otherwise a cleanup exception, or null when neither exists.</param>
public readonly record struct DescriptorCloseResult(
    bool Closed,
    OperationError? Error,
    ExceptionDispatchInfo? Exception)
{
    /// <summary>Keeps the first failure across expected errors and exceptions while cleanup continues.</summary>
    public static DescriptorCloseResult FirstFailure(DescriptorCloseResult earlier, DescriptorCloseResult later) =>
        earlier.Error != null || earlier.Exception != null ? earlier : later;
}
