using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Formatting;
using Lokad.Bash.Syntax;

namespace Lokad.Bash.Tests.EndToEnd;

// A deliberately small application host for language regressions. Utility behavior
// belongs to the embedding application's integration tests.
public static class EndToEndHarness
{
    public sealed record ExecutionResult(int ExitCode, string StdOut, string StdErr, TestHost Host);

    public static TestHost BuildTree(IEnumerable<(string Path, string Content)> files) => BuildTree([], files);

    public static TestHost BuildTree(IEnumerable<string> directories, IEnumerable<(string Path, string Content)> files)
    {
        var host = new TestHost();
        foreach (var directory in directories) host.AddDirectory(directory);
        foreach (var (path, content) in files) host.AddFile(path, content);
        return host;
    }

    public static Task<ExecutionResult> RunScriptAsync(string script, TestHost host) =>
        RunScriptAsync(script, host, ReadOnlyMemory<byte>.Empty);

    public static async Task<ExecutionResult> RunScriptAsync(string script, TestHost host, ReadOnlyMemory<byte> standardInput)
    {
        host.SetStandardInputBytes(standardInput.ToArray());
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(Parser.Parse(script));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new Engine().RunAsync(evaluation, host, cancellation.Token).ConfigureAwait(false);
        return new(result.ExitCode, host.GetOutput(FileDescriptor.StdOut), host.GetOutput(FileDescriptor.StdErr), host);
    }

    public static async Task<bool> FileExistsAsync(TestHost host, string path) =>
        await host.GetPathKindAsync(new FilePathLookup(new ShellPath(path), FileLinkFollowing.Follow), CancellationToken.None).ConfigureAwait(false) == FilePathKind.File;

    public static Task<string> ReadFileAsync(TestHost host, string path) => Task.FromResult(host.ReadFile(path));
}

public sealed class TestHost : MockFileSystem
{
    private readonly ConcurrentDictionary<FileDescriptor, Endpoint> _pipes = new();
    private int _nextPipe = 1_000_000;
    public Capture StandardOutput => new(GetOutputBytes(FileDescriptor.StdOut));
    public Capture StandardError => new(GetOutputBytes(FileDescriptor.StdErr));
    public readonly record struct Capture(ReadOnlyMemory<byte> Content);

    public override Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> names, ShellPath directory, IReadOnlyList<EnvironmentVariable> environment, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<CommandLookupResult>>(names.Select(name =>
            name is "cat" or "sh" or "bash" or "echo" or "printf" ? new CommandLookupResult.Found("/usr/bin/" + name)
                : (CommandLookupResult)new CommandLookupResult.Missing()).ToArray());

