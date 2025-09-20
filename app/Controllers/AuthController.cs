using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;
using System.Text;
using System;

namespace Lebiru.FileService.Controllers
{
    [Route("Auth")]
    public class AuthController : Controller
    {
        private static string _adminPassword = string.Empty;
        private static bool _passwordGenerated = false;
        private static readonly string _adminUsername = "admin";

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

        public static string GetUsername() => _adminUsername;

        [HttpGet("Login")]
        public IActionResult Login(string? error = null)
        {
            ViewBag.Error = error;
            return View();
        }

        [HttpPost("Login")]
        public IActionResult LoginPost()
        {
            var authHeader = Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(authHeader))
            {
                return Unauthorized();
            }

            try
            {
                var credentialBytes = Convert.FromBase64String(authHeader.Replace("Basic ", ""));
                var credentials = Encoding.UTF8.GetString(credentialBytes).Split(':', 2);
                var username = credentials[0];
                var password = credentials[1];

                if (username == _adminUsername && password == _adminPassword)
                {
                    return Redirect("/");
                }
            }
            catch
            {
                // Invalid auth header format
            }

            Response.Headers["WWW-Authenticate"] = "Basic";
            return Unauthorized();
        }

        [HttpGet("Logout")]
        public IActionResult Logout()
        {
            Response.Headers["WWW-Authenticate"] = "Basic";
            return Unauthorized();
        }
    }
}