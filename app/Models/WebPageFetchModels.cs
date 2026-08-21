using System.ComponentModel.DataAnnotations;

namespace Lebiru.FileService.Models;

/// <summary>Configures bounded and secure Web Page ingestion.</summary>
public sealed class WebPageFetchOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "WebPageFetch";

    /// <summary>Whether outbound Web Page fetching is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Total operation timeout in seconds.</summary>
    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Maximum response body size in bytes.</summary>
    [Range(1024, 100 * 1024 * 1024)]
    public long MaxResponseBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Maximum number of explicit redirects.</summary>
    [Range(0, 10)]
    public int MaxRedirects { get; set; } = 5;

    /// <summary>Maximum simultaneous Web Page fetches per authenticated user.</summary>
    [Range(1, 10)]
    public int MaxConcurrentFetchesPerUser { get; set; } = 2;

    /// <summary>Maximum immediate API requests per user per minute.</summary>
    [Range(1, 120)]
    public int RequestsPerMinute { get; set; } = 10;
}

/// <summary>Requests immediate ingestion of one HTML page.</summary>
public sealed class WebPageFetchRequest
{
    /// <summary>The absolute HTTP or HTTPS URL to retrieve.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    /// <summary>The owned destination directory, or null for root.</summary>
    public Guid? DirectoryId { get; set; }
}

/// <summary>Describes a completed Web Page ingestion.</summary>
public sealed record WebPageFetchResult(
    Guid FileId,
    string FileName,
    Guid? DirectoryId,
    string SourceUrl,
    string FinalUrl,
    int HttpStatusCode,
    string ContentType,
    long BytesDownloaded,
    DateTime FetchedAt);
