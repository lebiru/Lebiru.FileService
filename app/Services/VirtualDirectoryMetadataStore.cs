using Lebiru.FileService.Models;

namespace Lebiru.FileService.Services;

/// <summary>Provides synchronized, crash-safe virtual directory metadata persistence.</summary>
public interface IVirtualDirectoryMetadataStore
{
    /// <summary>Returns an isolated directory snapshot.</summary>
    List<VirtualDirectory> GetAll();

    /// <summary>Atomically replaces the directory snapshot.</summary>
    void Replace(IEnumerable<VirtualDirectory> directories);
}

/// <inheritdoc />
public sealed class VirtualDirectoryMetadataStore : IVirtualDirectoryMetadataStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private List<VirtualDirectory> _directories;

    /// <summary>Creates the JSON-backed directory store.</summary>
    public VirtualDirectoryMetadataStore(IWebHostEnvironment environment, ILogger<VirtualDirectoryMetadataStore> logger)
    {
        _path = Path.Combine(environment.ContentRootPath, "app-data", "directories.json");
        try { _directories = AtomicJsonStore.Read<List<VirtualDirectory>>(_path) ?? []; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load virtual directory metadata from {Path}", _path);
            _directories = [];
        }
    }

    /// <inheritdoc />
    public List<VirtualDirectory> GetAll()
    {
        lock (_sync) return _directories.Select(Clone).ToList();
    }

    /// <inheritdoc />
    public void Replace(IEnumerable<VirtualDirectory> directories)
    {
        lock (_sync)
        {
            var replacement = directories.Select(Clone).ToList();
            AtomicJsonStore.Write(_path, replacement);
            _directories = replacement;
        }
    }

    private static VirtualDirectory Clone(VirtualDirectory directory) => new()
    {
        Id = directory.Id,
        OwnerUserId = directory.OwnerUserId,
        ParentDirectoryId = directory.ParentDirectoryId,
        Name = directory.Name,
        CreatedAt = directory.CreatedAt,
        UpdatedAt = directory.UpdatedAt
    };
}
