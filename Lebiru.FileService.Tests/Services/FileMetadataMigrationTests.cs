using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lebiru.FileService.Tests.Services;

public sealed class FileMetadataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FelixMigrationTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingFileReceivesStableIdAndRemainsAtRoot()
    {
        var data = Path.Combine(_root, "app-data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "fileInfo.json"),
            """[{"FileName":"legacy.txt","FilePath":"legacy.txt","UploadTime":"2026-01-01T00:00:00Z","FileSize":5,"Owner":"alice"}]""");

        var store = new FileMetadataStore(new TestEnvironment(_root), NullLogger<FileMetadataStore>.Instance);
        var file = Assert.Single(store.GetAll());

        Assert.NotEqual(Guid.Empty, file.Id);
        Assert.Null(file.DirectoryId);
        Assert.Contains(file.Id.ToString(), File.ReadAllText(Path.Combine(data, "fileInfo.json")),
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
