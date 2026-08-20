using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers;

/// <summary>Displays live OpenTelemetry-backed application telemetry.</summary>
[Authorize]
[Route("Telemetry")]
public sealed class TelemetryController : Controller
{
    private readonly TelemetryService _telemetry;

    /// <summary>Creates the telemetry controller.</summary>
    public TelemetryController(TelemetryService telemetry) => _telemetry = telemetry;

    /// <summary>Displays the telemetry dashboard.</summary>
    [HttpGet("")]
    public IActionResult Index() => View(_telemetry.GetSnapshot());

    /// <summary>Returns a rolling telemetry snapshot for dashboard refreshes.</summary>
    [HttpGet("snapshot")]
    public IActionResult Snapshot([FromQuery] int minutes = 30) => Json(_telemetry.GetSnapshot(minutes));
}
