using Lokad.Bash.Evaluation;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class BashScriptTests
{
    [Fact]
    public async Task ReturnsStatusAndFinalDirectoryThroughThePublicEntryPoint()
    {
        var host = EndToEndHarness.BuildTree(["/work"], []);
        var result = await BashScript.RunAsync(host, "printf ready; cd /work", EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new ShellPath("/work"), result.CurrentDirectory);
        Assert.Equal("ready", host.GetOutput(FileDescriptor.StdOut));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReportsSyntaxFailureBeforeExecutingTheInvalidUnit()
    {
        var host = EndToEndHarness.BuildTree([]);
        var result = await BashScript.RunAsync(host, "printf unexpected; if", EvaluationOptions.Default, CancellationToken.None);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(ShellPath.Root, result.CurrentDirectory);
        Assert.Empty(host.GetOutput(FileDescriptor.StdOut));
        Assert.NotEmpty(host.GetOutput(FileDescriptor.StdErr));
    }

    [Fact]
    public void RuntimeReferencesOnlyFrameworkAssemblies()
    {
        Assert.All(typeof(BashScript).Assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name is "netstandard" or "mscorlib" || reference.Name?.StartsWith("System.", StringComparison.Ordinal) == true,
                $"Unexpected runtime dependency: {reference.Name}"));
    }
}
