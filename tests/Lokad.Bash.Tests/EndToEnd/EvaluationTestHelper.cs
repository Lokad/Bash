using System.Text;
using Lokad.Bash.Evaluation;

namespace Lokad.Bash.Tests.EndToEnd;

internal static class EvaluationTestHelper
{
    public static async Task<FileDescriptor> ReadTerminalTestDescriptorAsync(global::Lokad.Bash.Evaluation.Evaluation evaluation)
    {
        var operation = Assert.IsType<TerminalStatusesOperation>(await evaluation.GetNextStep(CancellationToken.None));
        var descriptor = Assert.Single(operation.Descriptors);
        operation.SetResult([false]);
        Assert.Null(await evaluation.GetNextStep(CancellationToken.None));
        return descriptor;
    }

    public static Task<EndToEndHarness.ExecutionResult> RunAsync(string script) => RunAsync(script, "value\n", BuildDefaultTree());
    public static Task<EndToEndHarness.ExecutionResult> RunAsync(string script, string? standardInput) => RunAsync(script, standardInput, BuildDefaultTree());
    public static Task<EndToEndHarness.ExecutionResult> RunAsync(string script, TestHost tree) => RunAsync(script, "value\n", tree);
    public static Task<EndToEndHarness.ExecutionResult> RunAsync(string script, string? standardInput, TestHost tree) =>
        EndToEndHarness.RunScriptAsync(script, tree, standardInput is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(standardInput));

    public static TestHost BuildDefaultTree() => EndToEndHarness.BuildTree(
        ["/target", "/dir", "/src", "/dest", "/docs"],
        [("/file", "content\n"), ("/input", "input\n"), ("/old.txt", "old\n"), ("/source.txt", "source\n"), ("/dir/file.txt", "dir\n")]);
}
