namespace Lebiru.FileService.Models;

/// <summary>Represents a logical user-owned directory stored independently from file bytes.</summary>
public sealed class VirtualDirectory
{
    /// <summary>The directory identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The username that owns the directory.</summary>
    public required string OwnerUserId { get; set; }

    /// <summary>The parent directory identifier; null represents root.</summary>
    public Guid? ParentDirectoryId { get; set; }

    /// <summary>The logical display name.</summary>
    public required string Name { get; set; }

    /// <summary>When the directory was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the directory metadata was last changed.</summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Creates a virtual directory.</summary>
public sealed class CreateDirectoryRequest
{
    /// <summary>The logical directory name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The parent directory, or null to create at root.</summary>
    public Guid? ParentDirectoryId { get; set; }
}

/// <summary>Updates a virtual directory's name and/or parent.</summary>
public sealed class UpdateDirectoryRequest
{
    private Guid? _parentDirectoryId;

    /// <summary>The new logical name, when renaming.</summary>
    public string? Name { get; set; }

    /// <summary>The new parent directory, or null to move to root.</summary>
    public Guid? ParentDirectoryId
    {
        get => _parentDirectoryId;
        set
        {
            _parentDirectoryId = value;
            HasParentDirectoryId = true;
        }
    }

    /// <summary>Whether the JSON request explicitly supplied parentDirectoryId.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasParentDirectoryId { get; private set; }
}

/// <summary>Moves a file to a virtual directory or root.</summary>
public sealed class MoveFileDirectoryRequest
{
    /// <summary>The target directory, or null for root.</summary>
    public Guid? DirectoryId { get; set; }
}

/// <summary>A directory representation safe for API responses.</summary>
/// <param name="Id">The directory identifier.</param>
/// <param name="Name">The logical directory name.</param>
/// <param name="ParentDirectoryId">The parent identifier, or null for root.</param>
/// <param name="CreatedAt">The creation timestamp.</param>
/// <param name="UpdatedAt">The last update timestamp.</param>
public sealed record DirectoryItem(
    Guid Id, string Name, Guid? ParentDirectoryId, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A file representation used in directory listings.</summary>
/// <param name="Id">The stable file identifier.</param>
/// <param name="FileName">The user-visible filename.</param>
/// <param name="FileSize">The file size in bytes.</param>
/// <param name="UploadTime">The upload timestamp.</param>
/// <param name="ExpiryTime">The optional expiry timestamp.</param>
/// <param name="DirectoryId">The containing directory, or null for root.</param>
public sealed record DirectoryFileItem(
    Guid Id, string FileName, long FileSize, DateTime UploadTime, DateTime? ExpiryTime, Guid? DirectoryId);

/// <summary>A single breadcrumb in a virtual directory hierarchy.</summary>
/// <param name="Id">The directory identifier; null represents root.</param>
/// <param name="Name">The breadcrumb label.</param>
public sealed record DirectoryBreadcrumb(Guid? Id, string Name);

/// <summary>Immediate directory contents plus ordered navigation breadcrumbs.</summary>
/// <param name="Directory">The current directory, or null for root.</param>
/// <param name="Directories">Immediate child directories.</param>
/// <param name="Files">Immediate child files.</param>
/// <param name="Breadcrumbs">Root-to-current navigation metadata.</param>
public sealed record DirectoryContents(
    DirectoryItem? Directory, IReadOnlyList<DirectoryItem> Directories,
    IReadOnlyList<DirectoryFileItem> Files, IReadOnlyList<DirectoryBreadcrumb> Breadcrumbs);

/// <summary>A disk-spooled ZIP archive ready to stream to the client.</summary>
/// <param name="Path">The temporary archive path.</param>
/// <param name="DownloadName">The safe HTTP download filename.</param>
/// <param name="FileCount">The number of files included.</param>
public sealed record DirectoryArchive(string Path, string DownloadName, int FileCount);
