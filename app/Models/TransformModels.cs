using System;
using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models
{
  /// <summary>
  /// Type of transformation to apply
  /// </summary>
  public enum TransformType
  {
    /// <summary>
    /// Extract content using regex pattern
    /// </summary>
    RegexParsing
  }

  /// <summary>
  /// Status of a transform operation
  /// </summary>
  public enum TransformStatus
  {
    /// <summary>
    /// The transform operation has not been executed
    /// </summary>
    NotExecuted,

    /// <summary>
    /// The transform operation is currently in progress
    /// </summary>
    InProgress,

    /// <summary>
    /// The transform operation was successful
    /// </summary>
    Success,

    /// <summary>
    /// The transform operation failed
    /// </summary>
    Failed
  }

  /// <summary>
  /// Model for a file transformation configuration
  /// </summary>
  public class TransformModel
  {
    /// <summary>
    /// Unique identifier for the transformation
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User-friendly name for the transformation
    /// </summary>
    [Required(ErrorMessage = "Title is required")]
    [StringLength(100, ErrorMessage = "Title cannot be longer than 100 characters")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// File pattern to match (e.g., *.txt, report_*.csv)
    /// </summary>
    [Required(ErrorMessage = "File pattern is required")]
    public string FilePattern { get; set; } = string.Empty;

    /// <summary>
    /// Type of transformation to apply
    /// </summary>
    public TransformType TransformType { get; set; } = TransformType.RegexParsing;

    /// <summary>
    /// Regex pattern for extraction (when TransformType is RegexParsing)
    /// </summary>
    [Required(ErrorMessage = "Regex pattern is required")]
    public string RegexPattern { get; set; } = string.Empty;

    /// <summary>
    /// How often to run the transformation automatically (in minutes)
    /// </summary>
    [Range(5, 10080, ErrorMessage = "Transform interval must be between 5 minutes and 10080 minutes (7 days)")]
    public int TransformIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Whether the transformation is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When the transformation was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the transformation was last executed
    /// </summary>
    public DateTime? LastExecutedTime { get; set; }

    /// <summary>
    /// How many files were processed in the last operation
    /// </summary>
    public int? LastProcessedFileCount { get; set; }

    /// <summary>
    /// Whether to modify the existing file or create a new one
    /// </summary>
    public bool ModifyExistingFile { get; set; } = false;
  }

  /// <summary>
  /// Model for a transform activity record
  /// </summary>
  public class TransformActivityModel
  {
    /// <summary>
    /// Unique identifier for the activity
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// ID of the transform source that was used
    /// </summary>
    public string TransformId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the transform source
    /// </summary>
    public string TransformTitle { get; set; } = string.Empty;

    /// <summary>
    /// Status of the transform activity
    /// </summary>
    public TransformStatus Status { get; set; } = TransformStatus.NotExecuted;

    /// <summary>
    /// Message associated with the activity
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// When the activity occurred
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of files processed
    /// </summary>
    public int FilesProcessed { get; set; }

    /// <summary>
    /// Any error that occurred
    /// </summary>
    public string? Error { get; set; }
  }

  /// <summary>
  /// View model for the Transform page
  /// </summary>
  public class TransformViewModel
  {
    /// <summary>
    /// List of transform sources
    /// </summary>
    public List<TransformModel> TransformSources { get; set; } = new List<TransformModel>();

    /// <summary>
    /// List of recent transform activities
    /// </summary>
    public List<TransformActivityModel> LatestActivities { get; set; } = new List<TransformActivityModel>();
  }

  /// <summary>
  /// Request model for direct file transformation from the UI
  /// </summary>
  public class TransformFilesRequest
  {
    /// <summary>
    /// List of filenames to transform
    /// </summary>
    public List<string> Files { get; set; } = new List<string>();

    /// <summary>
    /// Regex pattern to match
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>
    /// Replacement text
    /// </summary>
    public string Replacement { get; set; } = "";
  }
}