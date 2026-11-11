using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using Lokad.Bash.Commands;
using Lokad.Bash.Evaluation;
using Lokad.Bash.Formatting;
using Lokad.Bash.Syntax;
using Lokad.Bash.Files;

namespace Lokad.Bash.Tests.EndToEnd;

// A deliberately small application host for language regressions. Utility behavior
// belongs to the embedding application's integration tests.
public static class EndToEndHarness
{
    public sealed record ExecutionResult(int ExitCode, string StdOut, string StdErr, TestHost Host);

    public static TestHost ConfigureHost(Action<TestHost> configure)
    {
        var host = new TestHost();
        configure(host);
        return host;
    }

    public static TestHost WithMemoryBudget(TestHost host, Memory.IMemoryBudget budget)
    {
        host.MemoryBudget = budget;
        return host;
    }

    public static TestHost BuildTree(IEnumerable<(string Path, string Content)> files) => BuildTree([], files);

    public static TestHost BuildTree(IEnumerable<(string Path, DateTimeOffset ModifiedAt)> directories,
        IEnumerable<(string Path, string Content, DateTimeOffset ModifiedAt)> files)
    {
        var host = new TestHost();
        foreach (var (path, modifiedAt) in directories) host.AddDirectory(path, modifiedAt);
        foreach (var (path, content, modifiedAt) in files) host.AddFile(path, content, modifiedAt);
        return host;
    }

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
        return await RunParsedAsync(Parser.Parse(script), host, EvaluationOptions.Default, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public static Task<ExecutionResult> RunScriptAsync(string script, TestHost host, ReadOnlyMemory<byte> standardInput, TimeSpan timeout)
    {
        host.SetStandardInputBytes(standardInput.ToArray());
        return RunParsedAsync(Parser.Parse(script), host, EvaluationOptions.Default, timeout);
    }

    public static Task<ExecutionResult> RunScriptAsync(string script, TestHost host, EvaluationOptions options) =>
        RunParsedAsync(Parser.Parse(script), host, options, TimeSpan.FromSeconds(10));

    public static Task<ExecutionResult> RunSourceAsync(ReadOnlyMemory<byte> source, TestHost host, TimeSpan timeout) =>
        RunParsedAsync(Parser.Parse(source), host, EvaluationOptions.Default, timeout);

    private static async Task<ExecutionResult> RunParsedAsync(ParsedScript parsed, TestHost host, EvaluationOptions options, TimeSpan timeout)
    {
        host.ResetOutput();
        var evaluation = new global::Lokad.Bash.Evaluation.Evaluation(parsed, options: options);
        using var cancellation = new CancellationTokenSource(timeout);
        var result = await new Engine().RunAsync(evaluation, host, cancellation.Token).ConfigureAwait(false);
        return new(result.ExitCode, host.GetOutput(FileDescriptor.StdOut), host.GetOutput(FileDescriptor.StdErr), host);
    }

    public static async Task<bool> FileExistsAsync(TestHost host, string path) =>
        await host.GetPathKindAsync(new FilePathLookup(new ShellPath(path), FileLinkFollowing.Follow), CancellationToken.None).ConfigureAwait(false) == FilePathKind.File;

    public static Task<string> ReadFileAsync(TestHost host, string path) => Task.FromResult(host.ReadFile(path));

    public static async Task<bool> DirectoryExistsAsync(TestHost host, string path) =>
        await host.GetPathKindAsync(new FilePathLookup(new ShellPath(path), FileLinkFollowing.Follow), CancellationToken.None).ConfigureAwait(false) == FilePathKind.Directory;
}

public sealed class TestHost : MockFileSystem
{
    private readonly ConcurrentDictionary<FileDescriptor, Endpoint> _pipes = new();
    private int _nextPipe = 1_000_000;
    public Capture StandardOutput => new(GetOutputBytes(FileDescriptor.StdOut));
    public Capture StandardError => new(GetOutputBytes(FileDescriptor.StdErr));
    public readonly record struct Capture(ReadOnlyMemory<byte> Content)
    {
        public string TextContent => Encoding.UTF8.GetString(Content.Span);
    }

    public Task<RunResult> RunAsync(string script, CancellationToken cancel) =>
        BashScript.RunAsync(this, script, EvaluationOptions.Default, cancel);

    public override async ValueTask DisposeAsync()
    {
        foreach (var descriptor in _pipes.Keys)
            await CloseDescriptorAsync(descriptor).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override Task<IReadOnlyList<CommandLookupResult>> ResolveCommandsAsync(
        IReadOnlyList<string> names, ShellPath directory, IReadOnlyList<EnvironmentVariable> environment, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<CommandLookupResult>>(names.Select(name =>
            name is "cat" or "sh" or "bash" or "echo" or "printf" or "fixture-bindings" ? new CommandLookupResult.Found("/usr/bin/" + name)
                : (CommandLookupResult)new CommandLookupResult.Missing()).ToArray());

    public override async Task<CommandExecutionResult> RunCommandAsync(
        CommandInvocation command, CommandLaunchFailureReporting reporting, CancellationToken cancel)
    {
        try
        {
            // Fixed byte-preserving projection for declaration assertions.
            if (command.CommandName == "fixture-bindings")
            {
                using var collected = new MemoryStream();
                while (true)
                {
                    using var read = await ReadAsync(command.StdIn, DescriptorReadRequest.UpTo(64 * 1024), cancel).ConfigureAwait(false);
                    if (read.Content.IsEmpty) break;
                    var bytes = read.Content.ToArray();
                    read.Consume(bytes.Length);
                    collected.Write(bytes);
                }
                var content = collected.ToArray();
                var matched = false;
                for (var start = 0; start < content.Length;)
                {
                    var end = Array.IndexOf(content, (byte)'\n', start);
                    if (end < 0) end = content.Length;
                    ReadOnlyMemory<byte> line = content.AsMemory(start, end - start);
                    var name = line;
                    if (line.Span.StartsWith("declare "u8))
                    {
                        var separator = line.Span[8..].IndexOf((byte)' ');
                        name = separator < 0 ? ReadOnlyMemory<byte>.Empty : line[(9 + separator)..];
                    }
                    if (name.Span.StartsWith("probe_"u8))
                    {
                        await WriteAsync(command.StdOut, line, cancel).ConfigureAwait(false);
                        await WriteAsync(command.StdOut, "\n"u8.ToArray(), cancel).ConfigureAwait(false);
                        matched = true;
                    }
                    start = end + 1;
                }
                return new CommandExecutionResult.Completed(matched ? 0 : 1);
            }
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
