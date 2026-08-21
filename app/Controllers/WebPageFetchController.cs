using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lebiru.FileService.Controllers;

/// <summary>Ingests a single public HTML response as a normal managed file.</summary>
[ApiController]
[Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
[EnableRateLimiting("web-page-fetch")]
[Route("api/fetch/web-page")]
public sealed class WebPageFetchController : ControllerBase
{
    private readonly IWebPageFetchService _webPageFetch;

    /// <summary>Creates the Web Page fetch endpoint.</summary>
    public WebPageFetchController(IWebPageFetchService webPageFetch) => _webPageFetch = webPageFetch;

    /// <summary>Fetches one HTTP/HTTPS HTML response and stores it for the authenticated user.</summary>
    /// <param name="request">The source URL and optional owned destination directory.</param>
    /// <param name="cancellationToken">Stops outbound retrieval if the request is abandoned.</param>
    /// <returns>The created managed-file resource and fetch metadata.</returns>
    [HttpPost]
    [ProducesResponseType<WebPageFetchResult>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Fetch(WebPageFetchRequest request, CancellationToken cancellationToken)
    {
        var owner = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(owner)) return Unauthorized();
        try
        {
            var result = await _webPageFetch.FetchAsync(owner, request.Url, request.DirectoryId, cancellationToken);
            return Created($"/File/DownloadFile?filename={Uri.EscapeDataString(result.FileName)}", result);
        }
        catch (WebPageFetchException exception)
        {
            return StatusCode(exception.StatusCode, new ProblemDetails
            {
                Status = exception.StatusCode,
                Title = exception.Message,
                Extensions = { ["code"] = exception.Code }
            });
        }
    }
}
