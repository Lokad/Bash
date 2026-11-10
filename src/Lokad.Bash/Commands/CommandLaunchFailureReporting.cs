namespace Lokad.Bash.Commands;

/// <summary> Selects who emits launch-failure diagnostics; tool-emitted stderr is unaffected. </summary>
public enum CommandLaunchFailureReporting
{
    /// <summary> The host reports failures before closing command-owned descriptors. </summary>
    Host,

    /// <summary>
    /// The caller reports failures from the returned results using descriptors it retains.
    /// Command-owned descriptors are still closed before the host returns.
    /// </summary>
    Caller
}
