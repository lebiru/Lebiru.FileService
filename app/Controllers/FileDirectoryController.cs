using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers;

/// <summary>Manages virtual-directory placement for stored files.</summary>
[ApiController]
[Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
[Route("api/files")]
public sealed class FileDirectoryController : ControllerBase
{
    private readonly IVirtualDirectoryService _directories;

    /// <summary>Creates the file-directory API.</summary>
    public FileDirectoryController(IVirtualDirectoryService directories) => _directories = directories;

    /// <summary>Moves an owned file to an owned directory or to root without moving its stored object.</summary>
    [HttpPatch("{fileId:guid}/directory")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Move(Guid fileId, [FromBody] MoveFileDirectoryRequest request)
    {
        try
        {
            var file = _directories.MoveFile(CurrentUser(), fileId, request.DirectoryId);
            return Ok(new DirectoryFileItem(file.Id, file.FileName, file.FileSize, file.UploadTime,
                file.ExpiryTime, file.DirectoryId));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (VirtualDirectoryNotFoundException) { return NotFound(); }
        catch (FileMetadataNotFoundException) { return NotFound(); }
    }

    private string CurrentUser() => User.Identity?.Name ?? throw new UnauthorizedAccessException();
}
