#pragma warning disable CS1591
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lebiru.FileService.Controllers;

[ApiController]
[Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
[EnableRateLimiting("destinations")]
[Route("api/destinations")]
public sealed class DestinationsController(IDestinationService service) : ControllerBase
{
    [HttpGet] public IActionResult List() => Ok(service.List(CurrentUser()));
    [HttpGet("{id:guid}")] public IActionResult Get(Guid id) => service.Get(CurrentUser(), id) is { } item ? Ok(item) : NotFound();
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Create(DestinationUpsertRequest request) => Execute(() =>
    { var created = service.Create(CurrentUser(), request); return CreatedAtAction(nameof(Get), new { id = created.Id }, created); });
    [HttpPatch("{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult Update(Guid id, DestinationUpsertRequest request) => Execute(() =>
        service.Update(CurrentUser(), id, request) is { } item ? Ok(item) : NotFound());
    [HttpDelete("{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult Delete(Guid id) => service.Delete(CurrentUser(), id) ? NoContent() : NotFound();
    [HttpPost("{id:guid}/test"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid id, CancellationToken cancellationToken)
    { try { return await service.TestAsync(CurrentUser(), id, cancellationToken) is { } result ? Ok(result) : NotFound(); }
      catch (DestinationException exception) { return Problem(exception); } }
    [HttpGet("{id:guid}/deliveries")]
    public IActionResult DestinationDeliveries(Guid id) => service.GetDestinationDeliveries(CurrentUser(), id) is { } history ? Ok(history) : NotFound();
    [HttpPost("/api/files/{fileId:guid}/deliver"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Deliver(Guid fileId, DeliverFileRequest request, CancellationToken cancellationToken)
    { try { return Ok(await service.DeliverAsync(CurrentUser(), User.IsInRole(UserRoles.Admin), fileId, request.DestinationId, cancellationToken)); }
      catch (DestinationException exception) { return Problem(exception); } }
    [HttpGet("/api/files/{fileId:guid}/deliveries")]
    public IActionResult FileDeliveries(Guid fileId) => service.GetFileDeliveries(CurrentUser(), User.IsInRole(UserRoles.Admin), fileId) is { } history ? Ok(history) : NotFound();
    private IActionResult Execute(Func<IActionResult> action) { try { return action(); } catch (DestinationException exception) { return Problem(exception); } }
    private ObjectResult Problem(DestinationException exception)
    { var status = exception.Code.EndsWith("NotFound", StringComparison.Ordinal) ? 404 : exception.Code == "ConcurrencyLimit" ? 429 : 400;
      return StatusCode(status, new ProblemDetails { Status = status, Title = exception.Message, Extensions = { ["code"] = exception.Code } }); }
    private string CurrentUser() => User.Identity?.Name ?? throw new UnauthorizedAccessException();
}

[Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
[Route("Destinations")]
public sealed class DestinationPagesController : Controller
{
    [HttpGet] public IActionResult Index() => View("Index");
}
#pragma warning restore CS1591
