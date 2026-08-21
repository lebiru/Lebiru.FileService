using System.IO.Compression;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Tests.Services;

public sealed class VirtualDirectoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FelixDirectoryTests", Guid.NewGuid().ToString("N"));
    private readonly InMemoryDirectoryStore _directories = new();
    private readonly InMemoryFileStore _files = new();
    private readonly VirtualDirectoryService _service;

    public VirtualDirectoryServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "uploads"));
        _service = new VirtualDirectoryService(_directories, _files, new TestEnvironment(_root),
            NullLogger<VirtualDirectoryService>.Instance);
    }

    [Fact]
    public void CreatesRootAndNestedDirectories()
    {
        var root = _service.Create("alice", "Documents", null);
        var nested = _service.Create("alice", "Taxes", root.Id);

        Assert.Null(root.ParentDirectoryId);
        Assert.Equal(root.Id, nested.ParentDirectoryId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void RejectsInvalidDirectoryNames(string name) =>
        Assert.ThrowsAny<Exception>(() => _service.Create("alice", name, null));

    [Fact]
    public void CannotCreateUnderAnotherUsersDirectory()
    {
        var bob = _service.Create("bob", "Private", null);

        Assert.ThrowsAny<Exception>(() => _service.Create("alice", "Intrusion", bob.Id));
    }

    [Fact]
    public void RootAndNestedListingsReturnOnlyImmediateOwnedContents()
    {
        var documents = _service.Create("alice", "Documents", null);
        var taxes = _service.Create("alice", "Taxes", documents.Id);
        _service.Create("bob", "Hidden", null);
        var rootFile = AddFile("alice", "root.txt", null, "root");
        var nestedFile = AddFile("alice", "federal.txt", taxes.Id, "tax");
        AddFile("bob", "secret.txt", null, "secret");

        var root = _service.GetContents("alice", null);
        var documentsContents = _service.GetContents("alice", documents.Id);
        var taxesContents = _service.GetContents("alice", taxes.Id);

        Assert.Equal(rootFile.Id, Assert.Single(root.Files).Id);
        Assert.Equal(documents.Id, Assert.Single(root.Directories).Id);
        Assert.Empty(documentsContents.Files);
        Assert.Equal(taxes.Id, Assert.Single(documentsContents.Directories).Id);
        Assert.Equal(nestedFile.Id, Assert.Single(taxesContents.Files).Id);
        Assert.Equal(["Root", "Documents", "Taxes"], taxesContents.Breadcrumbs.Select(item => item.Name));
    }

    [Fact]
    public void CannotListOrReadAnotherUsersHierarchy()
    {
        var directory = _service.Create("bob", "Private", null);

        Assert.ThrowsAny<Exception>(() => _service.GetContents("alice", directory.Id));
    }

    [Fact]
    public void MovesOwnedFileBetweenRootAndOwnedDirectoriesWithoutChangingPhysicalPath()
    {
        var first = _service.Create("alice", "First", null);
        var second = _service.Create("alice", "Second", null);
        var file = AddFile("alice", "move.txt", null, "bytes");
        var metadata = _files.GetAll();
        metadata.Single().ViewCount = 7;
        metadata.Single().LastViewedAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        metadata.Single().DailyViewCounts["2026-08-21"] = 7;
        _files.Replace(metadata);
        var originalPath = file.FilePath;

        Assert.Equal(first.Id, _service.MoveFile("alice", file.Id, first.Id).DirectoryId);
        Assert.Equal(second.Id, _service.MoveFile("alice", file.Id, second.Id).DirectoryId);
        var movedToRoot = _service.MoveFile("alice", file.Id, null);

        Assert.Null(movedToRoot.DirectoryId);
        Assert.Equal(originalPath, movedToRoot.FilePath);
        Assert.True(File.Exists(originalPath));
        Assert.Equal(7, movedToRoot.ViewCount);
        Assert.Equal(7, movedToRoot.DailyViewCounts["2026-08-21"]);
    }

    [Fact]
    public void CannotMoveAnotherUsersFileOrUseAnotherUsersTarget()
    {
        var aliceFile = AddFile("alice", "alice.txt", null, "a");
        var bobFile = AddFile("bob", "bob.txt", null, "b");
        var bobDirectory = _service.Create("bob", "Private", null);

        Assert.ThrowsAny<Exception>(() => _service.MoveFile("alice", bobFile.Id, null));
        Assert.ThrowsAny<Exception>(() => _service.MoveFile("alice", aliceFile.Id, bobDirectory.Id));
    }

    [Fact]
    public void MovesDirectoriesWithoutRewritingDescendants()
    {
        var archive = _service.Create("alice", "Archive", null);
        var documents = _service.Create("alice", "Documents", null);
        var taxes = _service.Create("alice", "Taxes", documents.Id);

        _service.Update("alice", taxes.Id, null, archive.Id, parentSpecified: true);

        Assert.Equal(archive.Id, _service.GetContents("alice", archive.Id).Directories.Single().ParentDirectoryId);
        Assert.Equal(taxes.Id, _directories.GetAll().Single(item => item.Id == taxes.Id).Id);
        Assert.Null(_service.Update("alice", taxes.Id, null, null, parentSpecified: true).ParentDirectoryId);
    }

    [Fact]
    public void RejectsSelfAndDescendantCycles()
    {
        var parent = _service.Create("alice", "A", null);
        var child = _service.Create("alice", "B", parent.Id);

        Assert.ThrowsAny<Exception>(() => _service.Update("alice", parent.Id, null, parent.Id, true));
        Assert.ThrowsAny<Exception>(() => _service.Update("alice", parent.Id, null, child.Id, true));
    }

    [Fact]
    public void CannotRenameMoveOrDeleteAnotherUsersDirectory()
    {
        var bob = _service.Create("bob", "Private", null);
        var alice = _service.Create("alice", "Alice", null);

        Assert.ThrowsAny<Exception>(() => _service.Update("alice", bob.Id, "Renamed", null, false));
        Assert.ThrowsAny<Exception>(() => _service.Update("alice", alice.Id, null, bob.Id, true));
        Assert.ThrowsAny<Exception>(() => _service.Delete("alice", bob.Id));
    }

    [Fact]
    public void DeletesOnlyEmptyDirectories()
    {
        var empty = _service.Create("alice", "Empty", null);
        var nonEmpty = _service.Create("alice", "NonEmpty", null);
        _service.Create("alice", "Child", nonEmpty.Id);

        _service.Delete("alice", empty.Id);

        Assert.DoesNotContain(_directories.GetAll(), directory => directory.Id == empty.Id);
        Assert.ThrowsAny<Exception>(() => _service.Delete("alice", nonEmpty.Id));
    }

    [Fact]
    public async Task ArchivePreservesHierarchyContentsAndEmptyDirectoriesOnDisk()
    {
        var documents = _service.Create("alice", "Documents", null);
        var taxes = _service.Create("alice", "Taxes", documents.Id);
        _service.Create("alice", "Empty", documents.Id);
        AddFile("alice", "resume.txt", documents.Id, "resume-content");
        AddFile("alice", "federal.txt", taxes.Id, "tax-content");

        var artifact = await _service.CreateArchiveAsync("alice", documents.Id, CancellationToken.None);
        try
        {
            Assert.True(File.Exists(artifact.Path));
            Assert.Equal("Documents.zip", artifact.DownloadName);
            Assert.Equal(2, artifact.FileCount);
            using var archive = ZipFile.OpenRead(artifact.Path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "Documents/");
            Assert.Contains(archive.Entries, entry => entry.FullName == "Documents/Empty/");
            Assert.Equal("resume-content", ReadEntry(archive, "Documents/resume.txt"));
            Assert.Equal("tax-content", ReadEntry(archive, "Documents/Taxes/federal.txt"));
        }
        finally { File.Delete(artifact.Path); }
    }

    [Fact]
    public async Task ArchiveSanitizesTraversalAndExcludesOtherUsersFiles()
    {
        var directory = _service.Create("alice", "../../Documents", null);
        AddFile("alice", "safe.txt", directory.Id, "safe");
        AddFile("bob", "secret.txt", directory.Id, "secret");

        var artifact = await _service.CreateArchiveAsync("alice", directory.Id, CancellationToken.None);
        try
        {
            using var archive = ZipFile.OpenRead(artifact.Path);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("..", StringComparison.Ordinal));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith('/'));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("secret.txt", StringComparison.Ordinal));
            Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("safe.txt", StringComparison.Ordinal));
        }
        finally { File.Delete(artifact.Path); }
    }

    [Fact]
    public async Task CannotArchiveAnotherUsersDirectory()
    {
        var directory = _service.Create("bob", "Private", null);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _service.CreateArchiveAsync("alice", directory.Id, CancellationToken.None));
    }

    private StoredFileInfo AddFile(string owner, string name, Guid? directoryId, string content)
    {
        var path = Path.Combine(_root, "uploads", name);
        File.WriteAllText(path, content);
        var file = new StoredFileInfo
        {
            Id = Guid.NewGuid(), FileName = name, FilePath = path, Owner = owner,
            DirectoryId = directoryId, FileSize = content.Length, UploadTime = DateTime.UtcNow
        };
        var files = _files.GetAll();
        files.Add(file);
        _files.Replace(files);
        return file;
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class InMemoryDirectoryStore : IVirtualDirectoryMetadataStore
    {
        private List<VirtualDirectory> _items = [];
        public List<VirtualDirectory> GetAll() => _items.Select(Clone).ToList();
        public void Replace(IEnumerable<VirtualDirectory> directories) => _items = directories.Select(Clone).ToList();
        private static VirtualDirectory Clone(VirtualDirectory item) => new()
        {
            Id = item.Id, OwnerUserId = item.OwnerUserId, ParentDirectoryId = item.ParentDirectoryId,
            Name = item.Name, CreatedAt = item.CreatedAt, UpdatedAt = item.UpdatedAt
        };
    }

    private sealed class InMemoryFileStore : IFileMetadataStore
    {
        private List<StoredFileInfo> _items = [];
        public List<StoredFileInfo> GetAll() => _items.Select(Clone).ToList();
        public void Replace(IEnumerable<StoredFileInfo> files) => _items = files.Select(Clone).ToList();
        public long UsedSpace => _items.Sum(file => file.FileSize);
        public StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc) => throw new NotSupportedException();
        private static StoredFileInfo Clone(StoredFileInfo item) => new()
        {
            Id = item.Id, FileName = item.FileName, FilePath = item.FilePath, Owner = item.Owner,
            DirectoryId = item.DirectoryId, FileSize = item.FileSize, UploadTime = item.UploadTime,
            ExpiryTime = item.ExpiryTime, ViewCount = item.ViewCount, LastViewedAt = item.LastViewedAt,
            DailyViewCounts = new Dictionary<string, long>(item.DailyViewCounts ?? [])
        };
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Lebiru.FileService.Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
