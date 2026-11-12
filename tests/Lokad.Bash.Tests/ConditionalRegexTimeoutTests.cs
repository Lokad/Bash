using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class ConditionalRegexTimeoutTests
{
    [Fact]
    public async Task BacktrackingRefusalReturnsConditionalFailure()
    {
        var input = new string('a', 1000) + "!" + new string('a', 1000);
        const string pattern = "^[a-z]*[a-y]*[a-x]*[a-w]*[a-v]*$";
        await using var host = new TestHost();
        Assert.Equal(0, (await host.RunAsync("[[ a =~ " + pattern + " ]]", CancellationToken.None)).ExitCode);
        var result = await Task.Run(() => host.RunAsync("[[ \"" + input + "\" =~ " + pattern + " ]]", CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, result.ExitCode);
    }
}
