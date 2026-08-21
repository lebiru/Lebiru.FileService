using Lebiru.FileService.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Services;

/// <summary>Result of one explicit dedicated-page view-recording attempt.</summary>
public sealed record FileViewRecordResult(StoredFileInfo? File, bool Counted, bool Deduplicated, bool Failed);

/// <summary>Records authorized dedicated-page views without coupling tracking to generic file reads.</summary>
public interface IFileViewTrackingService
{
    /// <summary>Records at most one eligible view for a file/viewer during the configured window.</summary>
    FileViewRecordResult Record(Guid fileId, string viewerKey);
}

/// <inheritdoc />
public sealed class FileViewTrackingService : IFileViewTrackingService
{
    private readonly object _deduplicationSync = new();
    private readonly IFileMetadataStore _metadata;
    private readonly IMemoryCache _cache;
    private readonly TelemetryService _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly FileViewOptions _options;
    private readonly ILogger<FileViewTrackingService> _logger;

    /// <summary>Creates the tracking service.</summary>
    public FileViewTrackingService(IFileMetadataStore metadata, IMemoryCache cache,
        TelemetryService telemetry, TimeProvider timeProvider, IOptions<FileViewOptions> options,
        ILogger<FileViewTrackingService> logger)
    {
        _metadata = metadata;
        _cache = cache;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public FileViewRecordResult Record(Guid fileId, string viewerKey)
    {
        if (!_options.Enabled)
            return new FileViewRecordResult(_metadata.GetAll().SingleOrDefault(file => file.Id == fileId), false, false, false);

        var cacheKey = $"file-view:{fileId:N}:{viewerKey}";
        lock (_deduplicationSync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_options.DeduplicationWindowSeconds > 0 &&
                _cache.TryGetValue<DateTimeOffset>(cacheKey, out var eligibleAfter) && now < eligibleAfter)
            {
                _telemetry.RecordFileViewDeduplicated();
                _logger.LogDebug("FileViewDeduplicated for {FileId}", fileId);
                return new FileViewRecordResult(
                    _metadata.GetAll().SingleOrDefault(file => file.Id == fileId), false, true, false);
            }

            try
            {
                var updated = _metadata.RecordView(fileId, now.UtcDateTime);
                if (updated is null) return new FileViewRecordResult(null, false, false, false);
                if (_options.DeduplicationWindowSeconds > 0)
                    _cache.Set(cacheKey, now.AddSeconds(_options.DeduplicationWindowSeconds), new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.DeduplicationWindowSeconds),
                        Size = 1
                    });
                _telemetry.RecordFileView();
                _logger.LogInformation("FileViewRecorded for {FileId}", fileId);
                return new FileViewRecordResult(updated, true, false, false);
            }
            catch (Exception exception)
            {
                _telemetry.RecordFileViewFailure();
                _logger.LogWarning(exception, "FileViewRecordFailed for {FileId}", fileId);
                return new FileViewRecordResult(null, false, false, true);
            }
        }
    }
}
