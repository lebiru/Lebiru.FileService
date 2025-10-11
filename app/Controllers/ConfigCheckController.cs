using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lebiru.FileService.Controllers
{
  /// <summary>
  /// Controller for checking configuration settings
  /// </summary>
  [Route("[controller]")]
  [Authorize(Roles = "Admin")]
  public class ConfigCheckController : Controller
  {
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigCheckController> _logger;

    /// <summary>
    /// Initializes a new instance of the ConfigCheckController
    /// </summary>
    /// <param name="configuration">Application configuration</param>
    /// <param name="logger">Logger</param>
    public ConfigCheckController(
        IConfiguration configuration,
        ILogger<ConfigCheckController> logger)
    {
      _configuration = configuration;
      _logger = logger;
    }

    /// <summary>
    /// Check if user secrets are properly loaded
    /// </summary>
    /// <returns>Status of configuration</returns>
    [HttpGet("CheckSecrets")]
    public IActionResult CheckSecrets()
    {
      try
      {
        var clientId = _configuration["Authentication:Google:ClientId"];
        var clientSecret = _configuration["Authentication:Google:ClientSecret"];

        bool hasClientId = !string.IsNullOrEmpty(clientId) &&
                           clientId != "SET_IN_USER_SECRETS_OR_ENV_VARS";

        bool hasClientSecret = !string.IsNullOrEmpty(clientSecret) &&
                              clientSecret != "SET_IN_USER_SECRETS_OR_ENV_VARS";

        return Json(new
        {
          UserSecretsConfigured = true,
          GoogleOAuthConfigured = hasClientId && hasClientSecret,
          ClientIdConfigured = hasClientId,
          ClientSecretConfigured = hasClientSecret,
          // Don't expose actual secrets in response
          ClientIdMasked = hasClientId ? MaskString(clientId!) : string.Empty,
          ClientSecretMasked = hasClientSecret ? MaskString(clientSecret!) : string.Empty
        });
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error checking configuration");
        return StatusCode(500, "Error checking configuration");
      }
    }

    private string MaskString(string input)
    {
      if (string.IsNullOrEmpty(input))
        return string.Empty;

      if (input.Length <= 8)
        return "****";

      return input.Substring(0, 4) + "..." + input.Substring(input.Length - 4);
    }
  }
}