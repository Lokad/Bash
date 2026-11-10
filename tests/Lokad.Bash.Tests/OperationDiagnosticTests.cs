namespace Lokad.Bash.Tests;

public sealed class OperationDiagnosticTests
{
    [Fact]
    public void DescriptorReadExceptionPreservesItsDiagnosticAndEstablishedCause()
    {
        var error = new OperationError("provider: read refused \r\n\n")
            { Kind = OperationErrorKind.PermissionDenied };

        Assert.Equal(error, OperationError.FromException(new ShellDescriptorReadException(error)));
    }

    [Theory]
    [InlineData("failure\n", "failure")]
    [InlineData("failure\r\n", "failure\r")]
    [InlineData("first\nsecond\n", "first\nsecond")]
    [InlineData("failure\n\n", "failure\n")]
    [InlineData("\n", "")]
    public void HostDiagnosticPreservesItsTerminatorAndData(string message, string text)
    {
        var error = new OperationError(message);

        Assert.Same(message, error.Message);
        Assert.Equal(OperationErrorKind.Unknown, error.Kind);
        Assert.Equal(text, error.MessageWithoutTerminator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("failure")]
    [InlineData("failure\r")]
    [InlineData("first\nsecond")]
    public void HostDiagnosticRejectsMissingLfAtConstruction(string message)
    {
        Assert.Throws<ArgumentException>(() => new OperationError(message));
    }
}
