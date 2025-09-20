using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Handles authentication-related actions including login and logout
    /// </summary>
    [Route("Auth")]
    public class AuthController : Controller
    {
        private static string _adminPassword = string.Empty;
        private static bool _passwordGenerated = false;
        private static readonly string _adminUsername = "admin";

        /// <summary>
        /// Generates or retrieves the current admin password
        /// </summary>
        public static string GetOrGeneratePassword()
        {
            if (!_passwordGenerated)
            {
                var randomBytes = new byte[16];
                using (var rng = RandomNumberGenerator.Create())
                {
                    rng.GetBytes(randomBytes);
                }
                _adminPassword = Convert.ToBase64String(randomBytes);
                Console.WriteLine($"Admin password: {_adminPassword}");
                _passwordGenerated = true;
            }
            return _adminPassword;
        }

        /// <summary>
        /// Gets the admin username
        /// </summary>
        public static string GetUsername() => _adminUsername;

        /// <summary>
        /// Displays the login page
        /// </summary>
        [HttpGet("Login")]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.IsDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
            return View();
        }

        /// <summary>
        /// Handles the login form submission
        /// </summary>
        [HttpPost("Login")]
        public async Task<IActionResult> LoginPost(string username, string password, string? returnUrl = null)
        {
            if (username != _adminUsername || password != _adminPassword)
            {
                ViewBag.Error = "Invalid username or password";
                ViewBag.IsDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
                return View("Login");
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, "Admin")
            };

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            return Redirect(returnUrl ?? "/File/Home");
        }

        /// <summary>
        /// Handles user logout
        /// </summary>
        [HttpGet("Logout")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }
    }
}