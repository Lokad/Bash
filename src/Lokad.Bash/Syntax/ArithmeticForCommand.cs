using System;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Syntax;

/// <summary>Represents an arithmetic for loop of the form for ((expression)); do ...; done.</summary>
public sealed record ArithmeticForCommand : ICommand
{
    private ArithmeticForSyntax _syntax;
    public ShellSource Expression { get => _syntax.Source; init => _syntax = new ArithmeticForSyntax(value); }
    public ICommand Body { get; init; }
    internal ArithmeticSource Initialization => _syntax.Initialization;
    internal ArithmeticSource Condition => _syntax.Condition;
    internal ArithmeticSource Update => _syntax.Update;

    /// <summary>Retains loop syntax and splits immutable clauses without executing their expansions.</summary>
    public ArithmeticForCommand(ShellSource Expression, ICommand Body)
    {
        _syntax = new ArithmeticForSyntax(Expression);
        this.Body = Body;
    }

    public void Deconstruct(out ShellSource Expression, out ICommand Body) => (Expression, Body) = (this.Expression, this.Body);
    public bool Equals(ArithmeticForCommand? other) => other != null && Expression.Bytes.Equals(other.Expression.Bytes)
        && Equals(Body, other.Body);
    public override int GetHashCode() => HashCode.Combine(Expression.Bytes, Body);

    internal void Split(out ShellSource init, out ShellSource condition, out ShellSource update) =>
        (init, condition, update) = (Initialization.Source, Condition.Source, Update.Source);
}