    public override async Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken cancel)
    {
        try
        {
            if (command.CommandName is "sh" or "bash")
            {
                var source = command.Arguments[0].ToUtf8String() == "-c"
                    ? command.Arguments[1].ToUtf8String() : ReadFile(command.Arguments[0].ToUtf8String());
                var arguments = command.Arguments.Skip(2).ToArray();
                var evaluation = ShellEvaluationStartup.CreateInherited(command, Parser.Parse(source),
                    arguments.Length > 0 ? arguments[0] : command.ArgumentZeroOverride ?? ShellValue.FromText(command.CommandName), arguments.Skip(1).ToArray());
                foreach (var exported in command.Functions)
                    if (Parser.Parse(exported.Source).Commands is [FunctionDefinition definition]) evaluation.SetExportedFunction(definition);
                return new CommandExecutionResult.Completed((await new Engine().RunAsync(evaluation, this, cancel).ConfigureAwait(false)).ExitCode);
            }
            if (command.CommandName == "cat")
            {
                if (command.Arguments.Count == 0)
                    await CopyAsync(command.StdIn).ConfigureAwait(false);
                foreach (var argument in command.Arguments)
                {
                    var opened = await this.OpenFileAsync(new FilePathLookup(new ShellPath(argument.ToUtf8String()), FileLinkFollowing.Follow), cancel).ConfigureAwait(false);
                    if (opened.FileDescriptor is not { } descriptor) return new CommandExecutionResult.Completed(1);
                    try { await CopyAsync(descriptor).ConfigureAwait(false); }
                    finally { await CloseDescriptorAsync(descriptor).ConfigureAwait(false); }
                }
                return new CommandExecutionResult.Completed(0);
            }
            if (command.CommandName is "echo" or "printf")
            {
                using var memory = MemoryBudget.CreateScope();
                var bytes = command.CommandName == "echo"
                    ? EchoFormatting.Format(command.Arguments, EchoFormatting.GetOutputFormat(command.Arguments), memory)
                    : PrintfFormatting.EvaluateCommand(command.Arguments[0], command.Arguments.Skip(1).ToArray(), memory).Output;
                await WriteAsync(command.StdOut, bytes, cancel).ConfigureAwait(false);
                return new CommandExecutionResult.Completed(0);
            }
            await this.WriteLineAsync(command.StdErr, $"{command.CommandName}: command not found", cancel).ConfigureAwait(false);
            return new CommandExecutionResult.Completed(127);
        }
        finally
        {
            foreach (var descriptor in command.DescriptorsToClose)
                await CloseDescriptorAsync(descriptor).ConfigureAwait(false);
        }

        async Task CopyAsync(FileDescriptor input)
        {
            while (true)
            {
                using var read = await ReadAsync(input, DescriptorReadRequest.UpTo(64 * 1024), cancel).ConfigureAwait(false);
                if (read.Content.IsEmpty) return;
                var content = read.Content.ToArray();
                read.Consume(content.Length);
                if (!(await WriteAsync(command.StdOut, content, cancel).ConfigureAwait(false)).CanAcceptMore) return;
            }
        }
    }

    public override Task<PipeCreationResult> CreatePipeAsync(CancellationToken lifetime, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var pipe = new Pipe(new PipeOptions(useSynchronizationContext: false));
        var read = new FileDescriptor(Interlocked.Increment(ref _nextPipe));
        var write = new FileDescriptor(Interlocked.Increment(ref _nextPipe));
        _pipes[read] = new Endpoint(pipe, false, lifetime, MemoryBudget);
        _pipes[write] = new Endpoint(pipe, true, lifetime, MemoryBudget);
        return Task.FromResult(PipeCreationResult.Created(new PipeDescriptors(read, write)));
    }

    public override async ValueTask<DescriptorWriteResult> WriteAsync(FileDescriptor descriptor, ReadOnlyMemory<byte> content, CancellationToken cancel)
    {
        if (descriptor.IsClosed) return DescriptorWriteResult.WriteFailure;
        if (!_pipes.TryGetValue(descriptor, out var endpoint))
            return await base.WriteAsync(descriptor, content, cancel).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, endpoint.Lifetime);
        var result = await endpoint.Pipe.Writer.WriteAsync(content, linked.Token).ConfigureAwait(false);
        return result.IsCompleted ? DescriptorWriteResult.Closed : DescriptorWriteResult.Open;
    }

    public override async ValueTask<DescriptorReadBuffer> ReadAsync(FileDescriptor descriptor, DescriptorReadRequest request, CancellationToken cancel)
    {
        if (!_pipes.TryGetValue(descriptor, out var endpoint))
            return await base.ReadAsync(descriptor, request, cancel).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, endpoint.Lifetime);
        var read = await endpoint.Pipe.Reader.ReadAtLeastAsync(request.MinimumBytes, linked.Token).ConfigureAwait(false);
        var buffer = read.Buffer;
        if (buffer.IsEmpty)
        {
            endpoint.Pipe.Reader.AdvanceTo(buffer.End);
            return DescriptorReadBuffer.EndOfFile;
        }
        var content = buffer.Slice(0, Math.Min(buffer.Length, request.MaximumBytes)).ToArray();
        endpoint.Pending = buffer;
        return endpoint.Owner.Borrow(content, ByteReadResult.Success(content.Length, read.IsCompleted && content.Length == buffer.Length), canRetain: true);
    }

    public override async Task<OperationError?> CloseDescriptorAsync(FileDescriptor descriptor)
    {
        if (!_pipes.TryRemove(descriptor, out var endpoint)) return await base.CloseDescriptorAsync(descriptor).ConfigureAwait(false);
        if (endpoint.IsWriter) await endpoint.Pipe.Writer.CompleteAsync().ConfigureAwait(false);
        else await endpoint.Pipe.Reader.CompleteAsync().ConfigureAwait(false);
        return null;
    }

    private sealed class Endpoint
    {
        public Pipe Pipe { get; }
        public bool IsWriter { get; }
        public CancellationToken Lifetime { get; }
        public DescriptorReadBufferOwner Owner { get; }
        public ReadOnlySequence<byte> Pending { get; set; }
        public Endpoint(Pipe pipe, bool isWriter, CancellationToken lifetime, Memory.IMemoryBudget memory)
        {
            Pipe = pipe;
            IsWriter = isWriter;
            Lifetime = lifetime;
            Owner = new(count => pipe.Reader.AdvanceTo(Pending.GetPosition(count)), memory);
        }
    }
}
