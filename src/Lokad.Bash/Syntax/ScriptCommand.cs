namespace Lokad.Bash.Syntax;

/// <summary>Interpreter input parsed into complete command units as execution reaches them.</summary>
internal sealed record ScriptCommand(ShellSource Source) : ICommand;
