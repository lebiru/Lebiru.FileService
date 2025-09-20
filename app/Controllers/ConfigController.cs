using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;
using System.Collections.Generic;
using System.Collections;
using Microsoft.AspNetCore.Http;

namespace Lebiru.FileService.Controllers
{
  /// <summary>
  /// Controller for managing application configuration and settings
  /// </summary>
  [Route("Config")]
  [ApiController]
  [Microsoft.AspNetCore.Authorization.Authorize]
  public class ConfigController : Controller
  {
    /// <summary>
    /// The home page for the app. Displays current files hosted on FileService.
    /// </summary>
    /// <returns></returns>
    [HttpGet("View")]
    public IActionResult Index()
    {
      var envVariables = Environment.GetEnvironmentVariables();

      // Convert to a dictionary for easier handling in the view
      var envVarDict = envVariables.Cast<DictionaryEntry>()
                                    .ToDictionary(
                                        entry => entry.Key.ToString() ?? string.Empty,
                                        entry => entry.Value?.ToString() ?? string.Empty
                                    );

      // Check the Dark Mode setting
      var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";

      ViewBag.IsDarkMode = isDarkMode; // Pass to the view
      return View(envVarDict);
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