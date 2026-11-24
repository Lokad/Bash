using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class MockFileSystemDirectoryTests
{
    [Fact]
    public async Task ConcurrentWritableOpensPublishEveryCreatedFile()
    {
        await using var host = new MockFileSystem();
        host.AddDirectory("/fixture");
        const int workers = 8;
        const int filesPerWorker = 32;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creating = Task.WhenAll(Enumerable.Range(0, workers).Select(CreateFilesAsync));
        start.SetResult();
        await creating.WaitAsync(TimeSpan.FromSeconds(10));

        var entries = host.CaptureEntries();
        Assert.Equal(1 + workers * filesPerWorker, entries.Count);
        Assert.Equal((FilePathKind.Directory, string.Empty), entries["fixture"]);
        for (var worker = 0; worker < workers; worker++)
            for (var index = 0; index < filesPerWorker; index++)
                Assert.Equal((FilePathKind.File, Convert.ToHexString([(byte)worker, (byte)index])),
                    entries[$"fixture/{worker}-{index}"]);
        Assert.Equal(0, host.OpenFileCount);

        async Task CreateFilesAsync(int worker)
        {
            await start.Task.ConfigureAwait(false);
            for (var index = 0; index < filesPerWorker; index++)
            {
                var opened = await OpenFileAsync(host, $"/fixture/{worker}-{index}", FileCreationMode.CreateNew);
                var descriptor = Assert.IsType<FileDescriptor>(opened.FileDescriptor);
                try
                {
                    byte[] content = [(byte)worker, (byte)index];
                    Assert.True((await host.WriteAsync(descriptor, content, CancellationToken.None)).CanAcceptMore);
                }
                finally
                {
                    Assert.Null(await host.CloseDescriptorAsync(descriptor));
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentCreateNewClaimsOneName()
    {
        await using var host = new MockFileSystem();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => OpenOnceAsync()));
        start.SetResult();
        var results = await opening.WaitAsync(TimeSpan.FromSeconds(10));

        var winner = Assert.Single(results, result => result.FileDescriptor.HasValue);
        var descriptor = Assert.IsType<FileDescriptor>(winner.FileDescriptor);
        Assert.All(results.Where(result => !result.FileDescriptor.HasValue),
            result => Assert.Equal(OperationErrorKind.AlreadyExists, result.Error?.Kind));
        Assert.Equal(1, host.OpenFileCount);
        Assert.Null(await host.CloseDescriptorAsync(descriptor));
        Assert.Equal(0, host.OpenFileCount);
        Assert.Equal((FilePathKind.File, string.Empty), Assert.Single(host.CaptureEntries()).Value);

        async Task<FileOpenResult> OpenOnceAsync()
        {
            await start.Task.ConfigureAwait(false);
            return await OpenFileAsync(host, "/claimed", FileCreationMode.CreateNew);
        }
    }

    [Fact]
    public async Task ListingRetainsItsEntriesAndSnapshotsWhileAnotherOpenCompletes()
    {
        await using var host = new MockFileSystem();
        host.AddFile("/fixture/b", "old");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AfterListDirectory = _ =>
        {
            entered.SetResult();
            return release.Task;
        };

        var directory = new FilePathLookup(new ShellPath("/fixture"), FileLinkFollowing.Follow);
        var pending = ListAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(async () =>
            {
                var opened = await OpenFileAsync(host, "/fixture/a", FileCreationMode.CreateNew);
                Assert.Null(await host.CloseDescriptorAsync(Assert.IsType<FileDescriptor>(opened.FileDescriptor)));
                host.AddFile("/fixture/b", "new");
            }).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var before = Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var entry = Assert.Single(before.Entries);
        Assert.Equal("b", entry.Name);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<FileMetadata?>>(before.Metadata));
        Assert.NotNull(entry.Snapshot);
        var oldFile = await host.OpenReadAsync(entry.Snapshot, FileReadOpenMode.Immediate, CancellationToken.None);
        var reader = Assert.IsType<FileDescriptor>(oldFile.FileDescriptor);
        using (var read = await host.ReadAsync(reader, DescriptorReadRequest.UpTo(3), CancellationToken.None))
            Assert.Equal("old"u8.ToArray(), read.Content.ToArray());
        Assert.Null(await host.CloseDescriptorAsync(reader));

        host.AfterListDirectory = null;
        var after = Assert.Single(await ListAsync());
        Assert.Equal(["a", "b"], after.Entries.Select(item => item.Name));
        Assert.Equal("new", host.ReadFile("/fixture/b"));
        Assert.Equal(0, host.OpenFileCount);

        Task<IReadOnlyList<DirectoryListingResult>> ListAsync() => host.ListDirectoriesAsync(
            [directory], DirectoryMetadataRequest.Available, DirectorySnapshotRequest.CaptureAvailable,
            DirectoryAccessPattern.SingleDirectory, CancellationToken.None);
    }

    private static Task<FileOpenResult> OpenFileAsync(MockFileSystem host, string path, FileCreationMode mode) =>
        host.OpenWritableFileAsync(new FilePathLookup(new ShellPath(path), FileLinkFollowing.Follow),
            WritableFileOpenMode.WriteTruncate, mode, FileWriteRetention.AllowInMemory, CancellationToken.None);
}
