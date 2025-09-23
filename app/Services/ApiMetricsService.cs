using System.Threading;
using System.Text.Json;
using System.IO;

namespace Lebiru.FileService.Services
{
    /// <summary>
    /// Service for tracking API usage metrics
    /// </summary>
    public interface IApiMetricsService
    {
        /// <summary>
        /// Gets the total number of file uploads
        /// </summary>
        long UploadCount { get; }

        /// <summary>
        /// Gets the total number of file downloads
        /// </summary>
        long DownloadCount { get; }

        /// <summary>
        /// Gets the total number of file deletions
        /// </summary>
        long DeleteCount { get; }

        /// <summary>
        /// Gets the last time the metrics were updated
        /// </summary>
        DateTime LastUpdated { get; }

        /// <summary>
        /// Increments the upload counter
        /// </summary>
        void IncrementUploadCount();

        /// <summary>
        /// Increments the download counter
        /// </summary>
        void IncrementDownloadCount();

        /// <summary>
        /// Increments the delete counter
        /// </summary>
        void IncrementDeleteCount();
    }

    /// <summary>
    /// Implementation of the API metrics tracking service
    /// </summary>
    public class ApiMetricsService : IApiMetricsService
    {
        private readonly string _filePath;
        private readonly object _sync = new();
        private long _uploadCount;
        private long _downloadCount;
        private long _deleteCount;
        private DateTime _lastUpdated;

        /// <inheritdoc />
        public long UploadCount => _uploadCount;

        /// <inheritdoc />
        public long DownloadCount => _downloadCount;

        /// <inheritdoc />
        public long DeleteCount => _deleteCount;

        /// <inheritdoc />
        public DateTime LastUpdated => _lastUpdated;

        /// <summary>
        /// Initializes a new instance of the <see cref="ApiMetricsService"/> class.
        /// Sets up the persistence file path and loads existing metrics if available.
        /// </summary>
        public ApiMetricsService()
        {
            var dataDir = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
            Directory.CreateDirectory(dataDir);
            _filePath = Path.Combine(dataDir, "apiMetrics.json");
            Load();
            // Ensure file exists even if first run
            Save();
        }

        /// <inheritdoc />
        public void IncrementUploadCount()
        {
            Interlocked.Increment(ref _uploadCount);
            Save();
        }

        /// <inheritdoc />
        public void IncrementDownloadCount()
        {
            Interlocked.Increment(ref _downloadCount);
            Save();
        }

        /// <inheritdoc />
        public void IncrementDeleteCount()
        {
            Interlocked.Increment(ref _deleteCount);
            Save();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var state = JsonSerializer.Deserialize<MetricsState>(json);
                    if (state != null)
                    {
                        Interlocked.Exchange(ref _uploadCount, state.UploadCount);
                        Interlocked.Exchange(ref _downloadCount, state.DownloadCount);
                        Interlocked.Exchange(ref _deleteCount, state.DeleteCount);
                        _lastUpdated = state.LastUpdated;
                    }
                }
            }
            catch
            {
                // Ignore load failures; start fresh in memory
            }
        }

        private void Save()
        {
            try
            {
                lock (_sync)
                {
                    _lastUpdated = DateTime.UtcNow;
                    var state = new MetricsState
                    {
                        UploadCount = _uploadCount,
                        DownloadCount = _downloadCount,
                        DeleteCount = _deleteCount,
                        LastUpdated = _lastUpdated
                    };
                    var json = JsonSerializer.Serialize(state);
                    File.WriteAllText(_filePath, json);
                }
            }
            catch
            {
                // Swallow write errors to avoid disrupting API flow
            }
        }

        private class MetricsState
        {
            public long UploadCount { get; set; }
            public long DownloadCount { get; set; }
            public long DeleteCount { get; set; }
            public DateTime LastUpdated { get; set; }
        }
    }
}