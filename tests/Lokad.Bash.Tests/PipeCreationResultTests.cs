namespace Lokad.Bash.Tests;

public sealed class PipeCreationResultTests
{
    [Fact]
    public void DefaultIsFailureWithoutEndpoints()
    {
        var result = default(PipeCreationResult);

        Assert.Null(result.Descriptors);
        Assert.Equal(OperationErrorKind.Unknown, result.Error?.Kind);
        Assert.Equal("Cannot create pipe\n", result.Error?.Message);
    }
}
