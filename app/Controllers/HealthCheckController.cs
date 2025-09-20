using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Controller for handling health check requests and displaying system health status
    /// </summary>
    [Authorize]
    public class HealthCheckController : Controller
    {
        private readonly HealthCheckService _healthCheckService;

        /// <summary>
        /// Initializes a new instance of the HealthCheckController
        /// </summary>
        /// <param name="healthCheckService">The health check service for monitoring system health</param>
        public HealthCheckController(HealthCheckService healthCheckService)
        {
            _healthCheckService = healthCheckService;
        }

        /// <summary>
        /// Displays the health check dashboard showing system health status
        /// </summary>
        /// <returns>The health check dashboard view</returns>
        public async Task<IActionResult> Index()
        {
            var report = await _healthCheckService.CheckHealthAsync();
            return View(report);
        }
    }
}