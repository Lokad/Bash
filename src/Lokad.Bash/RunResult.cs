namespace Lokad.Bash;

/// <summary>Result of a shell script execution.</summary>
public readonly record struct RunResult(int ExitCode, ShellPath CurrentDirectory);
