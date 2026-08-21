using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Services;

/// <summary>Provides cached, synchronized access to stored file metadata.</summary>
public interface IFileMetadataStore
{
    /// <summary>Returns an isolated snapshot of all metadata.</summary>
    List<StoredFileInfo> GetAll();
    /// <summary>Atomically replaces the metadata snapshot.</summary>
    void Replace(IEnumerable<StoredFileInfo> files);
    /// <summary>Gets the total number of bytes represented by the current snapshot.</summary>
    long UsedSpace { get; }
    /// <summary>Atomically increments a file's view summary and daily UTC rollup.</summary>
    StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc);
}

/// <inheritdoc />
public sealed class FileMetadataStore : IFileMetadataStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private List<StoredFileInfo> _files;

    /// <summary>Creates the metadata store.</summary>
    public FileMetadataStore(IWebHostEnvironment environment, ILogger<FileMetadataStore> logger)
    {
        _path = Path.Combine(environment.ContentRootPath, "app-data", "fileInfo.json");
        try
        {
            _files = AtomicJsonStore.Read<List<StoredFileInfo>>(_path) ?? [];
            var migrated = false;
            foreach (var file in _files.Where(file => file.Id == Guid.Empty))
            {
                file.Id = Guid.NewGuid();
                migrated = true;
            }
            if (migrated)
            {
                AtomicJsonStore.Write(_path, _files);
                logger.LogInformation("Applied metadata migration VirtualDirectoriesV1 to {Count} existing files", _files.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load file metadata from {Path}", _path);
            _files = [];
        }
    }

    /// <inheritdoc />
    public List<StoredFileInfo> GetAll()
    {
        lock (_sync) return _files.Select(Clone).ToList();
    }

    /// <inheritdoc />
    public void Replace(IEnumerable<StoredFileInfo> files)
    {
        lock (_sync)
        {
            var replacement = files.Select(Clone).ToList();
            foreach (var file in replacement.Where(file => file.Id == Guid.Empty)) file.Id = Guid.NewGuid();
            AtomicJsonStore.Write(_path, replacement);
            _files = replacement;
        }
    }

    /// <inheritdoc />
    public long UsedSpace { get { lock (_sync) return _files.Sum(file => file.FileSize); } }

    /// <inheritdoc />
    public StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc)
    {
        lock (_sync)
        {
            var replacement = _files.Select(Clone).ToList();
            var file = replacement.SingleOrDefault(candidate => candidate.Id == fileId);
            if (file is null) return null;
            checked { file.ViewCount++; }
            file.LastViewedAt = DateTime.SpecifyKind(viewedAtUtc, DateTimeKind.Utc);
            file.DailyViewCounts ??= [];
            var day = file.LastViewedAt.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            checked { file.DailyViewCounts[day] = file.DailyViewCounts.GetValueOrDefault(day) + 1; }
            foreach (var oldDay in file.DailyViewCounts.Keys.OrderByDescending(key => key).Skip(366).ToList())
                file.DailyViewCounts.Remove(oldDay);
            AtomicJsonStore.Write(_path, replacement);
            _files = replacement;
            return Clone(file);
        }
    }

    private static StoredFileInfo Clone(StoredFileInfo file) => new()
    {
        Id = file.Id, FileName = file.FileName, FilePath = file.FilePath, FileSize = file.FileSize,
        UploadTime = file.UploadTime, ExpiryTime = file.ExpiryTime, Owner = file.Owner,
        DirectoryId = file.DirectoryId, ViewCount = file.ViewCount, LastViewedAt = file.LastViewedAt,
        DailyViewCounts = new Dictionary<string, long>(file.DailyViewCounts ?? [])
    };
}
