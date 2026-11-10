namespace Lokad.Bash;

/// <summary>Whether a copy or rename may replace an occupied destination.</summary>
public enum FileOverwriteMode
{
    ReplaceExisting,
    RequireNewDestination
}
