namespace Lebiru.FileService.Models
{
    /// <summary>
    /// Configuration settings for the FileService
    /// </summary>
    public class FileServiceConfig
    {
        /// <summary>
        /// Maximum allowed disk space in gigabytes. Defaults to 100GB if not specified.
        /// </summary>
        public int MaxDiskSpaceGB { get; set; } = 100;

        /// <summary>
        /// Percentage threshold at which warnings will be shown. Defaults to 90%.
        /// </summary>
        public int WarningThresholdPercent { get; set; } = 90;
    }

    internal class ServerSpaceInfo
    {
        private readonly long _maxDiskSpace;
        
        public ServerSpaceInfo(long maxDiskSpaceBytes)
        {
            _maxDiskSpace = maxDiskSpaceBytes;
        }

        public long TotalSpace => _maxDiskSpace;
        public long FreeSpace => Math.Max(0, _maxDiskSpace - UsedSpace);
        public long UsedSpace { get; set; }
    }
}