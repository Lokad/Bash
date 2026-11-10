using System;
using Xunit;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class ByteReadResultTests
{
    [Fact]
    public void DefaultValueIsNormalEndOfFile()
    {
        var result = default(ByteReadResult);

        Assert.Equal(ByteReadResult.EndOfFile, result);
        Assert.Equal(0, result.BytesRead);
        Assert.True(result.ReachedEoF);
        Assert.False(result.IsError);
    }

    [Fact]
    public void SuccessRejectsEmptyReads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ByteReadResult.Success(0, reachedEoF: false));
    }

    [Fact]
    public void FailureRejectsNegativeByteCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ByteReadResult.Failure(-1, error: new OperationError("read error\n"), reachedEoF: false));
    }

    [Fact]
    public void FailurePreservesBytesAndError()
    {
        var result = ByteReadResult.Failure(3, error: new OperationError("read error\n"), reachedEoF: false);

        Assert.Equal(3, result.BytesRead);
        Assert.Equal(new OperationError("read error\n"), result.Error);
        Assert.False(result.ReachedEoF);
        Assert.True(result.IsError);
    }
}
