using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Lebiru.FileService.Models;
using System.Security.Cryptography;

namespace Lebiru.FileService.Controllers
{
  /// <summary>
  /// Controller for handling Gmail OAuth authentication flow
  /// </summary>
  [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Contributor}")]
  [Route("[controller]")]
  [ApiController]
  public class GmailOAuthController : Controller
  {
    private readonly IConfiguration _configuration;
    private readonly ILogger<GmailOAuthController> _logger;
    private readonly HttpClient _httpClient;

    // Keys for session storage
    private const string ACCESS_TOKEN_KEY = "GmailOAuthAccessToken";
    private const string REFRESH_TOKEN_KEY = "GmailOAuthRefreshToken";
    private const string TOKEN_EXPIRY_KEY = "GmailOAuthTokenExpiry";
    private const string OAUTH_STATE_KEY = "GmailOAuthState";

    /// <summary>
    /// Initializes a new instance of the GmailOAuthController class
    /// </summary>
    /// <param name="configuration">The application configuration</param>
    /// <param name="logger">The logger instance</param>
    /// <param name="httpClientFactory">HTTP client factory for API requests</param>
    public GmailOAuthController(
        IConfiguration configuration,
        ILogger<GmailOAuthController> logger,
        IHttpClientFactory httpClientFactory)
    {
      _configuration = configuration;
      _logger = logger;
      _httpClient = httpClientFactory.CreateClient("GoogleOAuth");
    }

