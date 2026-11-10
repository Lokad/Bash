using System;

namespace Lokad.Bash.Files;

/// <summary>Effective access in the host's execution context, after resolving its permission policy.</summary>
[Flags]
public enum FileAccessRights
{
    None = 0,
    Read = 1,
    Write = 2,
    /// <summary>Execution for files; search/traversal for directories.</summary>
    Execute = 4
}
