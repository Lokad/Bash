using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Syntax;
using Lokad.Bash.Tests.EndToEnd;
using Eval = Lokad.Bash.Evaluation.Evaluation;

namespace Lokad.Bash.Tests;

public partial class EvaluationTests
{
    // Bash 5.2.21 -s; source-location prefixes may appear on either output channel.
    [Theory]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\ncat 2>/dev/null <<HPM_END\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\ncat <<HPM_END 2>/dev/null\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "after:1", "HPM_MISSING: stop\n", 0)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\n: 2>/dev/null <<HPM_END\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "", 1)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\n: <<HPM_END 2>/dev/null\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "HPM_MISSING: stop\n", 1)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\nhpm_fn 2>/dev/null <<HPM_END\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "", 1)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\nhpm_fn <<HPM_END 2>/dev/null\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "HPM_MISSING: stop\n", 1)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\n{ :; } 2>/dev/null <<HPM_END\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "", 1)]
    [InlineData("unset HPM_MISSING; hpm_fn(){ printf wrong; }\n{ :; } <<HPM_END 2>/dev/null\n${HPM_MISSING:?stop}\nHPM_END\nprintf 'after:%s' \"$?\"", "", "HPM_MISSING: stop\n", 1)]
    [InlineData("unset HPM_MISSING; cat 2>/dev/null <<< \"${HPM_MISSING:?stop}\"; printf 'after:%s' \"$?\" >&2", "", "after:1", 0)]
    [InlineData("unset HPM_MISSING; cat 2>&- <<< \"${HPM_MISSING:?stop}\"; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("unset HPM_MISSING; cat 2>&1 >/dev/null <<< \"${HPM_MISSING:?stop}\"; printf 'after:%s' \"$?\"", "HPM_MISSING: stop\nafter:1", "", 0)]
    [InlineData("unset HPM_MISSING; cat >/dev/null 2>&1 <<< \"${HPM_MISSING:?stop}\"; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("cat 2>/dev/null <<< \"$(printf seen >&2; printf payload)\"; printf after >&2", "payload\n", "after", 0)]
    [InlineData("cat <<< \"$(printf seen >&2; printf payload)\" 2>/dev/null; printf after >&2", "payload\n", "seenafter", 0)]
    [InlineData("cat 3<<<seed <<< \"$(cat <&3)\"; printf 'after:%s' \"$?\"", "seed\nafter:0", "", 0)]
    [InlineData("cat 2>/dev/null <<< \"$(cat <<< \"$(printf seen >&2; printf payload)\")\"; printf after >&2", "payload\n", "after", 0)]
    [InlineData("cat 2>/dev/null < /dev/null/HPM_missing; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("cat 2>/dev/null {HPM_FD}< /dev/null/HPM_missing; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("cat 2>&- < /dev/null/HPM_missing; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("hpm_fn(){ cat <<< \"$(printf seen >&2; printf payload)\"; }; hpm_fn 2>/dev/null; printf after >&2", "payload\n", "after", 0)]
    [InlineData("HPM_PREFIX=$(printf prefix >&2) cat 2>/dev/null <<< \"$(printf redir >&2; printf payload)\"; printf after >&2", "payload\n", "prefixafter", 0)]
    [InlineData("cat \"$(printf arg >&2; printf /dev/null)\" 2>/dev/null <<< \"$(printf redir >&2)\"; printf after >&2", "", "argafter", 0)]
    [InlineData("cat 2>/dev/null 3>&- 1>&3; printf 'after:%s' \"$?\"", "after:1", "", 0)]
    [InlineData("cat <<< \"$(printf first >&2; printf one)\" 2>/dev/null <<< \"$(printf second >&2; printf two)\"; printf after >&2", "two\n", "firstafter", 0)]
    [InlineData("exec 2>/dev/null; cat <<< \"$(printf hidden >&2; printf payload)\"; printf after", "payload\nafter", "", 0)]
    public async Task Evaluation_Redirection_PreparationUsesEarlierMappings(
        string script, string output, string error, int exitCode)
    {
        var result = await EndToEndHarness.RunScriptAsync(script, EndToEndHarness.BuildTree([]));

        Assert.Equal(output, result.StdOut);
        Assert.Equal(error, result.StdErr);
        Assert.Equal(exitCode, result.ExitCode);
    }

    // The host supplies the error payload; this checks routing and scope restoration, not filesystem policy.
    [Theory]
    [InlineData("cat 2>&1 >/dev/null < missing; printf after >&2", 1)]
    [InlineData("cat < missing 2>&1; printf after >&2", 2)]
    [InlineData("cat 2>&1 >/dev/null {fd}< missing; printf after >&2", 1)]
    [InlineData("cat {fd}< missing 2>&1; printf after >&2", 2)]
    public async Task Evaluation_Redirection_RoutesReportedOpenFailureBeforeLaterMappings(string script, int errorDescriptor)
    {
        var state = new Eval(Parser.Parse(Utf8(script)));
        var open = await NextOpenFileAsync(state);
        Assert.Equal("/missing", open.Path.Path);
        open.SetResult(FileOpenResult.Failed(new OperationError("reported failure\n")));

        var error = await NextWriteAsync(state);
        Assert.Equal(errorDescriptor, error.FileDescriptor.Id);
        Assert.Equal("reported failure\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        var continuation = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, continuation.FileDescriptor);
        Assert.Equal("after", Text(continuation.Content));
        continuation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Theory]
    [InlineData("cat 2>&- < missing; printf after >&2")]
    [InlineData("cat 2>&- {fd}< missing; printf after >&2")]
    public async Task Evaluation_Redirection_ClosedErrorStreamSuppressesReportedOpenFailure(string script)
    {
        var state = new Eval(Parser.Parse(Utf8(script)));
        var open = await NextOpenFileAsync(state);
        open.SetResult(FileOpenResult.Failed(new OperationError("reported failure\n")));

        var continuation = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, continuation.FileDescriptor);
        Assert.Equal("after", Text(continuation.Content));
        continuation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }

    [Fact]
    public async Task Evaluation_Redirection_ClosesOpenedDescriptorAfterExpansionFailure()
    {
        var state = new Eval(Parser.Parse(Utf8("cat 2>/error <<< \"${missing:?stop}\"; printf after >&2")));
        var open = await NextOpenFileAsync(state);
        Assert.Equal("/error", open.Path.Path);
        open.SetResult(FileOpenResult.Opened(10, readSize: null));

        var error = await NextWriteAsync(state);
        Assert.Equal(10, error.FileDescriptor.Id);
        Assert.Equal("missing: stop\n", Text(error.Content));
        error.SetResult(DescriptorWriteResult.Open);

        var close = await NextCloseDescriptorAsync(state);
        Assert.Equal(10, close.Descriptor.Id);
        close.SetResult(null);

        var continuation = await NextWriteAsync(state);
        Assert.Equal(FileDescriptor.StdErr, continuation.FileDescriptor);
        Assert.Equal("after", Text(continuation.Content));
        continuation.SetResult(DescriptorWriteResult.Open);

        Assert.Null(await NextStepAsync(state));
        Assert.Equal(0, state.LastExitCode);
    }
}
