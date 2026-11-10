using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class CommandSubstitutionCaptureTests
{
    [Fact]
    public async Task FinalCaptureBytesNeedNoExtraReadAndKeepCommandStatus()
    {
        var state = new Eval(Parser.Parse("value=$(cmd)"));
        var host = new TestOperationVisitor([7]);
        host.EnqueuePipeDescriptors(new PipeDescriptors(10, 11));
        host.EnqueueCloseDescriptor();
        host.EnqueueCloseDescriptor();
        host.EnqueueReadResult(ReadResult.Success(new byte[] { 0xff, 13, 10 }, [], reachedEoF: true));

        await new Engine().RunAsync(state, host);

        Assert.Equal(7, state.LastExitCode);
        Assert.Equal(ShellValue.FromBytes(new byte[] { 0xff, 13 }), state.Variables["value"]);
        Assert.Equal([new FileDescriptor(11), new FileDescriptor(10)], host.ClosedDescriptors);
        Assert.Empty(state.GetDescriptorsForCleanup());
    }
}
