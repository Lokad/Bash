using Lokad.Bash.Files;

namespace Lokad.Bash.Tests;

public sealed class DirectoryEntryTests
{
    [Fact]
    public void ListingMetadataIsOptionalAndAlignedWithEntries()
    {
        DirectoryEntry[] entries = [new("first", FilePathKind.File), new("second", FilePathKind.SymbolicLink)];
        FileMetadata?[] metadata = [default(FileMetadata), null];
        var listing = DirectoryListingResult.Success(entries, metadata);

        Assert.Same(metadata, listing.Metadata);
        Assert.Null(DirectoryListingResult.Success(entries).Metadata);
        Assert.Null(default(DirectoryListingResult).Metadata);
        Assert.Throws<ArgumentException>(() => DirectoryListingResult.Success(entries, [default(FileMetadata)]));
        var failed = DirectoryListingResult.Failure(new OperationError("denied\n"));
        Assert.Throws<InvalidOperationException>(() => failed.Metadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("parent/child")]
    [InlineData("bad\0name")]
    public void DirectoryEntry_RejectsInvalidChildNames(string name)
    {
        Assert.Throws<ArgumentException>(() => new DirectoryEntry(name, FilePathKind.File));
    }

    [Theory]
    [InlineData("é=[x]", FilePathKind.File)]
    [InlineData("a\\b", FilePathKind.Directory)]
    [InlineData("a\nb", FilePathKind.File)]
    [InlineData("special", FilePathKind.Other)]
    [InlineData("block", FilePathKind.BlockDevice)]
    [InlineData("character", FilePathKind.CharacterDevice)]
    [InlineData("fifo", FilePathKind.Fifo)]
    [InlineData("socket", FilePathKind.Socket)]
    [InlineData("link", FilePathKind.SymbolicLink)]
    public void DirectoryEntryRetainsChildSpellingWithoutPathNormalization(string name, FilePathKind kind)
    {
        var entry = new DirectoryEntry(name, kind);

        Assert.Equal(name, entry.Name);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(kind == FilePathKind.Directory, entry.IsDirectory);
    }

    [Theory]
    [InlineData(FilePathKind.Missing)]
    [InlineData((FilePathKind)(-1))]
    public void DirectoryEntryRejectsKindsThatDoNotDescribeAnExistingEntry(FilePathKind kind) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectoryEntry("entry", kind));

    [Fact]
    public async Task OnlyRegularFileEntriesCanCarrySnapshots()
    {
        var files = new MockFileSystem();
        files.AddFile("/file", "content");
        var resolution = await files.ResolveFileAsync(
            new FilePathLookup(new ShellPath("/file"), FileLinkFollowing.Follow), CancellationToken.None);
        var snapshot = Assert.IsAssignableFrom<FileSnapshot>(resolution.File?.Snapshot);

        Assert.Same(snapshot, new DirectoryEntry("file", FilePathKind.File) { Snapshot = snapshot }.Snapshot);
        foreach (var kind in new[] { FilePathKind.SymbolicLink, FilePathKind.Directory, FilePathKind.Other })
        {
            var entry = new DirectoryEntry("entry", kind);
            Assert.Null(entry.Snapshot);
            Assert.Throws<ArgumentException>(() => entry with { Snapshot = snapshot });
        }
    }
}
