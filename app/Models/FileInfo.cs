using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models
{
    /// <summary>
    /// Represents a stored file with expiry information
    /// </summary>
    public class FileInfo
    {
        /// <summary>
        /// The name of the file
        /// </summary>
        public required string FileName { get; set; }

        /// <summary>
        /// The path to the file on disk
        /// </summary>
        public required string FilePath { get; set; }

        /// <summary>
        /// When the file will expire and be deleted
        /// </summary>
        public DateTime? ExpiryTime { get; set; }

        /// <summary>
        /// When the file was uploaded
        /// </summary>
        public DateTime UploadTime { get; set; }

        /// <summary>
        /// The size of the file in bytes
        /// </summary>
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Represents the available expiry time options for files
    /// </summary>
    public enum ExpiryOption
    {
        /// <summary>
        /// File never expires
        /// </summary>
        [Display(Name = "Never")]
        Never = 0,

        /// <summary>
        /// File expires after 1 minute
        /// </summary>
        [Display(Name = "1 Minute")]
        OneMinute = 1,

        /// <summary>
        /// File expires after 1 hour
        /// </summary>
        [Display(Name = "1 Hour")]
        OneHour = 2,

        /// <summary>
        /// File expires after 1 day
        /// </summary>
        [Display(Name = "1 Day")]
        OneDay = 3,

        /// <summary>
        /// File expires after 1 week
        /// </summary>
        [Display(Name = "1 Week")]
        OneWeek = 4
    }
}
