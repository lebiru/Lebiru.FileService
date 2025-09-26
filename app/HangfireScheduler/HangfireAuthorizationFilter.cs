using Hangfire.Annotations;
using Hangfire.Dashboard;

namespace Lebiru.FileService.HangfireScheduler
{
  /// <summary>
  /// Authorization filter for Hangfire dashboard
  /// </summary>
  public class HangfireAuthorizationFilter : IDashboardAuthorizationFilter
  {
    /// <summary>
    /// Authorizes access to the Hangfire dashboard
    /// </summary>
    /// <param name="context">Dashboard context</param>
    /// <returns>True if user is authenticated</returns>
    public bool Authorize([NotNull] DashboardContext context)
    {
      var httpContext = context.GetHttpContext();

      // Use the same authentication as the rest of the application
      return httpContext.User.Identity?.IsAuthenticated ?? false;
    }
  }
}