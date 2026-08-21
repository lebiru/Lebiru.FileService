using System.IO.Compression;
using Lebiru.FileService.Models;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Services;

/// <summary>Manages user-owned virtual directory metadata without relocating stored file objects.</summary>
public interface IVirtualDirectoryService
{
    /// <summary>Returns whether a directory belongs to the user.</summary>
    bool IsOwnedBy(Guid directoryId, string ownerUserId);

    /// <summary>Creates a directory under an owned parent or root.</summary>
    VirtualDirectory Create(string ownerUserId, string name, Guid? parentDirectoryId);

    /// <summary>Lists immediate root or directory contents with breadcrumbs.</summary>
    DirectoryContents GetContents(string ownerUserId, Guid? directoryId);

    /// <summary>Renames and/or moves an owned directory.</summary>
    VirtualDirectory Update(string ownerUserId, Guid directoryId, string? name,
        Guid? parentDirectoryId, bool parentSpecified);

    /// <summary>Deletes an empty owned directory.</summary>
    void Delete(string ownerUserId, Guid directoryId);

    /// <summary>Moves an owned file to an owned directory or root using metadata only.</summary>
    StoredFileInfo MoveFile(string ownerUserId, Guid fileId, Guid? directoryId);

    /// <summary>Creates a bounded-memory, disk-spooled archive of an owned directory tree.</summary>
    Task<DirectoryArchive> CreateArchiveAsync(string ownerUserId, Guid directoryId,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class VirtualDirectoryService : IVirtualDirectoryService
{
    private const int MaximumNameLength = 255;
    private const int MaximumDepth = 1024;
    private readonly object _sync = new();
    private readonly IVirtualDirectoryMetadataStore _directoryStore;
    private readonly IFileMetadataStore _fileStore;
    private readonly ILogger<VirtualDirectoryService> _logger;
    private readonly string _uploadsRoot;

    /// <summary>Creates the virtual directory application service.</summary>
    public VirtualDirectoryService(
        IVirtualDirectoryMetadataStore directoryStore,
        IFileMetadataStore fileStore,
        IWebHostEnvironment environment,
        ILogger<VirtualDirectoryService> logger)
    {
        _directoryStore = directoryStore;
        _fileStore = fileStore;
        _logger = logger;
        _uploadsRoot = Path.Combine(environment.ContentRootPath, "uploads");
    }

    /// <inheritdoc />
    public bool IsOwnedBy(Guid directoryId, string ownerUserId) =>
        _directoryStore.GetAll().Any(directory => directory.Id == directoryId && IsOwner(directory, ownerUserId));

    /// <inheritdoc />
    public VirtualDirectory Create(string ownerUserId, string name, Guid? parentDirectoryId)
    {
        EnsureOwner(ownerUserId);
        var validName = ValidateName(name);
        lock (_sync)
        {
            var directories = _directoryStore.GetAll();
            if (parentDirectoryId.HasValue && !directories.Any(directory =>
                    directory.Id == parentDirectoryId.Value && IsOwner(directory, ownerUserId)))
                throw new VirtualDirectoryNotFoundException();

            var now = DateTime.UtcNow;
            var directory = new VirtualDirectory
            {
                Id = Guid.NewGuid(), OwnerUserId = ownerUserId, ParentDirectoryId = parentDirectoryId,
                Name = validName, CreatedAt = now, UpdatedAt = now
            };
            directories.Add(directory);
            _directoryStore.Replace(directories);
            _logger.LogInformation("Directory {DirectoryId} created for {OwnerUserId} under {ParentDirectoryId}",
                directory.Id, ownerUserId, parentDirectoryId);
            return Clone(directory);
        }
    }

    /// <inheritdoc />
    public DirectoryContents GetContents(string ownerUserId, Guid? directoryId)
    {
        EnsureOwner(ownerUserId);
        var directories = _directoryStore.GetAll()
            .Where(directory => IsOwner(directory, ownerUserId)).ToList();
        VirtualDirectory? current = null;
        if (directoryId.HasValue)
            current = directories.SingleOrDefault(directory => directory.Id == directoryId.Value)
                ?? throw new VirtualDirectoryNotFoundException();

        var children = directories
            .Where(directory => directory.ParentDirectoryId == directoryId)
            .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToItem).ToList();
        var files = _fileStore.GetAll()
            .Where(file => IsOwner(file, ownerUserId) && file.DirectoryId == directoryId)
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(ToItem).ToList();
        return new DirectoryContents(current is null ? null : ToItem(current), children, files,
            BuildBreadcrumbs(current, directories));
    }

    /// <inheritdoc />
    public VirtualDirectory Update(string ownerUserId, Guid directoryId, string? name,
        Guid? parentDirectoryId, bool parentSpecified)
    {
        EnsureOwner(ownerUserId);
        if (name is null && !parentSpecified)
            throw new DirectoryValidationException("Supply a name and/or parentDirectoryId.");

        lock (_sync)
        {
            var directories = _directoryStore.GetAll();
            var owned = directories.Where(directory => IsOwner(directory, ownerUserId)).ToList();
            var directory = owned.SingleOrDefault(candidate => candidate.Id == directoryId)
                ?? throw new VirtualDirectoryNotFoundException();

            if (name is not null) directory.Name = ValidateName(name);
            if (parentSpecified)
            {
                if (parentDirectoryId == directoryId)
                    throw new DirectoryConflictException("A directory cannot contain itself.");
                if (parentDirectoryId.HasValue)
                {
                    if (!owned.Any(candidate => candidate.Id == parentDirectoryId.Value))
                        throw new VirtualDirectoryNotFoundException();
                    EnsureNoCycle(directoryId, parentDirectoryId.Value, owned);
                }
                directory.ParentDirectoryId = parentDirectoryId;
            }

            directory.UpdatedAt = DateTime.UtcNow;
            _directoryStore.Replace(directories);
            _logger.LogInformation("Directory {DirectoryId} updated for {OwnerUserId}; parent is {ParentDirectoryId}",
                directory.Id, ownerUserId, directory.ParentDirectoryId);
            return Clone(directory);
        }
    }

    /// <inheritdoc />
    public void Delete(string ownerUserId, Guid directoryId)
    {
        EnsureOwner(ownerUserId);
        lock (_sync)
        {
            var directories = _directoryStore.GetAll();
            var directory = directories.SingleOrDefault(candidate =>
                candidate.Id == directoryId && IsOwner(candidate, ownerUserId))
                ?? throw new VirtualDirectoryNotFoundException();
            if (directories.Any(candidate => candidate.ParentDirectoryId == directoryId && IsOwner(candidate, ownerUserId)) ||
                _fileStore.GetAll().Any(file => file.DirectoryId == directoryId && IsOwner(file, ownerUserId)))
                throw new DirectoryConflictException("The directory is not empty.");

            directories.Remove(directory);
            _directoryStore.Replace(directories);
            _logger.LogInformation("Empty directory {DirectoryId} deleted for {OwnerUserId}", directoryId, ownerUserId);
        }
    }

    /// <inheritdoc />
    public StoredFileInfo MoveFile(string ownerUserId, Guid fileId, Guid? directoryId)
    {
        EnsureOwner(ownerUserId);
        lock (_sync)
        {
            if (directoryId.HasValue && !IsOwnedBy(directoryId.Value, ownerUserId))
                throw new VirtualDirectoryNotFoundException();
            var files = _fileStore.GetAll();
            var file = files.SingleOrDefault(candidate => candidate.Id == fileId && IsOwner(candidate, ownerUserId))
                ?? throw new FileMetadataNotFoundException();
            file.DirectoryId = directoryId;
            _fileStore.Replace(files);
            _logger.LogInformation("File {FileId} moved for {OwnerUserId} to directory {DirectoryId}",
                fileId, ownerUserId, directoryId);
            return Clone(file);
        }
    }

    /// <inheritdoc />
    public async Task<DirectoryArchive> CreateArchiveAsync(string ownerUserId, Guid directoryId,
        CancellationToken cancellationToken)
    {
        EnsureOwner(ownerUserId);
        var directories = _directoryStore.GetAll().Where(directory => IsOwner(directory, ownerUserId)).ToList();
        var root = directories.SingleOrDefault(directory => directory.Id == directoryId)
            ?? throw new VirtualDirectoryNotFoundException();
        var files = _fileStore.GetAll().Where(file => IsOwner(file, ownerUserId)).ToList();
        var directoryLookup = directories.ToLookup(directory => directory.ParentDirectoryId);
        var fileLookup = files.ToLookup(file => file.DirectoryId);
        var tempPath = Path.Combine(Path.GetTempPath(), $"felix-directory-{Guid.NewGuid():N}.zip");
        var fileCount = 0;
        _logger.LogInformation("Directory archive requested for {DirectoryId} by {OwnerUserId}", directoryId, ownerUserId);
        try
        {
            await using var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            var rootPath = SafeZipSegment(root.Name);
            var stack = new Stack<(VirtualDirectory Directory, string Path, int Depth)>();
            var visited = new HashSet<Guid>();
            stack.Push((root, rootPath, 0));

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, path, depth) = stack.Pop();
                if (depth > MaximumDepth || !visited.Add(directory.Id))
                    throw new DirectoryConflictException("The directory hierarchy is too deep or contains a cycle.");
                archive.CreateEntry(path + "/");

                foreach (var file in fileLookup[directory.Id])
                {
                    var physicalPath = ResolveStoredFile(file);
                    if (!System.IO.File.Exists(physicalPath))
                    {
                        _logger.LogWarning("Skipping missing stored object for file {FileId}", file.Id);
                        continue;
                    }
                    var entry = archive.CreateEntry(path + "/" + SafeZipSegment(file.FileName), CompressionLevel.Fastest);
                    await using var entryStream = entry.Open();
                    await using var input = new FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(entryStream, 64 * 1024, cancellationToken);
                    fileCount++;
                }

                foreach (var child in directoryLookup[directory.Id])
                    stack.Push((child, path + "/" + SafeZipSegment(child.Name), depth + 1));
            }

            _logger.LogInformation("Directory archive completed for {DirectoryId} with {FileCount} files",
                directoryId, fileCount);
            return new DirectoryArchive(tempPath, SafeDownloadName(root.Name) + ".zip", fileCount);
        }
        catch (Exception ex)
        {
            try { System.IO.File.Delete(tempPath); } catch (IOException) { }
            _logger.LogWarning(ex, "Directory archive failed for {DirectoryId}", directoryId);
            throw;
        }
    }

    private string ResolveStoredFile(StoredFileInfo file)
    {
        var resolved = FilePathSecurity.ResolveFile(_uploadsRoot, file.FileName);
        var configured = Path.GetFullPath(file.FilePath);
        if (!string.Equals(resolved, configured, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new DirectoryConflictException("Stored file metadata points outside the configured object store.");
        return resolved;
    }

    private static IReadOnlyList<DirectoryBreadcrumb> BuildBreadcrumbs(
        VirtualDirectory? current, IReadOnlyCollection<VirtualDirectory> directories)
    {
        var breadcrumbs = new List<DirectoryBreadcrumb> { new(null, "Root") };
        if (current is null) return breadcrumbs;
        var byId = directories.ToDictionary(directory => directory.Id);
        var chain = new Stack<VirtualDirectory>();
        var visited = new HashSet<Guid>();
        var cursor = current;
        while (cursor is not null)
        {
            if (chain.Count >= MaximumDepth || !visited.Add(cursor.Id))
                throw new DirectoryConflictException("The directory hierarchy is too deep or contains a cycle.");
            chain.Push(cursor);
            cursor = cursor.ParentDirectoryId.HasValue && byId.TryGetValue(cursor.ParentDirectoryId.Value, out var parent)
                ? parent : null;
        }
        while (chain.Count > 0)
        {
            var directory = chain.Pop();
            breadcrumbs.Add(new(directory.Id, directory.Name));
        }
        return breadcrumbs;
    }

    private static void EnsureNoCycle(Guid movingId, Guid proposedParentId,
        IReadOnlyCollection<VirtualDirectory> ownedDirectories)
    {
        var byId = ownedDirectories.ToDictionary(directory => directory.Id);
        var visited = new HashSet<Guid>();
        var cursorId = proposedParentId;
        for (var depth = 0; depth <= MaximumDepth; depth++)
        {
            if (cursorId == movingId)
                throw new DirectoryConflictException("A directory cannot be moved into one of its descendants.");
            if (!visited.Add(cursorId))
                throw new DirectoryConflictException("The directory hierarchy contains a cycle.");
            if (!byId.TryGetValue(cursorId, out var cursor) || !cursor.ParentDirectoryId.HasValue) return;
            cursorId = cursor.ParentDirectoryId.Value;
        }
        throw new DirectoryConflictException("The directory hierarchy exceeds the maximum depth.");
    }

    private static string ValidateName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length == 0) throw new DirectoryValidationException("A directory name is required.");
        if (normalized is "." or "..") throw new DirectoryValidationException("The directory name is invalid.");
        if (normalized.Length > MaximumNameLength)
            throw new DirectoryValidationException($"Directory names cannot exceed {MaximumNameLength} characters.");
        if (normalized.Any(char.IsControl))
            throw new DirectoryValidationException("Directory names cannot contain control characters.");
        return normalized;
    }

    private static string SafeZipSegment(string value)
    {
        var sanitized = new string(value.Select(character =>
            character is '/' or '\\' || char.IsControl(character) ? '_' : character).ToArray()).Trim();
        while (sanitized.Contains("..", StringComparison.Ordinal)) sanitized = sanitized.Replace("..", "_", StringComparison.Ordinal);
        sanitized = sanitized.TrimStart('.', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    private static string SafeDownloadName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "directory" : sanitized;
    }

    private static bool IsOwner(VirtualDirectory directory, string ownerUserId) =>
        string.Equals(directory.OwnerUserId, ownerUserId, StringComparison.OrdinalIgnoreCase);

    private static bool IsOwner(StoredFileInfo file, string ownerUserId) =>
        string.Equals(file.Owner, ownerUserId, StringComparison.OrdinalIgnoreCase);

    private static void EnsureOwner(string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(ownerUserId)) throw new UnauthorizedAccessException();
    }

    private static DirectoryItem ToItem(VirtualDirectory directory) =>
        new(directory.Id, directory.Name, directory.ParentDirectoryId, directory.CreatedAt, directory.UpdatedAt);

    private static DirectoryFileItem ToItem(StoredFileInfo file) =>
        new(file.Id, file.FileName, file.FileSize, file.UploadTime, file.ExpiryTime, file.DirectoryId);

    private static VirtualDirectory Clone(VirtualDirectory directory) => new()
    {
        Id = directory.Id, OwnerUserId = directory.OwnerUserId, ParentDirectoryId = directory.ParentDirectoryId,
        Name = directory.Name, CreatedAt = directory.CreatedAt, UpdatedAt = directory.UpdatedAt
    };

    private static StoredFileInfo Clone(StoredFileInfo file) => new()
    {
        Id = file.Id, FileName = file.FileName, FilePath = file.FilePath, FileSize = file.FileSize,
        UploadTime = file.UploadTime, ExpiryTime = file.ExpiryTime, Owner = file.Owner,
        DirectoryId = file.DirectoryId
    };
}

internal sealed class VirtualDirectoryNotFoundException : Exception;
internal sealed class FileMetadataNotFoundException : Exception;
internal sealed class DirectoryValidationException(string message) : Exception(message);
internal sealed class DirectoryConflictException(string message) : Exception(message);
