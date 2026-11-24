using Lokad.Bash.Files;
using Lokad.Bash.Tests.EndToEnd;

namespace Lokad.Bash.Tests;

public sealed class MockFileSystemWriteTests
{
    [Fact]
    public async Task MockCatResolvesOperandsAgainstTheInvocationDirectory()
    {
        await using var host = new TestHost();
        host.AddFile("/dir/input", "payload");
        var result = await host.RunAsync("cd /dir; cat input", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("payload", host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData("printf seed >file; printf plus >>file; cat file", "seedplus")]
    [InlineData("exec 3>>file; printf a >&3; cat file; printf b >&3; exec 3>&-; cat file", "aab")]
    [InlineData("printf seed >file; exec 3>>file; exec 4>>file; printf a >&3; printf b >&4; exec 3>&-; printf c >&4; exec 4>&-; cat file", "seedabc")]
    public async Task AppendHandlesShareCurrentContentAndPublishBeforeClose(string script, string output)
    {
        await using var host = new TestHost();
        var result = await host.RunAsync(script, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, host.StandardOutput.TextContent);
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task ReadWriteHandlesShareOneCursorForReadsAndOverwrites()
    {
        await using var host = new TestHost();
        host.AddFile("/file", "a\nbcd");
        var result = await host.RunAsync("exec 3<>file; IFS= read -r line <&3; printf XY >&3; IFS= read -r rest <&3; printf '%s|%s|' \"$line\" \"$rest\"; exec 3>&-; cat file", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a|d|a\nXYd", host.StandardOutput.TextContent);
        Assert.Equal("a\nXYd", host.ReadFile("/file"));
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task TruncationKeepsExistingHandlePositionsAndClosingAnIdleHandleDoesNotOverwriteData()
    {
        await using var host = new TestHost();
        var result = await host.RunAsync("exec 3>file; printf ab >&3; exec 4>file; printf x >&3; exec 4>&-; exec 3>&-; cat file", CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new byte[] { 0, 0, (byte)'x' }, host.StandardOutput.Content.ToArray());
        Assert.Empty(host.StandardError.Content.ToArray());
        Assert.Equal(0, host.OpenFileCount);
    }

    [Fact]
    public async Task CapturedVersionsRemainImmutableWhileWritesAreVisibleToNewSnapshots()
    {
        await using var host = new MockFileSystem();
        host.AddFile("/file", "seed");
        var path = new FilePathLookup(new ShellPath("/file"), FileLinkFollowing.Follow);
        var initial = Assert.Single(await host.ResolvePathsAsync([path], FilePathInfoRequest.KindOnly, FileSnapshotRequest.Capture, CancellationToken.None)).File;
        Assert.NotNull(initial);
        var snapshot = initial.Snapshot;
        Assert.NotNull(snapshot);
        var opened = await host.OpenWritableFileAsync(path, WritableFileOpenMode.WriteAppend,
            FileCreationMode.OpenExisting, FileWriteRetention.AllowInMemory, CancellationToken.None);
        var writer = Assert.IsType<FileDescriptor>(opened.FileDescriptor);
        Assert.True((await host.WriteAsync(writer, "plus"u8.ToArray(), CancellationToken.None)).CanAcceptMore);
        Assert.Equal("seedplus", host.ReadFile("/file"));

        var oldRead = await host.OpenReadAsync(snapshot, FileReadOpenMode.Immediate, CancellationToken.None);
        var reader = Assert.IsType<FileDescriptor>(oldRead.FileDescriptor);
        using (var read = await host.ReadAsync(reader, DescriptorReadRequest.UpTo(1024), CancellationToken.None))
        {
            Assert.Equal("seed"u8.ToArray(), read.Content.ToArray());
            read.Consume(read.Content.Length);
        }
        await host.CloseDescriptorAsync(reader);
        await host.CloseDescriptorAsync(writer);
        Assert.Equal("seedplus", host.ReadFile("/file"));
        Assert.Equal(0, host.OpenFileCount);
    }

    [Theory]
    [InlineData(WritableFileOpenMode.WriteTruncate)]
    [InlineData(WritableFileOpenMode.WriteAppend)]
    [InlineData(WritableFileOpenMode.ReadWrite)]
    public async Task AllWritableModesRespectCreationAndDirectoryFailures(WritableFileOpenMode mode)
    {
        await using var host = new MockFileSystem();
        host.AddFile("/file", "seed");
        host.AddDirectory("/dir");
        var missing = await Open("/missing", FileCreationMode.OpenExisting);
        var existing = await Open("/file", FileCreationMode.CreateNew);
        var directory = await Open("/dir", FileCreationMode.OpenOrCreate);
        Assert.Equal(OperationErrorKind.NotFound, missing.Error?.Kind);
        Assert.Equal(OperationErrorKind.AlreadyExists, existing.Error?.Kind);
        Assert.Equal(OperationErrorKind.IsDirectory, directory.Error?.Kind);
        Assert.Equal("seed", host.ReadFile("/file"));
        Assert.Equal(0, host.OpenFileCount);

        Task<FileOpenResult> Open(string name, FileCreationMode creation) => host.OpenWritableFileAsync(
            new FilePathLookup(new ShellPath(name), FileLinkFollowing.Follow), mode, creation,
            FileWriteRetention.AllowInMemory, CancellationToken.None);
    }

    [Fact]
    public async Task IndependentAppendHandlesDoNotLoseWritesToTheSameFile()
    {
        await using var host = new MockFileSystem();
        var path = new FilePathLookup(new ShellPath("/file"), FileLinkFollowing.Follow);
        var first = await host.OpenWritableFileAsync(path, WritableFileOpenMode.WriteAppend,
            FileCreationMode.OpenOrCreate, FileWriteRetention.AllowInMemory, CancellationToken.None);
        var second = await host.OpenWritableFileAsync(path, WritableFileOpenMode.WriteAppend,
            FileCreationMode.OpenExisting, FileWriteRetention.AllowInMemory, CancellationToken.None);
        var firstHandle = Assert.IsType<FileDescriptor>(first.FileDescriptor);
        var secondHandle = Assert.IsType<FileDescriptor>(second.FileDescriptor);
        await Task.WhenAll(Task.Run(() => WriteManyAsync(firstHandle, (byte)'a')),
            Task.Run(() => WriteManyAsync(secondHandle, (byte)'b'))).WaitAsync(TimeSpan.FromSeconds(10));
        await host.CloseDescriptorAsync(firstHandle);
        await host.CloseDescriptorAsync(secondHandle);
        var content = host.ReadFileBytes("/file");
        Assert.Equal(1000, content.Length);
        Assert.Equal(500, content.Count(value => value == (byte)'a'));
        Assert.Equal(500, content.Count(value => value == (byte)'b'));
        Assert.Equal(0, host.OpenFileCount);

        async Task WriteManyAsync(FileDescriptor descriptor, byte value)
        {
            byte[] bytes = [value];
            for (var index = 0; index < 500; index++)
                Assert.True((await host.WriteAsync(descriptor, bytes, CancellationToken.None)).CanAcceptMore);
        }
    }

    [Fact]
    public async Task ConcurrentOutputWritesPreserveEachDescriptorAndRecord()
    {
        await using var host = new MockFileSystem();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int writesPerProducer = 2_000;
        var writers = Task.WhenAll(
            WriteRecordsAsync(FileDescriptor.StdOut, (byte)'a'),
            WriteRecordsAsync(FileDescriptor.StdOut, (byte)'b'),
            WriteRecordsAsync(FileDescriptor.StdErr, (byte)'c'),
            WriteRecordsAsync(FileDescriptor.StdErr, (byte)'d'));
        start.SetResult();
        await writers.WaitAsync(TimeSpan.FromSeconds(10));

        AssertRecords(FileDescriptor.StdOut, (byte)'a', (byte)'b');
        AssertRecords(FileDescriptor.StdErr, (byte)'c', (byte)'d');

        async Task WriteRecordsAsync(FileDescriptor descriptor, byte producer)
        {
            await start.Task.ConfigureAwait(false);
            byte[] record = [producer, (byte)'\n'];
            for (var index = 0; index < writesPerProducer; index++)
                Assert.True((await host.WriteAsync(descriptor, record, CancellationToken.None)).CanAcceptMore);
        }

        void AssertRecords(FileDescriptor descriptor, byte first, byte second)
        {
            var output = host.GetOutputBytes(descriptor);
            Assert.Equal(4 * writesPerProducer, output.Length);
            var firstCount = 0;
            var secondCount = 0;
            for (var index = 0; index < output.Length; index += 2)
            {
                Assert.Equal((byte)'\n', output[index + 1]);
                if (output[index] == first) firstCount++;
                if (output[index] == second) secondCount++;
            }

            Assert.Equal(writesPerProducer, firstCount);
            Assert.Equal(writesPerProducer, secondCount);
        }
    }
}
