using System.Diagnostics;
using System.IO;
using Hangfire.Console;
using Hangfire.Server;
using Hangfire;

using Lebiru.FileService.Models;
using Lebiru.FileService.Services;

/// <summary>
/// Job for cleaning up uploaded files based on configured rules
/// </summary>
public class CleanupJob
{
    private readonly string _fileDirectory;
    private readonly string _dataDirectory;
    private readonly IUserService? _userService;

    /// <summary>
    /// Initializes a new instance of the CleanupJob class
    /// </summary>
    /// <param name="fileDirectory">The directory containing files to clean up</param>
    /// <param name="userService">The user service for managing file ownership</param>
    public CleanupJob(string fileDirectory, IUserService userService)
    {
        _fileDirectory = fileDirectory;
        _dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
        _userService = userService;
    }

    /// <summary>Compatibility constructor used by legacy callers that only enqueue cleanup.</summary>
    /// <param name="backgroundJobClient">Background job client.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="logger">Cleanup logger.</param>
    public CleanupJob(IBackgroundJobClient backgroundJobClient, IConfiguration configuration, ILogger<CleanupJob> logger)
    {
        _fileDirectory = "./uploads/";
        _dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
    }

    /// <summary>
    /// Executes the cleanup job, removing files based on configured rules
    /// </summary>
    /// <param name="context">The Hangfire context providing job execution information</param>
    public void Execute(PerformContext context)
    {
        // Start job execution
        var stopwatch = Stopwatch.StartNew();

        context.WriteLine($"🧹 Starting cleanup in {_fileDirectory}", ConsoleTextColor.Yellow);

        if (!Directory.Exists(_fileDirectory))
        {
            context.WriteLine($"❌ Directory not found: {_fileDirectory}", ConsoleTextColor.Red);

            return;
        }

        try
        {
            // First, clear the fileInfo.json
            var fileInfoPath = Path.Combine(_dataDirectory, "fileInfo.json");
            if (File.Exists(fileInfoPath))
            {
                for (int retries = 0; retries < 3; retries++)
                {
                    try
                    {
                        // Use FileShare.None to ensure exclusive access
                        using (var fs = new FileStream(fileInfoPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var writer = new StreamWriter(fs))
                        {
                            writer.Write("[]");
                        }
                        context.WriteLine("📄 Cleared fileInfo.json", ConsoleTextColor.Green);
                        break;
                    }
                    catch (IOException) when (retries < 2)
                    {
                        // If file is locked, wait a bit and retry
                        context.WriteLine($"⌛ File is locked, retrying... (attempt {retries + 1}/3)", ConsoleTextColor.Yellow);
                    }
                }
            }

            // Clear owned files from all users
            try
            {
                _userService?.ClearOwnedFiles();
                context.WriteLine("👤 Cleared owned files from all users", ConsoleTextColor.Green);
            }
            catch (Exception ex)
            {
                context.WriteLine($"⚠️ Warning: Could not clear user file ownership: {ex.Message}", ConsoleTextColor.Yellow);
            }

            var files = Directory.GetFiles(_fileDirectory);
            context.WriteLine($"📁 Found {files.Length} file(s) to delete.", ConsoleTextColor.White);


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
