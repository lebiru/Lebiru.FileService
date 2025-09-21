using System.Threading;

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
        private long _uploadCount;
        private long _downloadCount;
        private long _deleteCount;

        /// <inheritdoc />
        public long UploadCount => _uploadCount;

        /// <inheritdoc />
        public long DownloadCount => _downloadCount;

        /// <inheritdoc />
        public long DeleteCount => _deleteCount;

        /// <inheritdoc />
        public void IncrementUploadCount()
        {
            Interlocked.Increment(ref _uploadCount);
        }

        /// <inheritdoc />
        public void IncrementDownloadCount()
        {
            Interlocked.Increment(ref _downloadCount);
        }

        /// <inheritdoc />
        public void IncrementDeleteCount()
        {
            Interlocked.Increment(ref _deleteCount);
        }
    }
}