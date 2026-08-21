using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lebiru.FileService.Services;

/// <summary>Represents one minute of aggregated HTTP telemetry.</summary>
public sealed record TelemetryPoint(
    DateTime Timestamp,
    long Requests,
    long Errors,
    double AverageDurationMs);

/// <summary>Represents the current application telemetry snapshot.</summary>
public sealed record TelemetrySnapshot(
    DateTime GeneratedAt,
    long TotalRequests,
    long TotalErrors,
    double ErrorRate,
    double AverageDurationMs,
    long ManagedMemoryBytes,
    int ActiveRequests,
    IReadOnlyList<TelemetryPoint> Series);

/// <summary>Collects request telemetry for OpenTelemetry and the local dashboard.</summary>
public sealed class TelemetryService : IDisposable
{
    /// <summary>The OpenTelemetry meter name exported by this application.</summary>
    public const string MeterName = "Lebiru.FileService";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _requestCounter;
    private readonly Counter<long> _errorCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly UpDownCounter<long> _activeCounter;
    private readonly Counter<long> _webPageFetchCounter;
    private readonly Counter<long> _webPageFetchBytesCounter;
    private readonly Counter<long> _webPageFetchSsrfBlockedCounter;
    private readonly Histogram<double> _webPageFetchDurationHistogram;
    private readonly Counter<long> _fileViewsCounter;
    private readonly Counter<long> _fileViewDeduplicatedCounter;
    private readonly Counter<long> _fileViewFailuresCounter;
    private readonly ConcurrentDictionary<long, Bucket> _buckets = new();
    private long _totalRequests;
    private long _totalErrors;
    private long _totalDurationMicroseconds;
    private int _activeRequests;

    /// <summary>Creates the telemetry instruments.</summary>
    public TelemetryService()
    {
        _requestCounter = _meter.CreateCounter<long>("lebiru.http.server.requests", "{request}");
        _errorCounter = _meter.CreateCounter<long>("lebiru.http.server.errors", "{error}");
        _durationHistogram = _meter.CreateHistogram<double>("lebiru.http.server.duration", "ms");
        _activeCounter = _meter.CreateUpDownCounter<long>("lebiru.http.server.active_requests", "{request}");
        _webPageFetchCounter = _meter.CreateCounter<long>("lebiru.webpage.fetches", "{fetch}");
        _webPageFetchBytesCounter = _meter.CreateCounter<long>("lebiru.webpage.fetch.bytes", "By");
        _webPageFetchSsrfBlockedCounter = _meter.CreateCounter<long>("lebiru.webpage.fetch.ssrf_blocked", "{fetch}");
        _webPageFetchDurationHistogram = _meter.CreateHistogram<double>("lebiru.webpage.fetch.duration", "ms");
        _fileViewsCounter = _meter.CreateCounter<long>("fileservice_file_views_total", "{view}");
        _fileViewDeduplicatedCounter = _meter.CreateCounter<long>("fileservice_file_view_deduplicated_total", "{view}");
        _fileViewFailuresCounter = _meter.CreateCounter<long>("fileservice_file_view_record_failures_total", "{failure}");
    }

    /// <summary>Marks a request as active.</summary>
    public void RequestStarted()
    {
        Interlocked.Increment(ref _activeRequests);
        _activeCounter.Add(1);
    }

    /// <summary>Records a completed HTTP request.</summary>
    public void RequestCompleted(string method, string path, int statusCode, double durationMs)
    {
        Interlocked.Decrement(ref _activeRequests);
        _activeCounter.Add(-1);
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Add(ref _totalDurationMicroseconds, (long)(durationMs * 1000));

        var tags = new TagList
        {
            { "http.request.method", method },
            { "http.response.status_code", statusCode },
            { "url.path", NormalizePath(path) }
        };
        _requestCounter.Add(1, tags);
        _durationHistogram.Record(durationMs, tags);

        var isError = statusCode >= 500;
        if (isError)
        {
            Interlocked.Increment(ref _totalErrors);
            _errorCounter.Add(1, tags);
        }

        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var bucket = _buckets.GetOrAdd(minute, _ => new Bucket());
        Interlocked.Increment(ref bucket.Requests);
        Interlocked.Add(ref bucket.DurationMicroseconds, (long)(durationMs * 1000));
        if (isError) Interlocked.Increment(ref bucket.Errors);
        Trim(minute);
    }

    /// <summary>Returns the latest rolling telemetry snapshot.</summary>
    public TelemetrySnapshot GetSnapshot(int minutes = 30)
    {
        minutes = Math.Clamp(minutes, 5, 120);
        var currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var points = new List<TelemetryPoint>(minutes);
        for (var minute = currentMinute - minutes + 1; minute <= currentMinute; minute++)
        {
            _buckets.TryGetValue(minute, out var bucket);
            var requests = bucket == null ? 0 : Interlocked.Read(ref bucket.Requests);
            var errors = bucket == null ? 0 : Interlocked.Read(ref bucket.Errors);
            var duration = bucket == null ? 0 : Interlocked.Read(ref bucket.DurationMicroseconds);
            points.Add(new TelemetryPoint(
                DateTimeOffset.FromUnixTimeSeconds(minute * 60).UtcDateTime,
                requests,
                errors,
                requests == 0 ? 0 : duration / 1000d / requests));
        }

        var total = Interlocked.Read(ref _totalRequests);
        var totalErrors = Interlocked.Read(ref _totalErrors);
        var totalDuration = Interlocked.Read(ref _totalDurationMicroseconds);
        return new TelemetrySnapshot(
            DateTime.UtcNow,
            total,
            totalErrors,
            total == 0 ? 0 : totalErrors * 100d / total,
            total == 0 ? 0 : totalDuration / 1000d / total,
            GC.GetTotalMemory(false),
            Volatile.Read(ref _activeRequests),
            points);
    }

    /// <summary>Records a completed or rejected Web Page ingestion operation.</summary>
    public void RecordWebPageFetch(bool success, long bytes, double durationMs, bool ssrfBlocked)
    {
        var tags = new TagList { { "fetch.success", success } };
        _webPageFetchCounter.Add(1, tags);
        _webPageFetchDurationHistogram.Record(durationMs, tags);
        if (bytes > 0) _webPageFetchBytesCounter.Add(bytes, tags);
        if (ssrfBlocked) _webPageFetchSsrfBlockedCounter.Add(1);
    }

    /// <summary>Records one eligible dedicated file-page view.</summary>
    public void RecordFileView() => _fileViewsCounter.Add(1);

    /// <summary>Records one refresh suppressed by the view deduplication window.</summary>
    public void RecordFileViewDeduplicated() => _fileViewDeduplicatedCounter.Add(1);

    /// <summary>Records a failed authoritative view-summary update.</summary>
    public void RecordFileViewFailure() => _fileViewFailuresCounter.Add(1);

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    private void Trim(long currentMinute)
    {
        foreach (var minute in _buckets.Keys.Where(key => key < currentMinute - 120))
            _buckets.TryRemove(minute, out _);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Guid.TryParse(segment, out _) ? "{id}" : segment);
        return "/" + string.Join('/', segments);
    }

    private sealed class Bucket
    {
        public long Requests;
        public long Errors;
        public long DurationMicroseconds;
    }
}
