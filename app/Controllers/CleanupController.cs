using Hangfire;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/cleanup")]
public class CleanupController : ControllerBase
{
    private readonly CleanupJob _cleanupJob;

    public CleanupController(CleanupJob cleanupJob)
    {
        _cleanupJob = cleanupJob;
    }

    [HttpPost]
    public IActionResult TriggerCleanup()
    {
        // Enqueue the cleanup job
        BackgroundJob.Enqueue(() => _cleanupJob.Execute(null));
        return Ok("Cleanup job has been enqueued.");
    }
}