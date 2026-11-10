using Lokad.Bash.Evaluation;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests;

public sealed class ArithmeticErrorTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n ")]
    [InlineData("0")]
    [InlineData("1-1")]
    public void TryEvaluate_ZeroIsSuccessful(string expression)
    {
        Assert.True(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), static _ => 0, static (_, _) => null,
            out var value, out var error));
        Assert.Equal(0, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("x++,", "invalid arithmetic expression", 11L)]
    [InlineData("x=30, 1/0, x=40", "division by 0", 30L)]
    [InlineData("x=30, 1%0, x=40", "division by 0", 30L)]
    [InlineData("x=30, 2**-1, x=40", "exponent less than 0", 30L)]
    [InlineData("x=30, x/=0, x=40", "division by 0", 30L)]
    public void TryEvaluate_FailureKeepsEarlierWrites(string expression, string expected, long assigned)
    {
        var x = 10L;

        Assert.False(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), _ => x, Assign, out var value, out var error));
        Assert.Equal(0, value);
        Assert.NotNull(error);
        Assert.Equal(expected, error.Message);
        Assert.Equal(assigned, x);

        ArithmeticError? Assign(string name, long written)
        {
            Assert.Equal("x", name);
            x = written;
            return null;
        }
    }

    [Theory]
    [InlineData("x=30, locked=40, x=50")]
    [InlineData("locked=(x=30)")]
    public void TryEvaluate_RejectedAssignmentStopsAfterRightHandEffects(string expression)
    {
        var x = 10L;
        var attempts = new List<string>();

        Assert.False(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), _ => x, Assign, out _, out var error));
        Assert.Equal(new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, ShellValue.FromText("locked")), error);
        Assert.Equal(30, x);
        Assert.Equal(["x", "locked"], attempts);

        ArithmeticError? Assign(string name, long written)
        {
            attempts.Add(name);
            if (name == "locked")
                return new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, ShellValue.FromText(name));
            x = written;
            return null;
        }
    }

    [Theory]
    [InlineData("x=30@", 10, 20)]
    [InlineData("x=30;", 10, 20)]
    [InlineData("x=30\r", 10, 20)]
    [InlineData("x=y=30@", 10, 20)]
    [InlineData("x=30, y=40@", 30, 20)]
    [InlineData("x=(y=30)@", 10, 30)]
    [InlineData("x=(y=30@)", 10, 20)]
    [InlineData("x=(y=30) += 4", 10, 30)]
    [InlineData("x=30 ? y=40 : y=50", 10, 40)]
    [InlineData("++x@", 10, 20)]
    [InlineData("x++@", 11, 20)]
    [InlineData("x=y++@", 10, 21)]
    [InlineData("x=30 40", 30, 20)]
    [InlineData("x=12foo", 10, 20)]
    [InlineData("x=12e3", 10, 20)]
    [InlineData("x=12_3", 10, 20)]
    [InlineData("x=y=12foo", 10, 20)]
    [InlineData("x=30, y=12foo", 30, 20)]
    [InlineData("x=(y=30)+12foo", 10, 30)]
    [InlineData("x=30**=2", 10, 20)]
    [InlineData("x=(y=30)**=2", 10, 30)]
    [InlineData("x=30, y=40**=2", 30, 20)]
    [InlineData("x=30 2#x", 10, 20)]
    public void TryEvaluate_InvalidRhsPreservesOnlyCompletedInnerWrites(string expression, long x, long y)
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal) { ["x"] = 10, ["y"] = 20 };

        Assert.False(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), name => variables.GetValueOrDefault(name),
            Assign, out _, out var error));
        Assert.Equal(ArithmeticErrorKind.InvalidExpression, error.Kind);
        Assert.Equal(x, variables["x"]);
        Assert.Equal(y, variables["y"]);

        ArithmeticError? Assign(string name, long value)
        {
            variables[name] = value;
            return null;
        }
    }

    [Theory]
    [InlineData("0 && 12foo")]
    [InlineData("1 || 12foo")]
    [InlineData("1 ? 7 : 12foo")]
    public void TryEvaluate_UnselectedNumbersStillRequireValidTokens(string expression)
    {
        Assert.False(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), static _ => 0, static (_, _) => null,
            out _, out var error));
        Assert.Equal(ArithmeticErrorKind.InvalidExpression, error.Kind);
    }

    [Theory]
    [InlineData("1 || (locked=(x=30))", 1L)]
    [InlineData("0 && (locked=(x=30))", 0L)]
    [InlineData("1 ? 7 : (locked=(x=30))", 7L)]
    public void TryEvaluate_UnselectedOperandsDoNotReadOrWrite(string expression, long expected)
    {
        Assert.True(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression),
            _ => throw new InvalidOperationException("Unselected read"),
            (_, _) => throw new InvalidOperationException("Unselected write"), out var value, out var error));
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Fact]
    public void TryEvaluate_RejectedWritePreservesTheEarlierAssignment()
    {
        var variables = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["first"] = 10,
            ["locked"] = 20
        };
        var attempts = new List<KeyValuePair<string, long>>();

        Assert.False(ArithmeticEvaluation.TryEvaluate(
            ShellValue.FromText("first=30, locked=40"),
            name => variables.GetValueOrDefault(name),
            Assign, out var value, out var error));

        Assert.Equal(0, value);
        Assert.Equal(new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, ShellValue.FromText("locked")), error);
        Assert.Collection(attempts,
            assignment => Assert.Equal(KeyValuePair.Create("first", 30L), assignment),
            assignment => Assert.Equal(KeyValuePair.Create("locked", 40L), assignment));
        Assert.Equal(30, variables["first"]);
        Assert.Equal(20, variables["locked"]);

        ArithmeticError? Assign(string name, long assigned)
        {
            attempts.Add(KeyValuePair.Create(name, assigned));
            if (name == "locked")
                return new ArithmeticError(ArithmeticErrorKind.ReadonlyVariable, ShellValue.FromText(name));
            variables[name] = assigned;
            return null;
        }
    }

    [Fact]
    public void TryEvaluate_ReportsTheDeclaredNestingLimit()
    {
        var expression = new string('+', ShellSourceCursor.MaximumNestedExpansionDepth + 1) + "1";

        Assert.False(ArithmeticEvaluation.TryEvaluate(ShellValue.FromText(expression), static _ => 0, static (_, _) => null,
            out _, out var error));
        Assert.NotNull(error);
        Assert.Equal(ArithmeticErrorKind.NestingLimit, error.Kind);
    }
}
