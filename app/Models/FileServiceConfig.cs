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
        /// Maximum allowed file size in megabytes. Defaults to 100MB if not specified.
        /// </summary>
        public int MaxFileSizeMB { get; set; } = 100;

        /// <summary>
        /// Percentage threshold at which warnings will be shown. Defaults to 90%.
        /// </summary>
        public int WarningThresholdPercent { get; set; } = 90;

        /// <summary>
        /// Percentage threshold at which critical warnings will be shown. Defaults to 99%.
        /// </summary>
        public int CriticalThresholdPercent { get; set; } = 99;
    }
}