using System.Text;
using Lokad.Bash.Helpers;

namespace Lokad.Bash.Tests;

public sealed class ByteLinesTests
{
    [Fact]
    public void FindCompleteLines_CollectsOffsets()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("x\n", 300)) + "tail");

        const long MemoryBudgetBytes = 4096;
        var consumed = ByteLines.FindCompleteLines(content, 200, new Lokad.Bash.Memory.MemoryGovernor(MemoryBudgetBytes), out var lineStartOffsets, out _);

        Assert.Equal(400, consumed);
        Assert.Equal(Enumerable.Range(1, 199).Select(static index => index * 2), lineStartOffsets);
    }
}
