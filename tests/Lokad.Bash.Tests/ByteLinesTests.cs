using System.Text;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;

namespace Lokad.Bash.Tests;

public sealed class ByteLinesTests
{
    [Fact]
    public void FindCompleteLines_CollectsOffsets()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x\n", 300)) + "tail");

        const long MemoryBudgetBytes = 4096;
        var consumed = ByteLines.FindCompleteLines(content, 200, new MemoryGovernor(MemoryBudgetBytes), out var lineStartOffsets, out _);

        Assert.Equal(400, consumed);
        Assert.Equal(Enumerable.Range(1, 199).Select(static index => index * 2), lineStartOffsets);
    }

    [Theory]
    [InlineData((byte)'\n', 1, false, 0)]
    [InlineData((byte)'\n', 128, false, 0)]
    [InlineData((byte)'\n', 129, false, 0)]
    [InlineData((byte)0, 257, false, 0)]
    [InlineData((byte)'\n', 206, true, 0)]
    [InlineData((byte)0, 257, true, 0)]
    [InlineData((byte)0, 257, true, 17)]
    public void FindCompleteRecordsKeepsVariableWidthOffsetsAcrossBatches(byte delimiter, int limit, bool reachedEof, int examined)
    {
        var source = Enumerable.Repeat((byte)0xff, examined).ToList();
        var ends = new List<int>();
        for (var index = 0; index < 205; index++)
        {
            source.AddRange(Enumerable.Repeat((byte)0xff, index % 11));
            source.Add(delimiter);
            ends.Add(source.Count);
        }
        source.Add(13);
        if (reachedEof)
            ends.Add(source.Count);
        var count = Math.Min(limit, ends.Count);
        var expectedAdmission = count == 1 ? 0 : 32 + sizeof(int) * (count - 1);
        var governor = new MemoryGovernor(expectedAdmission);

        var consumed = ByteLines.FindCompleteRecords(source.ToArray(), delimiter, limit, examined, reachedEof, governor,
            out var starts, out var admission);

        Assert.Equal(ends[count - 1], consumed);
        Assert.Equal(ends.Take(count - 1), starts);
        Assert.Equal(expectedAdmission, governor.ReservedBytes);
        admission?.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }
}
