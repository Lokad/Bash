using System.Text.Json;
using Lokad.Bash;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Files;
using Lokad.Bash.Helpers;
using Lokad.Bash.Memory;
using Lokad.Bash.Syntax;

await PackageConsumer.VerifyAsync();

internal static class PackageConsumer
{
    public static async Task VerifyAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var governor = new MemoryGovernor(2 * 1024 * 1024);
        using (var host = new ConsumerHost(governor))
        {
            var run = await BashScript.RunAsync(host,
                "sum=0; for ((i=0;i<4;i++)); do ((sum+=i)); done; printf '%s:' \"$sum\"; verify-bytes $'\\377'; printf '%s' \"${PIPESTATUS[0]}\"",
                EvaluationOptions.Default, deadline.Token);
            Check(run.ExitCode == 0, "Public invocation failed.");
            host.Expect("6:0"u8, []);

            var script = Parser.Parse("value=$((value+5)); printf '%s:' \"$((value+=2))\"; printf '%s' $'\\377\\r'");
            for (var repetition = 0; repetition < 2; repetition++)
            {
                host.Reset();
                using var memory = governor.CreateScope();
                var evaluation = new Evaluation(script);
                run = await new Engine().RunAsync(evaluation, host, memory, deadline.Token);
                Check(run.ExitCode == 0, "Prepared execution failed.");
                host.Expect(new byte[] { (byte)'7', (byte)':', 0xff, 0x0d }, []);
            }

            host.Reset();
            run = await BashScript.RunAsync(host, "printf before\nprintf 'unterminated",
                EvaluationOptions.Default, deadline.Token);
            Check(run.ExitCode == 2, "Later syntax failure lost its status.");
            Check(host.Output.AsSpan().SequenceEqual("before"u8) && host.Error.Length > 0,
                "Complete command units lost earlier effects or the syntax diagnostic.");

            host.Reset();
            run = await BashScript.RunAsync(host, "trap 'printf trap' EXIT; set -e; false; printf wrong",
                EvaluationOptions.Default, deadline.Token);
            Check(run.ExitCode == 1, "Errexit lost its status.");
            host.Expect("trap"u8, []);

            host.Reset();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await BashScript.RunAsync(host, "while :; do :; done", EvaluationOptions.Default, cancellation.Token);
                throw new InvalidOperationException("Cancellation did not propagate.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            host.Expect([], []);
            Check(governor.ReservedBytes == ConsumerHost.OutputEstimate, "Evaluation retained memory after cleanup.");
            VerifyPublicMatcher();
        }
        Check(governor.ReservedBytes == 0, "Host output ownership was not released.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed = true,
            runtimeMajor = Environment.Version.Major,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            assembly = typeof(BashScript).Assembly.GetName().Version?.ToString(),
            checks = new[] { "public-invocation", "host-byte-arguments", "prepared-reuse", "byte-output",
                "lazy-command-units", "errexit-exit-trap", "cancellation", "memory-refund", "public-utf8-matcher" },
            estimatedPeakBytes = governor.PeakBytes
        }));
    }

    private static void VerifyPublicMatcher()
    {
        var mask = default(GlobMaskSpan);
        Check(TextGlobMatcher.MatchUtf8("?"u8, in mask, "🙂"u8, true, CancellationToken.None, out var length)
            == GlobMatchResult.Match && length == 4, "Public UTF-8 matcher migration failed.");
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

/// <summary>Public-contract probe: only output and one synthetic external command are capabilities.</summary>
internal sealed class ConsumerHost : IBashHost, IDisposable
{
    public const long OutputEstimate = 64 * 1024;
    private const int OutputCapacity = 16 * 1024;
    private readonly IMemoryScope _outputOwner;
    private readonly MemoryStream _output;
    private readonly MemoryStream _error;
    public IMemoryBudget MemoryBudget { get; }
    public IShellTiming Timing { get; } = new ConsumerTiming();
    public ShellExecutionContext? Execution => null;
    public byte[] Output => _output.ToArray();
    public byte[] Error => _error.ToArray();

    public ConsumerHost(IMemoryBudget memory)
    {
        MemoryBudget = memory;
        _outputOwner = memory.CreateScope();
        if (!_outputOwner.TryReserve(OutputEstimate, out _))
        {
            _outputOwner.Dispose();
            throw new InvalidOperationException("Probe output admission failed.");
        }
        _output = new MemoryStream(OutputCapacity);
        _error = new MemoryStream(OutputCapacity);
    }

    public void Reset() { _output.SetLength(0); _error.SetLength(0); }
    public void Expect(ReadOnlySpan<byte> output, ReadOnlySpan<byte> error)
    {
        PackageConsumer.Check(_output.GetBuffer().AsSpan(0, checked((int)_output.Length)).SequenceEqual(output), "Unexpected stdout bytes.");
        PackageConsumer.Check(_error.GetBuffer().AsSpan(0, checked((int)_error.Length)).SequenceEqual(error), "Unexpected stderr bytes.");
    }

    public ValueTask<DescriptorWriteResult> WriteAsync(FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var output = descriptor.Id switch { 1 => _output, 2 => _error, _ => throw Unexpected() };
        PackageConsumer.Check(content.Length <= OutputCapacity - output.Length, "Probe output exceeded its admitted capacity.");
        output.Write(content.Span);
        return ValueTask.FromResult(DescriptorWriteResult.Open);
    }

    public Task<CommandExecutionResult> RunCommandAsync(CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PackageConsumer.Check(command.CommandName == "verify-bytes" && command.Arguments.Count == 1
            && command.Arguments[0].Bytes.Span.SequenceEqual(new byte[] { 0xff }), "Host command argument bytes changed.");
        return Task.FromResult<CommandExecutionResult>(new CommandExecutionResult.Completed(0));
    }

    public void Dispose() { _output.Dispose(); _error.Dispose(); _outputOwner.Dispose(); }
    private static InvalidOperationException Unexpected() => new("Unexpected host capability in package probe.");
    public Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(IReadOnlyList<string> names, ShellPath directory,
        IReadOnlyList<EnvironmentVariable> environment, CancellationToken token) => throw Unexpected();
    public Task<FileOpenResult> OpenWritableFileAsync(FilePathLookup path, WritableFileOpenMode mode,
        FileCreationMode creation, FileWriteRetention retention, CancellationToken token) => throw Unexpected();
    public Task<FileOpenResult> OpenReadAsync(FileSnapshot file, FileReadOpenMode mode, CancellationToken token) => throw Unexpected();
    public Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor) => throw Unexpected();
    public Task<IReadOnlyList<DirectoryListingResult>> ListDirectoriesAsync(IReadOnlyList<FilePathLookup> paths,
        DirectoryMetadataRequest metadata, DirectorySnapshotRequest snapshots, DirectoryAccessPattern access, CancellationToken token) => throw Unexpected();
    public ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken token) => throw Unexpected();
    public Task<IReadOnlyList<bool>> GetTerminalStatusesAsync(IReadOnlyList<FileDescriptor> descriptors, CancellationToken token) => throw Unexpected();
    public Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetime, CancellationToken token) => throw Unexpected();
    public Task<IReadOnlyList<FilePathResolutionResult>> ResolvePathsAsync(IReadOnlyList<FilePathLookup> paths,
        FilePathInfoRequest request, FileSnapshotRequest snapshots, CancellationToken token) => throw Unexpected();

    private sealed class ConsumerTiming : IShellTiming, IDisposable
    {
        public IDisposable BeginEvaluation() => this;
        public ShellTimingSnapshot Read() => throw Unexpected();
        public void Dispose() { }
    }
}
