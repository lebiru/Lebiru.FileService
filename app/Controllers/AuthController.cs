using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Lebiru.FileService.Services;

namespace Lebiru.FileService.Controllers
{
    /// <summary>
    /// Handles authentication-related actions including login and logout
    /// </summary>
    [Route("Auth")]
    public class AuthController : Controller
    {
        private readonly IUserService _userService;
        private readonly ILogger<AuthController> _logger;

        /// <summary>
        /// Initializes a new instance of the AuthController
        /// </summary>
        public AuthController(IUserService userService, ILogger<AuthController> logger)
        {
            _userService = userService ?? throw new ArgumentNullException(nameof(userService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

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
            if (!_userService.ValidateUser(username, password))
            {
                ViewBag.Error = "Invalid username or password";
                ViewBag.IsDarkMode = HttpContext.Session.GetString("DarkMode") == "true";
                return View("Login");
            }

            var claimsPrincipal = _userService.CreateClaimsPrincipal(username);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                claimsPrincipal,
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