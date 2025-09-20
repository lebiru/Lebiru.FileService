using System.Net.Http.Headers;
using System.Text;

namespace Lebiru.FileService.Middleware;

public class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _username;
    private readonly string _password;

    public BasicAuthMiddleware(RequestDelegate next, string username, string password)
    {
        _next = next;
        _username = username;
        _password = password;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLower();
        if (path?.StartsWith("/auth/login") == true || path?.StartsWith("/auth/logout") == true)
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.ContainsKey("Authorization"))
        {
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic";
            return;
        }

        try
        {
            var authHeader = AuthenticationHeaderValue.Parse(context.Request.Headers["Authorization"]);
            var credentialBytes = Convert.FromBase64String(authHeader.Parameter ?? string.Empty);
            var credentials = Encoding.UTF8.GetString(credentialBytes).Split(':', 2);
            var username = credentials[0];
            var password = credentials[1];

            if (username == _username && password == _password)
            {
                await _next(context);
                return;
            }
        }
        catch
        {
            // Invalid auth header format
        }

        context.Response.StatusCode = 401;
        context.Response.Headers["WWW-Authenticate"] = "Basic";
    }
}