using System.Diagnostics;
using System.IO;
using Hangfire.Console;
using Hangfire.Server;
using OpenTelemetry.Trace;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;

/// <summary>
/// Job for cleaning up uploaded files based on configured rules
/// </summary>
public class CleanupJob
{
    private readonly string _fileDirectory;
    private readonly string _dataDirectory;
    private readonly Tracer _tracer;
    private readonly IUserService _userService;

    /// <summary>
    /// Initializes a new instance of the CleanupJob class
    /// </summary>
    /// <param name="fileDirectory">The directory containing files to clean up</param>
    /// <param name="tracerProvider">The OpenTelemetry tracer provider for monitoring</param>
    /// <param name="userService">The user service for managing file ownership</param>
    public CleanupJob(string fileDirectory, TracerProvider tracerProvider, IUserService userService)
    {
        _fileDirectory = fileDirectory;
        _dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
        _tracer = tracerProvider.GetTracer("Hangfire");
        _userService = userService;
    }

    /// <summary>
    /// Executes the cleanup job, removing files based on configured rules
    /// </summary>
    /// <param name="context">The Hangfire context providing job execution information</param>
    public void Execute(PerformContext context)
    {
        using (var span = _tracer.StartActiveSpan("CleanupJob.Execute"))
        {
            var stopwatch = Stopwatch.StartNew();

            context.WriteLine($"🧹 Starting cleanup in {_fileDirectory}", ConsoleTextColor.Yellow);

            if (!Directory.Exists(_fileDirectory))
            {
                context.WriteLine($"❌ Directory not found: {_fileDirectory}", ConsoleTextColor.Red);
                span.SetAttribute("DirectoryNotFound", "true");
                return;
            }

            try
            {
                // First, clear the fileInfo.json
                var fileInfoPath = Path.Combine(_dataDirectory, "fileInfo.json");
                if (File.Exists(fileInfoPath))
                {
                    File.WriteAllText(fileInfoPath, "[]");
                    context.WriteLine("📄 Cleared fileInfo.json", ConsoleTextColor.Green);
                }

                // Clear owned files from all users
                try
                {
                    var users = _userService.GetAllUsers();
                    foreach (var user in users)
                    {
                        foreach (var file in user.OwnedFiles.ToList()) // Use ToList to avoid modification during enumeration
                        {
                            _userService.RemoveFileFromUser(file);
                        }
                    }
                    context.WriteLine("👤 Cleared owned files from all users", ConsoleTextColor.Green);
                }
                catch (Exception ex)
                {
                    context.WriteLine($"⚠️ Warning: Could not clear user file ownership: {ex.Message}", ConsoleTextColor.Yellow);
                }

                var files = Directory.GetFiles(_fileDirectory);
                context.WriteLine($"📁 Found {files.Length} file(s) to delete.", ConsoleTextColor.White);
                span.SetAttribute("File Info", $"📁 Found {files.Length} file(s) to delete.");

                for (int i = 0; i < files.Length; i++)
                {
                    var file = files[i];

                    // Delete the file
                    File.Delete(file);

                    // Calculate percentage completion
                    int percentComplete = (int)(((double)(i + 1) / files.Length) * 100);

                    // Log progress
                    context.WriteLine($"✔️ Deleted file: {Path.GetFileName(file)} [{percentComplete}% complete]", ConsoleTextColor.Green);

                    // Update progress bar in Hangfire console
                    context.WriteProgressBar(percentComplete);
                }

                stopwatch.Stop();
                context.WriteLine($"🏁 Cleanup completed in {stopwatch.Elapsed.TotalSeconds:F2} seconds.", ConsoleTextColor.Green);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                context.WriteLine($"❗ An error occurred: {ex.Message}", ConsoleTextColor.Red);
            }
        }
        
    }
}