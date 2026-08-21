using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models
{
  /// <summary>
  /// Status of a fetch operation
  /// </summary>
  public enum FetchStatus
  {
    /// <summary>
    /// The fetch operation has not been executed
    /// </summary>
    NotExecuted,

    /// <summary>
    /// The fetch operation is currently in progress
    /// </summary>
    InProgress,

    /// <summary>
    /// The fetch operation was successful
    /// </summary>
    Success,

    /// <summary>
    /// The fetch operation failed
    /// </summary>
    Failed
  }

  /// <summary>
  /// Model for an external fetch source configuration
  /// </summary>
  public class FetchSourceModel
  {
    /// <summary>
    /// Unique identifier for the fetch source
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly name for the fetch source
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, ErrorMessage = "Name cannot be longer than 100 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Type of fetch source (Gmail, WebPage, FTP, SFTP, HTTP, WebDAV, NetworkShare)
    /// </summary>
    [Required(ErrorMessage = "Type is required")]
    public string Type { get; set; } = string.Empty;

    /// <summary>The authenticated user that owns this fetch source.</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The destination virtual directory, or null for root.</summary>
    public Guid? DirectoryId { get; set; }

    /// <summary>
    /// Server URL or address
    /// </summary>
    [Required(ErrorMessage = "Server URL is required")]
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Username for authentication (if required)
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password for authentication (if required)
    /// Will be stored encrypted
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Path on the remote server
    /// </summary>
    public string? RemotePath { get; set; }

    /// <summary>
    /// Port number for the connection (if needed)
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// File pattern to match (e.g., *.pdf)
    /// </summary>
    public string? FilePattern { get; set; }

    /// <summary>
    /// Whether to search subdirectories recursively
    /// </summary>
    public bool IsRecursive { get; set; }

    /// <summary>
    /// Whether to delete files from source after successful fetch
    /// </summary>
    public bool DeleteAfterFetch { get; set; }

    /// <summary>
    /// How often to fetch files automatically (in minutes)
    /// </summary>
    [Range(5, 10080, ErrorMessage = "Fetch interval must be between 5 minutes and 10080 minutes (7 days)")]
    [Required(ErrorMessage = "Fetch interval is required")]
    public int FetchIntervalMinutes { get; set; }

    /// <summary>
    /// Whether the fetch source is active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When the source was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When files were last fetched from this source
    /// </summary>
    public DateTime? LastFetchTime { get; set; }

    /// <summary>
    /// How many files were fetched in the last operation
    /// </summary>
    public int? LastFetchFileCount { get; set; }

    #region Protocol-specific properties

    /// <summary>
    /// Whether to use passive mode for FTP
    /// </summary>
    public bool UsePassiveFtp { get; set; }

    /// <summary>
    /// Path to private key file for SFTP
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Whether to ignore SSL certificate errors
    /// </summary>
    public bool IgnoreSslErrors { get; set; }

    #region Gmail-specific properties

    /// <summary>
    /// OAuth access token for Gmail API
    /// </summary>
    public string? OAuthAccessToken { get; set; }

    /// <summary>
    /// OAuth refresh token for Gmail API
    /// </summary>
    public string? OAuthRefreshToken { get; set; }

    /// <summary>
    /// When the access token expires
    /// </summary>
    public DateTime? TokenExpiresAt { get; set; }

    /// <summary>
    /// Email search query for Gmail (e.g., "has:attachment subject:Report")
    /// </summary>
    public string? EmailSearchQuery { get; set; }

    /// <summary>
    /// Only fetch emails newer than this many days
    /// </summary>
    public int? EmailAgeInDays { get; set; }

    /// <summary>
    /// Only download attachments with these extensions (comma separated)
    /// </summary>
    public string? AttachmentTypes { get; set; }

    /// <summary>
    /// Whether to mark emails as read after fetching
    /// </summary>
    public bool MarkAsRead { get; set; }

    /// <summary>
    /// Whether to archive emails after fetching
    /// </summary>
    public bool ArchiveAfterFetch { get; set; }

    /// <summary>
    /// Whether to include email body as text file
    /// </summary>
    public bool IncludeEmailBody { get; set; }

    #endregion

    #endregion
  }

  /// <summary>
  /// Model for a fetch activity record
  /// </summary>
  public class FetchActivityModel
  {
    /// <summary>
    /// Unique identifier for the activity
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// ID of the related fetch source
    /// </summary>
    public string FetchSourceId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the fetch source (for display purposes)
    /// </summary>
    public string FetchSourceName { get; set; } = string.Empty;

    /// <summary>
    /// When the activity occurred
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Status of the fetch operation
    /// </summary>
    public FetchStatus Status { get; set; }

    /// <summary>
    /// Status message or error details
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Number of files fetched
    /// </summary>
    public int FetchedFileCount { get; set; } = 0;

    /// <summary>The owner of the fetch activity.</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The source type that produced the activity.</summary>
    public string? SourceType { get; set; }

    /// <summary>The requested source URL.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>The final URL after validated redirects.</summary>
    public string? FinalUrl { get; set; }

    /// <summary>The successful upstream HTTP status.</summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>The returned media type.</summary>
    public string? ContentType { get; set; }

    /// <summary>The number of response bytes stored.</summary>
    public long? BytesDownloaded { get; set; }

    /// <summary>The created FileService file identifier.</summary>
    public Guid? FileId { get; set; }
  }

  /// <summary>
  /// View model for the fetch sources page
  /// </summary>
  public class FetchViewModel
  {
    /// <summary>
    /// List of configured fetch sources
    /// </summary>
    public List<FetchSourceModel> FetchSources { get; set; } = new List<FetchSourceModel>();

    /// <summary>
    /// List of recent fetch activities
    /// </summary>
    public List<FetchActivityModel> LatestActivities { get; set; } = new List<FetchActivityModel>();
  }
}
