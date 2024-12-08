using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Mvc;
using System.IO.Compression;
using Hangfire;
using System.Collections.Generic;
using System.Collections;
using Microsoft.AspNetCore.Http;

namespace Lebiru.FileService.Controllers
{
  [Route("Config")]
  [ApiController]
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
                                    .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value?.ToString());

      // Check the Dark Mode setting
      var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";

      ViewBag.IsDarkMode = isDarkMode; // Pass to the view
      return View(envVarDict);
    }

    [HttpPost("ToggleDarkMode")]
    public IActionResult ToggleDarkMode([FromForm] bool enableDarkMode)
    {
      // Store the setting in the session
      HttpContext.Session.SetString("DarkMode", enableDarkMode ? "true" : "false");
      return Ok(new { Success = true, IsDarkMode = enableDarkMode });
    }

  }
}