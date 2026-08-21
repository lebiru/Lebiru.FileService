using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models;

/// <summary>Configures dedicated file-page view tracking.</summary>
public sealed class FileViewOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FileViews";

    /// <summary>Whether eligible dedicated-page views are recorded.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum interval between counted views by the same viewer for one file.</summary>
    [Range(0, 86400)]
    public int DeduplicationWindowSeconds { get; set; } = 300;
}

/// <summary>A UTC daily view-count point for one file.</summary>
public sealed record FileViewPoint(DateTime Date, long ViewCount);

/// <summary>Read-only model for the dedicated file page.</summary>
public sealed record FileDetailsViewModel(
    Guid Id,
    string FileName,
    long FileSize,
    DateTime UploadTime,
    DateTime? ExpiryTime,
    Guid? DirectoryId,
    string ContentType,
    long ViewCount,
    DateTime? LastViewedAt,
    IReadOnlyList<FileViewPoint> ViewSeries,
    string? TextContent);

/// <summary>Authorized, read-only dedicated file metadata returned by the API.</summary>
public sealed record FileDetailsResponse(
    Guid Id,
    string Name,
    long Size,
    string ContentType,
    long ViewCount,
    DateTime? LastViewedAt,
    IReadOnlyList<FileViewPoint> ViewSeries);
