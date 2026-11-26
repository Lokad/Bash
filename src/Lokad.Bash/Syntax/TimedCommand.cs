namespace Lokad.Bash.Syntax;

/// <summary>Times a pipeline using Bash's reserved word, optionally with the portable output format.</summary>
public sealed record TimedCommand(ICommand Inner, bool Portable) : ICommand
{
    internal TimingSpelling Spelling { get; init; }
}

/// <summary>Preserves the authored prefix when timing flags are propagated into a subshell.</summary>
internal enum TimingSpelling { AsEvaluated, Omitted, Ordinary }
