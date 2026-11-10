using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class ConditionalRegexTimeoutTests
{
    [Fact]
    public async Task BacktrackingRefusalReturnsConditionalFailure()
    {
        var input = new string('a', 1000) + "!" + new string('a', 1000);
        const string pattern = "^[a-z]*[a-y]*[a-x]*[a-w]*[a-v]*$";
        Assert.False(ConditionalEvaluation.Parse("a =~ " + pattern).HasParseError);
        var result = await Task.Run(() => ConditionalEvaluation.Parse("\"" + input + "\" =~ " + pattern))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.HasParseError);
    }
}
