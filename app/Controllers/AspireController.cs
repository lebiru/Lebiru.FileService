using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers;

/// <summary>Provides administrator-only access to the configured Aspire dashboard.</summary>
[Authorize(Roles = UserRoles.Admin)]
[Route("Aspire")]
public sealed class AspireController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    /// <summary>Creates the Aspire dashboard redirect controller.</summary>
    public AspireController(IConfiguration configuration, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    /// <summary>Redirects an administrator to the configured Aspire dashboard.</summary>
    [HttpGet("")]
    public IActionResult Index()
    {
        var configuredUrl = _configuration["Aspire:DashboardUrl"];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var dashboardUri) ||
            (dashboardUri.Scheme != Uri.UriSchemeHttp && dashboardUri.Scheme != Uri.UriSchemeHttps))
        {
            return NotFound();
        }

        if (!_environment.IsDevelopment() && dashboardUri.Scheme != Uri.UriSchemeHttps)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "The Aspire dashboard must use HTTPS outside development.");
        }

        return Redirect(dashboardUri.AbsoluteUri);
    }
}
