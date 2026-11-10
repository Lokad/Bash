namespace Lokad.Bash.Tests;

internal static class TestMemoryBudget
{
    // General fixtures leave ample room; exhaustion tests choose their own allowance.
    public const long DefaultBytes = 1024L * 1024 * 1024;
    public const long EvaluationBytes = 128L * 1024 * 1024;
    public const long FunctionFormattingBytes = 64L * 1024 * 1024;
    public const long TextToolBytes = 256L * 1024 * 1024;
}
