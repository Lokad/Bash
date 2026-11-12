using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    [Fact]
    public async Task PipelineStatusRemainsAvailableUntilTheNextCommandCompletes()
    {
        var state = new Eval(Parser.Parse("false | true; printf '%s' \"${PIPESTATUS[*]}\""));
        var creation = Assert.IsType<CreatePipeOperation>(await NextStepAsync(state));
        creation.SetResult(PipeCreationResult.Created(new PipeDescriptors(10, 11)));
        var pipeline = Assert.IsType<RunPipelineEvaluations>(await NextStepAsync(state));
        Assert.False(state.Variables.ContainsKey("PIPESTATUS"));
        pipeline.SetResult([1, 0]);

        var write = await NextWriteAsync(state);
        Assert.Equal("1 0", Text(write.Content));
        Assert.Equal(new[] { ShellValue.FromText("1"), ShellValue.FromText("0") }, state.IndexedArrays["PIPESTATUS"]);
        Assert.False(state.Completion.IsCompleted);
        write.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(ShellValue.FromText("0"), Assert.Single(state.IndexedArrays["PIPESTATUS"]));
    }
}
