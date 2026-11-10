using System;

namespace Lokad.Bash.Syntax;

/// <summary>
/// Represents a shell function definition.
/// </summary>
/// <param name="Name">The unexpanded name token; invalid names are rejected when the definition executes.</param>
/// <param name="Body">The function body command.</param>
public sealed record FunctionDefinition(FunctionName Name, ICommand Body) : ICommand;

/// <summary>A name token's literal spelling and function-definition grammar, before any evaluation.</summary>
public sealed record FunctionName(ShellValue Value, FunctionNameSyntax Syntax)
{
    /// <summary>Export names must be valid definitions without environment/path separators.</summary>
    public bool CanExport => !Syntax.HasFlag(FunctionNameSyntax.Invalid)
        && Value.Bytes.Span.IndexOfAny((byte)'/', (byte)'=') < 0;
}

/// <summary>Separates accepted name forms from names that fail only when their definition executes.</summary>
[Flags]
public enum FunctionNameSyntax
{
    /// <summary>No quoting, expansion or keyword-only grammar restriction.</summary>
    Plain = 0,
    /// <summary>A keyword or assignment word requires the function keyword.</summary>
    KeywordOnly = 1,
    /// <summary>Quoting or dollar syntax prevents registration; the name is never expanded.</summary>
    Invalid = 2
}
