using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lebiru.FileService
{
    /// <summary>
    /// Extension methods for configuring system health checks
    /// </summary>
    public static class HealthCheckExtensions
    {
        /// <summary>
        /// Adds system health checks for memory usage, disk space, and uploads directory
        /// </summary>
        /// <param name="builder">The health checks builder</param>
        /// <returns>The health checks builder for chaining</returns>
        public static IHealthChecksBuilder AddSystemHealthChecks(this IHealthChecksBuilder builder)
        {
            return builder
                .AddCheck("memory", () =>
                {
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    var memoryMB = process.WorkingSet64 / 1024 / 1024;
                    return memoryMB < 1024 
                        ? HealthCheckResult.Healthy($"Memory usage: {memoryMB}MB")
                        : HealthCheckResult.Degraded($"High memory usage: {memoryMB}MB");
                })
                .AddCheck("disk", () =>
                {
                    var drive = new System.IO.DriveInfo("C");
                    var freeSpaceMB = drive.AvailableFreeSpace / 1024 / 1024;
                    return freeSpaceMB > 100 
                        ? HealthCheckResult.Healthy($"Free space: {freeSpaceMB}MB")
                        : HealthCheckResult.Degraded($"Low disk space: {freeSpaceMB}MB");
                })
                .AddCheck("uploads", () =>
                {
                    var uploadsPath = "./uploads";
                    if (!System.IO.Directory.Exists(uploadsPath))
                    {
                        return HealthCheckResult.Unhealthy("Uploads directory does not exist");
                    }

                    try
                    {
                        // Try to write a test file
                        var testFile = System.IO.Path.Combine(uploadsPath, ".test");
                        System.IO.File.WriteAllText(testFile, "test");
                        System.IO.File.Delete(testFile);
                        return HealthCheckResult.Healthy("Uploads directory is writable");
                    }
                    catch (Exception ex)
                    {
                        return HealthCheckResult.Unhealthy("Cannot write to uploads directory", ex);
                    }
                });
        }
    }
}