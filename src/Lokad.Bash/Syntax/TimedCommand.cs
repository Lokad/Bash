namespace Lokad.Bash.Syntax;

/// <summary>Times a pipeline using Bash's reserved word, optionally with the portable output format.</summary>
public sealed record TimedCommand(ICommand Inner, bool Portable) : ICommand;