    /// <summary>
    /// Initiates the OAuth flow for Gmail integration
    /// </summary>
    [HttpGet("Authorize")]
    public IActionResult Authorize()
    {
      try
      {
        var clientId = _configuration["Authentication:Google:ClientId"];
        if (string.IsNullOrEmpty(clientId) || clientId == "YOUR_GOOGLE_CLIENT_ID")
        {
          _logger.LogError("Google OAuth client ID not configured or using default placeholder value");
          return View("Error", new { Message = "Google OAuth is not configured correctly on this server. Please configure valid Google OAuth credentials." });
        }

        // Define the OAuth parameters - first try to use the configured redirect URI
        var configuredRedirectUri = _configuration["Authentication:Google:RedirectUri"];
        var redirectUri = string.IsNullOrEmpty(configuredRedirectUri)
            ? Url.Action("Callback", "GmailOAuth", null, Request.Scheme)
            : configuredRedirectUri;

        _logger.LogInformation("Initial Redirect URI: {RedirectUri}", redirectUri);

        // Make sure the redirect URI is absolute and properly formed
        if (string.IsNullOrEmpty(redirectUri) || !Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
        {
          var host = $"{Request.Scheme}://{Request.Host}";
          redirectUri = $"{host}/GmailOAuth/Callback";
          _logger.LogInformation("Adjusted Redirect URI to: {RedirectUri}", redirectUri);

          // Log a warning that we had to adjust the URI
          _logger.LogWarning("Had to adjust the redirect URI because it was missing or invalid. Consider setting Authentication:Google:RedirectUri in appsettings.json.");
        }

        // Ensure we're using HTTP for port 3000 to avoid SSL errors
        if (redirectUri.StartsWith("https://localhost:3000/"))
        {
          var oldUri = redirectUri;
          redirectUri = redirectUri.Replace("https://localhost:3000/", "http://localhost:3000/");
          _logger.LogWarning("Changed redirect URI from HTTPS to HTTP on port 3000 to avoid SSL errors. Old: {OldUri}, New: {NewUri}", oldUri, redirectUri);
        }

        // Verify the redirect URI doesn't have any unexpected characters
        if (redirectUri.Contains(" ") || redirectUri.Contains("\t") || redirectUri.Contains("\n"))
        {
          _logger.LogWarning("Redirect URI contains whitespace characters. Cleaning URI...");
          redirectUri = redirectUri.Trim().Replace("\t", "").Replace("\n", "").Replace("\r", "");
          _logger.LogInformation("Cleaned Redirect URI: {RedirectUri}", redirectUri);
        }

        var scope = "https://www.googleapis.com/auth/gmail.readonly";
        _logger.LogInformation("OAuth Scope: {Scope}", scope);

        // Build the authorization URL carefully
        var authUrlBuilder = new StringBuilder();
        authUrlBuilder.Append("https://accounts.google.com/o/oauth2/auth");

        // Add parameters with proper URL encoding
        authUrlBuilder.Append("?client_id=").Append(Uri.EscapeDataString(clientId));
        authUrlBuilder.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        authUrlBuilder.Append("&response_type=code");
        authUrlBuilder.Append("&scope=").Append(Uri.EscapeDataString(scope));
        authUrlBuilder.Append("&access_type=offline");
        authUrlBuilder.Append("&prompt=consent");
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        HttpContext.Session.SetString(OAUTH_STATE_KEY, state);
        authUrlBuilder.Append("&state=").Append(Uri.EscapeDataString(state));

        var authorizationUrl = authUrlBuilder.ToString();

        if (!Uri.IsWellFormedUriString(authorizationUrl, UriKind.Absolute))
        {
          _logger.LogError("Generated authorization URL is not a valid absolute URI: {URL}", authorizationUrl);
          return View("Error", new { Message = "Failed to generate a valid authorization URL. Please try again." });
        }

        return Redirect(authorizationUrl);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error initiating Gmail OAuth flow");
        return View("Error", new { Message = "Failed to initiate OAuth flow. Please try again later." });
      }
    }

    /// <summary>
    /// Handles the OAuth callback from Google
    /// </summary>
    [HttpGet("Callback")]
    public async Task<IActionResult> Callback(string code, string? state = null, string? error = null)
    {
      var expectedState = HttpContext.Session.GetString(OAUTH_STATE_KEY);
      HttpContext.Session.Remove(OAUTH_STATE_KEY);
      if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(expectedState) ||
          !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
      {
        _logger.LogWarning("Rejected Gmail OAuth callback with invalid state");
        return BadRequest("Invalid OAuth state.");
      }

      if (!string.IsNullOrEmpty(error))
      {
        _logger.LogWarning("OAuth error: {Error}", error);
        ViewBag.Error = "Authentication was denied or canceled.";
        return View("OAuthResult", false);
      }

      if (string.IsNullOrEmpty(code))
      {
        _logger.LogWarning("OAuth callback received without code");
        ViewBag.Error = "No authorization code received.";
        return View("OAuthResult", false);
      }

      try
      {
        // Get OAuth configuration
        var clientId = _configuration["Authentication:Google:ClientId"] ?? "";
        var clientSecret = _configuration["Authentication:Google:ClientSecret"] ?? "";

        // First try to use the configured redirect URI, fallback to generated URL if not available
        var configuredRedirectUri = _configuration["Authentication:Google:RedirectUri"];
        var redirectUri = string.IsNullOrEmpty(configuredRedirectUri)
            ? Url.Action("Callback", "GmailOAuth", null, Request.Scheme) ?? ""
            : configuredRedirectUri;

        // Ensure we're using HTTP for port 3000 to avoid SSL errors
        if (redirectUri.StartsWith("https://localhost:3000/"))
        {
          var oldUri = redirectUri;
          redirectUri = redirectUri.Replace("https://localhost:3000/", "http://localhost:3000/");
          _logger.LogWarning("Changed redirect URI from HTTPS to HTTP on port 3000 to avoid SSL errors. Old: {OldUri}, New: {NewUri}", oldUri, redirectUri);
        }

        _logger.LogInformation("Using callback redirect URI: {RedirectUri}", redirectUri);
        _logger.LogInformation("Callback comparison - From config: {ConfigURI}, Generated: {GeneratedURI}",
            configuredRedirectUri ?? "Not set",
            Url.Action("Callback", "GmailOAuth", null, Request.Scheme) ?? "Not generated");

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
          _logger.LogError("Missing OAuth configuration");
          ViewBag.Error = "OAuth configuration is incomplete on the server.";
          return View("OAuthResult", false);
        }

        // Exchange the authorization code for tokens
        var tokenRequestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
          ["code"] = code,
          ["client_id"] = clientId,
          ["client_secret"] = clientSecret,
          ["redirect_uri"] = redirectUri,
          ["grant_type"] = "authorization_code"
        });

        var response = await _httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            tokenRequestContent);

        if (!response.IsSuccessStatusCode)
        {
          var errorContent = await response.Content.ReadAsStringAsync();
          _logger.LogError("Failed to exchange code for tokens. Status: {Status}, Error: {Error}",
              response.StatusCode, errorContent);
          ViewBag.Error = "Failed to complete authentication.";
          return View("OAuthResult", false);
        }

