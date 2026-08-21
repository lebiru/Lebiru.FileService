using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers;

/// <summary>Manages authenticated users' virtual directory hierarchies.</summary>
[ApiController]
[Authorize]
[Route("api/directories")]
public sealed class DirectoriesController : ControllerBase
{
    private readonly IVirtualDirectoryService _directories;

    /// <summary>Creates the directory API.</summary>
    public DirectoriesController(IVirtualDirectoryService directories) => _directories = directories;

    /// <summary>Creates a root or nested virtual directory.</summary>
    [HttpPost]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public IActionResult Create([FromBody] CreateDirectoryRequest request)
    {
        try
        {
            var directory = _directories.Create(CurrentUser(), request.Name, request.ParentDirectoryId);
            var item = ToItem(directory);
            return CreatedAtAction(nameof(GetContents), new { directoryId = item.Id }, item);
        }
        catch (Exception exception) { return MapException(exception); }
    }

    /// <summary>Lists root directories and files. A null file DirectoryId means root.</summary>
    [HttpGet("root/contents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetRootContents()
    {
        try { return Ok(_directories.GetContents(CurrentUser(), null)); }
        catch (Exception exception) { return MapException(exception); }
    }

    /// <summary>Lists immediate contents and breadcrumbs for an owned directory.</summary>
    [HttpGet("{directoryId:guid}/contents")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetContents(Guid directoryId)
    {
        try { return Ok(_directories.GetContents(CurrentUser(), directoryId)); }
        catch (Exception exception) { return MapException(exception); }
    }

    /// <summary>Renames and/or moves a directory. An explicit null parentDirectoryId moves it to root.</summary>
    [HttpPatch("{directoryId:guid}")]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Update(Guid directoryId, [FromBody] UpdateDirectoryRequest request)
    {
        try
        {
            return Ok(ToItem(_directories.Update(CurrentUser(), directoryId, request.Name,
                request.ParentDirectoryId, request.HasParentDirectoryId)));
        }
        catch (Exception exception) { return MapException(exception); }
    }

    /// <summary>Deletes an empty directory. Non-empty directories return 409 Conflict.</summary>
    [HttpDelete("{directoryId:guid}")]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Delete(Guid directoryId)
    {
        try
        {
            _directories.Delete(CurrentUser(), directoryId);
            return NoContent();
        }
        catch (Exception exception) { return MapException(exception); }
    }

    /// <summary>Downloads an owned directory tree as a disk-spooled ZIP archive.</summary>
    [HttpGet("{directoryId:guid}/archive")]
    [Produces("application/zip")]
    public async Task<IActionResult> Archive(Guid directoryId, CancellationToken cancellationToken)
    {
        try
        {
            var archive = await _directories.CreateArchiveAsync(CurrentUser(), directoryId, cancellationToken);
            HttpContext.Response.OnCompleted(() =>
            {
                try { System.IO.File.Delete(archive.Path); } catch (IOException) { }
                return Task.CompletedTask;
            });
            return PhysicalFile(archive.Path, "application/zip", archive.DownloadName, enableRangeProcessing: false);
        }
        catch (Exception exception) { return MapException(exception); }
    }

    private string CurrentUser() => User.Identity?.Name ?? throw new UnauthorizedAccessException();

    private IActionResult MapException(Exception exception) => exception switch
    {
        UnauthorizedAccessException => Unauthorized(),
        VirtualDirectoryNotFoundException or FileMetadataNotFoundException => NotFound(),
        DirectoryValidationException => BadRequest(new { error = exception.Message }),
        DirectoryConflictException => Conflict(new { error = exception.Message }),
        _ => throw exception
    };

    private static DirectoryItem ToItem(VirtualDirectory directory) =>
        new(directory.Id, directory.Name, directory.ParentDirectoryId, directory.CreatedAt, directory.UpdatedAt);
}
