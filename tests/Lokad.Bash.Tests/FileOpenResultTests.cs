namespace Lokad.Bash.Tests;

public sealed class FileOpenResultTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public void OpenedRetainsDescriptorAndOptionalSizeWithoutAnError(long? size)
    {
        var result = FileOpenResult.Opened(FileDescriptor.StdIn, size);

        Assert.Equal(FileOpenOutcome.Opened, result.Outcome);
        Assert.Equal(FileDescriptor.StdIn, result.FileDescriptor);
        Assert.Equal(size, result.ReadSize);
        Assert.Null(result.Error);
    }

    [Fact]
    public void DefaultIsAnUnknownFailureWithADiagnostic()
    {
        var result = default(FileOpenResult);

        Assert.Equal(FileOpenOutcome.Failed, result.Outcome);
        Assert.Null(result.FileDescriptor);
        Assert.Null(result.ReadSize);
        Assert.Equal(OperationErrorKind.Unknown, result.Error?.Kind);
        Assert.Equal("Cannot open file\n", result.Error?.Message);
    }

    [Fact]
    public void FailedRetainsTheDiagnosticWithoutADescriptorOrSize()
    {
        var error = new OperationError("provider refused\r\n") { Kind = OperationErrorKind.PermissionDenied };
        var result = FileOpenResult.Failed(error);

        Assert.Equal(FileOpenOutcome.Failed, result.Outcome);
        Assert.Equal(error, result.Error);
        Assert.Null(result.FileDescriptor);
        Assert.Null(result.ReadSize);
    }

    [Fact]
    public void OpenedRejectsNegativeSizes()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            FileOpenResult.Opened(FileDescriptor.StdIn, readSize: -1));

        Assert.Equal("readSize", error.ParamName);
    }
}
