using System.Text;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public sealed class OperationDiagnosticTests
{
    [Theory]
    [InlineData("read value", "read", "line 1: read: read error: 0: ", false)]
    [InlineData("read value", "read", "read: read error: 0: ", true)]
    [InlineData("read -d : value", "read", "line 1: read: read error: 0: ", false)]
    [InlineData("read -d : value", "read", "read: read error: 0: ", true)]
    [InlineData("echo ok", "echo", "line 1: echo: write error: ", false)]
    [InlineData("echo ok", "echo", "echo: write error: ", true)]
    [InlineData("cd /unavailable", "cd", "line 1: cd: /unavailable: ", false)]
    [InlineData("cd /unavailable", "cd", "cd: /unavailable: ", true)]
    public async Task BuiltinHostErrorsPreserveFramingAndRefusal(string script, string name, string prefix, bool exhaustBudget)
    {
        var state = new Eval(Parser.Parse(script + " 2>errors"));
        var governor = new MemoryGovernor(128 * 1024);
        using var owner = governor.CreateScope();
        state.Memory.Budget = owner;
        var descriptor = new FileDescriptor(17);
        var detail = new string('é', 2048) + "\r\n\n";
        var error = new OperationError(detail);
        var open = Assert.IsType<OpenFileOperation>(await state.GetNextStep());
        open.SetResult(FileOpenResult.Opened(descriptor, readSize: null));
        var failed = await state.GetNextStep();
        using var other = exhaustBudget ? governor.Reserve(governor.LimitBytes - governor.ReservedBytes) : null;
        var before = governor.ReservedBytes;
        switch (failed)
        {
            case ReadLineOperation line:
                line.SetResult(ReadResult.Failure(error));
                break;
            case ReadInputOperation input:
                input.SetResult(new ReadInputResult(false, ReadBackslashMode.Preserve, error));
                break;
            case WriteOperation output:
                output.SetResult(DescriptorWriteResult.Failure(error));
                break;
            case PathInfosOperation lookup:
                lookup.SetResult([FilePathResolutionResult.Failure(error)]);
                break;
            default:
                throw new InvalidOperationException("Expected a builtin host operation.");
        }

        var write = Assert.IsType<WriteOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, write.FileDescriptor);
        var expected = Encoding.UTF8.GetBytes(exhaustBudget
            ? name + ": shared memory budget exhausted\n" : prefix + detail);
        Assert.Equal(expected, write.Content.ToArray());
        var pending = governor.ReservedBytes;
        if (!exhaustBudget)
            Assert.Equal(expected.Length, pending - before);
        write.SetResult(DescriptorWriteResult.Open);
        var close = Assert.IsType<CloseDescriptorOperation>(await state.GetNextStep());
        Assert.Equal(descriptor, close.Descriptor);
        if (!exhaustBudget)
            Assert.True(governor.ReservedBytes < pending);
        close.SetResult(null);

        Assert.Null(await state.GetNextStep());
        Assert.Equal(1, state.LastExitCode);
        Assert.Equal("/", state.CurrentDirectory.Path);
        Assert.False(state.Variables.ContainsKey("value"));
        other?.Dispose();
        owner.Dispose();
        Assert.Equal(0, governor.ReservedBytes);
    }

    [Fact]
    public void DescriptorReadExceptionPreservesItsDiagnosticAndEstablishedCause()
    {
        var error = new OperationError("provider: read refused \r\n\n")
            { Kind = OperationErrorKind.PermissionDenied };

        Assert.Equal(error, OperationError.FromException(new ShellDescriptorReadException(error)));
    }

    [Theory]
    [InlineData("failure\n", "failure")]
    [InlineData("failure\r\n", "failure\r")]
    [InlineData("first\nsecond\n", "first\nsecond")]
    [InlineData("failure\n\n", "failure\n")]
    [InlineData("\n", "")]
    public void HostDiagnosticPreservesItsTerminatorAndData(string message, string text)
    {
        var error = new OperationError(message);

        Assert.Same(message, error.Message);
        Assert.Equal(OperationErrorKind.Unknown, error.Kind);
        Assert.Equal(text, error.MessageWithoutTerminator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("failure")]
    [InlineData("failure\r")]
    [InlineData("first\nsecond")]
    public void HostDiagnosticRejectsMissingLfAtConstruction(string message)
    {
        Assert.Throws<ArgumentException>(() => new OperationError(message));
    }
}
