using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Tests.Services;

public sealed class FileViewTrackingServiceTests : IDisposable
{
    private readonly TelemetryService _telemetry = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 1000 });

    [Fact]
    public void FirstViewUpdatesSummaryAndUtcDailyRollup()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        var store = StoreWithFile();
        var result = Service(store, time).Record(store.FileId, "alice");

        Assert.True(result.Counted);
        Assert.Equal(1, result.File!.ViewCount);
        Assert.Equal(time.GetUtcNow().UtcDateTime, result.File.LastViewedAt);
        Assert.Equal(1, result.File.DailyViewCounts["2026-08-21"]);
    }

    [Fact]
    public void SameViewerIsDeduplicatedInsideWindowAndCountsAfterWindow()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        var store = StoreWithFile();
        var service = Service(store, time);

        Assert.True(service.Record(store.FileId, "alice").Counted);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(service.Record(store.FileId, "alice").Deduplicated);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(service.Record(store.FileId, "alice").Counted);

        Assert.Equal(2, store.GetAll().Single().ViewCount);
    }

    [Fact]
    public void DifferentViewersCountIndependently()
    {
        var store = StoreWithFile();
        var service = Service(store, new TestTimeProvider(DateTimeOffset.UtcNow));

        service.Record(store.FileId, "alice");
        service.Record(store.FileId, "bob");

        Assert.Equal(2, store.GetAll().Single().ViewCount);
    }

    [Fact]
    public async Task ConcurrentDistinctViewsDoNotLoseIncrements()
    {
        var store = StoreWithFile();
        var service = Service(store, new TestTimeProvider(DateTimeOffset.UtcNow), dedupeSeconds: 0);

        await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(viewer => Task.Run(() => service.Record(store.FileId, $"viewer-{viewer}"))));

        Assert.Equal(100, store.GetAll().Single().ViewCount);
    }

    [Fact]
    public void DisabledTrackingPreservesExistingCount()
    {
        var store = StoreWithFile();
        var service = Service(store, new TestTimeProvider(DateTimeOffset.UtcNow), enabled: false);

        var result = service.Record(store.FileId, "alice");

        Assert.False(result.Counted);
        Assert.Equal(0, store.GetAll().Single().ViewCount);
    }

    private FileViewTrackingService Service(MemoryStore store, TimeProvider time,
        bool enabled = true, int dedupeSeconds = 300) => new(store, _cache, _telemetry, time,
        Options.Create(new FileViewOptions
        {
            Enabled = enabled, DeduplicationWindowSeconds = dedupeSeconds
        }), NullLogger<FileViewTrackingService>.Instance);

    private static MemoryStore StoreWithFile()
    {
        var file = new StoredFileInfo
        {
            Id = Guid.NewGuid(), FileName = "report.pdf", FilePath = "report.pdf", Owner = "alice"
        };
        return new MemoryStore(file);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _telemetry.Dispose();
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }

    private sealed class MemoryStore(StoredFileInfo file) : IFileMetadataStore
    {
        private readonly object _sync = new();
        private StoredFileInfo _file = Clone(file);
        public Guid FileId => _file.Id;
        public List<StoredFileInfo> GetAll() { lock (_sync) return [Clone(_file)]; }
        public void Replace(IEnumerable<StoredFileInfo> files) { lock (_sync) _file = Clone(files.Single()); }
        public long UsedSpace => _file.FileSize;
        public StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc)
        {
            lock (_sync)
            {
                if (_file.Id != fileId) return null;
                _file.ViewCount++;
                _file.LastViewedAt = viewedAtUtc;
                var day = viewedAtUtc.ToString("yyyy-MM-dd");
                _file.DailyViewCounts[day] = _file.DailyViewCounts.GetValueOrDefault(day) + 1;
                return Clone(_file);
            }
        }
        private static StoredFileInfo Clone(StoredFileInfo item) => new()
        {
            Id = item.Id, FileName = item.FileName, FilePath = item.FilePath, Owner = item.Owner,
            DirectoryId = item.DirectoryId, FileSize = item.FileSize, UploadTime = item.UploadTime,
            ExpiryTime = item.ExpiryTime, ViewCount = item.ViewCount, LastViewedAt = item.LastViewedAt,
            DailyViewCounts = new Dictionary<string, long>(item.DailyViewCounts)
        };
    }
}
