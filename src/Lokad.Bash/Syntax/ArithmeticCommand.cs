using System;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>Represents an arithmetic command of the form ((expression)).</summary>
public sealed record ArithmeticCommand : ICommand
{
    private ArithmeticSource _syntax;

    /// <summary>The unexpanded arithmetic source; replacements receive independent prepared syntax.</summary>
    public ShellSource Expression { get => _syntax.Source; init => _syntax = new ArithmeticSource(value); }
    internal ArithmeticSource Syntax => _syntax;

    /// <summary>Retains source and prepares immutable lexical structure without executing it.</summary>
    public ArithmeticCommand(ShellSource Expression) => _syntax = new ArithmeticSource(Expression);

    public void Deconstruct(out ShellSource Expression) => Expression = this.Expression;
    public bool Equals(ArithmeticCommand? other) => other != null && Expression.Bytes.Equals(other.Expression.Bytes);
    public override int GetHashCode() => HashCode.Combine(Expression.Bytes);
}
