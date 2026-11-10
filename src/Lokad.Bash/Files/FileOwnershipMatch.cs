using System;

namespace Lokad.Bash.Files;

/// <summary>Matches between an entry's owner/group and the host's effective identities, independent of access rights.</summary>
[Flags]
public enum FileOwnershipMatch
{
    /// <summary>Both identities are known not to match.</summary>
    None = 0,
    /// <summary>The entry's owner matches the effective user.</summary>
    EffectiveUser = 1,
    /// <summary>The entry's group matches the effective group, regardless of supplementary group membership.</summary>
    EffectiveGroup = 2
}
