using System;

namespace Lokad.Bash.Files;

/// <summary>Stored special mode bits, independent of the caller's effective access rights.</summary>
[Flags]
public enum FileSpecialModeBits
{
    None = 0,
    SetUserId = 1,
    SetGroupId = 2,
    Sticky = 4
}
