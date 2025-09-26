using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;
using System.Collections.Generic;
using System.Collections;
using Microsoft.AspNetCore.Http;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Authorization;

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Controller for managing application configuration and settings
    /// </summary>
    [Route("Config")]
    [ApiController]
    [Authorize(Roles = UserRoles.Admin)]
    public class ConfigController : Controller
    {
        private readonly IUserService _userService;

        /// <summary>
        /// Initializes a new instance of the ConfigController class
        /// </summary>
        public ConfigController(IUserService userService)
        {
            _userService = userService;
        }
        // List of prefixes and exact matches for relevant environment variables
        private static readonly string[] RelevantPrefixes = new[]
        {
        "ASPNETCORE_",      // ASP.NET Core configuration
        "DOTNET_",          // .NET runtime configuration
        "FileService_",    // Our app's configuration
        "Hangfire_"        // Hangfire configuration
    };

        private static readonly string[] ExactMatches = new[]
        {
        "ASPNETCORE_ENVIRONMENT",
        "TZ",                        // Timezone
        "PORT",                      // Server port
        "VERSION",                   // App version
        "MaxDiskSpaceGB",           // Our disk space config
        "WarningThresholdPercent"   // Our warning threshold config
    };

        /// <summary>
        /// The configuration view page. Displays relevant environment variables and settings.
        /// </summary>
        /// <returns>The configuration view with filtered environment variables</returns>
        [HttpGet("View")]
        public IActionResult Index()
        {
            var envVariables = Environment.GetEnvironmentVariables();
            var config = HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var fileServiceConfig = config.GetSection("FileService").Get<FileServiceConfig>();

            var relevantVariables = new Dictionary<string, string>();

            // Add version from HttpContext items (set in Program.cs)
            if (HttpContext.Items["Version"] is string version)
            {
                relevantVariables["Application:Version"] = version;
            }
            else
            {
                // Try to read directly from appsettings.version.json if not in context
                try
                {
                    var versionConfigLocal = new ConfigurationBuilder()
                        .SetBasePath(Directory.GetCurrentDirectory())
                        .AddJsonFile("appsettings.version.json", optional: true)
                        .Build();

                    relevantVariables["Application:Version"] = versionConfigLocal["Version"] ?? "Unknown";
                }
                catch
                {
                    // Fall back to Unknown if the file can't be read
                    relevantVariables["Application:Version"] = "Unknown";
                }
            }

            // Add Git commit information if available
            if (HttpContext.Items["GitCommit"] is string gitCommit)
            {
                relevantVariables["Application:GitCommit"] = gitCommit;
            }

            // Add environment variables
            foreach (DictionaryEntry entry in envVariables)
            {
                var key = entry.Key.ToString() ?? string.Empty;

                // Check if this is a relevant variable
                if (RelevantPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) ||
                    ExactMatches.Any(match => key.Equals(match, StringComparison.OrdinalIgnoreCase)))
                {
                    relevantVariables[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            // Create descriptions for settings
            var descriptions = new Dictionary<string, string>
        {
            { "FileService:MaxFileSizeMB", "Maximum allowed size for individual file uploads in megabytes" },
            { "FileService:MaxDiskSpaceGB", "Maximum disk space allowed for file storage in gigabytes" },
            { "FileService:WarningThresholdPercent", "Percentage of disk space at which warning (yellow) indicators will be shown" },
            { "FileService:CriticalThresholdPercent", "Percentage of disk space at which critical (red) indicators will be shown" },
            { "ASPNETCORE_ENVIRONMENT", "Current environment (Development/Production) affecting application behavior and features" },
            { "TZ", "Server timezone setting used for timestamp calculations" },
            { "PORT", "The port number on which the application is running" },
            { "VERSION", "Current version of the application" },
            { "DOTNET_", "Configuration settings for .NET runtime behavior" },
            { "Hangfire_", "Job scheduling and background processing configuration" }
        };
            ViewBag.Descriptions = descriptions;

            // Get users for admin view
            ViewBag.Users = _userService.GetAllUsers();

            // Add configuration values
            var fileServiceSection = config.GetSection("FileService");
            if (fileServiceSection != null)
            {
                relevantVariables["FileService:MaxFileSizeMB"] =
                    fileServiceConfig?.MaxFileSizeMB.ToString() ?? "100";
                relevantVariables["FileService:MaxDiskSpaceGB"] =
                    fileServiceSection["MaxDiskSpaceGB"] ?? "100";
                relevantVariables["FileService:WarningThresholdPercent"] =
                    fileServiceConfig?.WarningThresholdPercent.ToString() ?? "90";
                relevantVariables["FileService:CriticalThresholdPercent"] =
                    fileServiceConfig?.CriticalThresholdPercent.ToString() ?? "99";
                relevantVariables["FileService:MaxDiskSpaceGB"] =
                    fileServiceSection["MaxDiskSpaceGB"] ?? "100";
                relevantVariables["FileService:WarningThresholdPercent"] =
                    fileServiceSection["WarningThresholdPercent"] ?? "90";
            }

            // Sort the dictionary by key for consistent display
            var sortedVariables = relevantVariables
                .OrderBy(kv => kv.Key)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Check the Dark Mode setting
            var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
            ViewBag.IsDarkMode = isDarkMode;

            return View(sortedVariables);
        }

        /// <summary>
        /// Toggles the dark mode setting for the current session
        /// </summary>
        /// <param name="enableDarkMode">True to enable dark mode, false to disable</param>
        /// <returns>A JSON result indicating success and the new dark mode state</returns>
        [HttpPost("ToggleDarkMode")]
        public IActionResult ToggleDarkMode([FromForm] bool enableDarkMode)
        {
            // Store the setting in the session
            HttpContext.Session.SetString("DarkMode", enableDarkMode ? "true" : "false");
            return Ok(new { Success = true, IsDarkMode = enableDarkMode });
        }

    }
}