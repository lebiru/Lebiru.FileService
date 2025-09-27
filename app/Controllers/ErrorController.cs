using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics;

namespace Lebiru.FileService.Controllers
{
  /// <summary>
  /// Controller for handling various error pages
  /// </summary>
  [Route("Error")]
  public class ErrorController : Controller
  {
    /// <summary>
    /// Handles 404 Not Found errors
    /// </summary>
    /// <returns>A custom 404 page</returns>
    [Route("404")]
    public IActionResult PageNotFound()
    {
      // Set the dark mode property based on the user's preference
      var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
      ViewBag.IsDarkMode = isDarkMode;

      // Get the original path that caused the 404 error
      var originalPath = HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath;
      ViewBag.OriginalPath = originalPath;

      Response.StatusCode = 404; // Make sure we return a 404 status code

      return View();
    }

    /// <summary>
    /// Handles general errors
    /// </summary>
    /// <param name="statusCode">HTTP status code</param>
    /// <returns>Appropriate error view</returns>
    [Route("{statusCode:int}")]
    public IActionResult HandleErrorCode(int statusCode)
    {
      // Set the dark mode property based on the user's preference
      var isDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
      ViewBag.IsDarkMode = isDarkMode;

      // Handle different status codes
      switch (statusCode)
      {
        case 404:
          return RedirectToAction(nameof(PageNotFound));
        default:
          ViewBag.StatusCode = statusCode;
          ViewBag.ErrorMessage = $"Error {statusCode} occurred.";
          Response.StatusCode = statusCode;
          return View("Error");
      }
    }
  }
}