        var tokenResponse = await response.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenResponse);

        if (tokenData.TryGetProperty("access_token", out var accessTokenElement))
        {
          string accessToken = accessTokenElement.GetString() ?? "";
          // Store tokens in session
          if (!string.IsNullOrEmpty(accessToken))
          {
            HttpContext.Session.SetString(ACCESS_TOKEN_KEY, accessToken);
          }
          else
          {
            _logger.LogError("Empty access token received");
            ViewBag.Error = "Invalid token response from authentication server.";
            return View("OAuthResult", false);
          }

          // The refresh token is only provided on first authorization
          if (tokenData.TryGetProperty("refresh_token", out var refreshToken))
          {
            string refreshTokenStr = refreshToken.GetString() ?? "";
            if (!string.IsNullOrEmpty(refreshTokenStr))
            {
              HttpContext.Session.SetString(REFRESH_TOKEN_KEY, refreshTokenStr);
            }
          }

          // Calculate and store expiry time
          if (tokenData.TryGetProperty("expires_in", out var expiresInElement))
          {
            var expiresIn = expiresInElement.GetInt32();
            var expiryTime = DateTime.UtcNow.AddSeconds(expiresIn);
            HttpContext.Session.SetString(TOKEN_EXPIRY_KEY, expiryTime.ToString("O"));
          }
        }
        else
        {
          _logger.LogError("Access token not found in response");
          ViewBag.Error = "Invalid token response from authentication server.";
          return View("OAuthResult", false);
        }

        _logger.LogInformation("Gmail OAuth authentication successful");
        ViewBag.Success = true;
        return View("OAuthResult", true);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error processing OAuth callback");
        ViewBag.Error = "An error occurred during authentication.";
        return View("OAuthResult", false);
      }
    }

    /// <summary>
    /// Returns the OAuth token status for the current session
    /// </summary>
    [HttpGet("Status")]
    public IActionResult GetOAuthStatus()
    {
      // Check if we have valid tokens
      var hasAccessToken = HttpContext.Session.TryGetValue(ACCESS_TOKEN_KEY, out _);
      var hasRefreshToken = HttpContext.Session.TryGetValue(REFRESH_TOKEN_KEY, out _);

      if (hasAccessToken && hasRefreshToken)
      {
        return Json(new { success = true });
      }

      return Json(new { success = false });
    }

    /// <summary>
    /// Refreshes an expired access token using the refresh token
    /// </summary>
    [HttpPost("RefreshToken")]
    public async Task<IActionResult> RefreshToken()
    {
      // Check if we have a refresh token
      if (!HttpContext.Session.TryGetValue(REFRESH_TOKEN_KEY, out var refreshTokenBytes))
      {
        return Json(new { success = false, message = "No refresh token available" });
      }

      var refreshToken = Encoding.UTF8.GetString(refreshTokenBytes);

      try
      {
        // Get OAuth configuration
        var clientId = _configuration["Authentication:Google:ClientId"] ?? "";
        var clientSecret = _configuration["Authentication:Google:ClientSecret"] ?? "";

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
          _logger.LogError("Missing OAuth configuration for token refresh");
          return Json(new { success = false, message = "OAuth configuration is incomplete on the server." });
        }

        // Request new access token
        var tokenRequestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
          ["client_id"] = clientId,
          ["client_secret"] = clientSecret,
          ["refresh_token"] = refreshToken,
          ["grant_type"] = "refresh_token"
        });

        var response = await _httpClient.PostAsync(
            "https://oauth2.googleapis.com/token",
            tokenRequestContent);

        if (!response.IsSuccessStatusCode)
        {
          var errorContent = await response.Content.ReadAsStringAsync();
          _logger.LogError("Failed to refresh token. Status: {Status}, Error: {Error}",
              response.StatusCode, errorContent);
          return Json(new { success = false, message = "Failed to refresh token" });
        }

        var tokenResponse = await response.Content.ReadAsStringAsync();
        var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenResponse);

        // Store new access token in session
        if (tokenData.TryGetProperty("access_token", out var accessTokenElement))
        {
          var newAccessToken = accessTokenElement.GetString() ?? "";

          if (string.IsNullOrEmpty(newAccessToken))
          {
            _logger.LogError("Empty access token received during refresh");
            return Json(new { success = false, message = "Invalid token response from server" });
          }

          HttpContext.Session.SetString(ACCESS_TOKEN_KEY, newAccessToken);

          // Calculate and store new expiry time
          if (tokenData.TryGetProperty("expires_in", out var expiresInElement))
          {
            var expiresIn = expiresInElement.GetInt32();
            var expiryTime = DateTime.UtcNow.AddSeconds(expiresIn);
            HttpContext.Session.SetString(TOKEN_EXPIRY_KEY, expiryTime.ToString("O"));
          }

          return Json(new { success = true });
        }

        return Json(new { success = false, message = "Access token not found in response" });
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error refreshing OAuth token");
        return Json(new { success = false, message = "Error refreshing token." });
      }
    }
  }
}
