using System.Diagnostics;
using System.IO;
using Hangfire.Console;
using Hangfire.Server;
using OpenTelemetry.Trace;

public class CleanupJob
{
    private readonly string _fileDirectory;
    private readonly Tracer _tracer;

    public CleanupJob(string fileDirectory, TracerProvider tracerProvider)
    {
        _fileDirectory = fileDirectory;
        _tracer = tracerProvider.GetTracer("Hangfire");
    }

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
                var files = Directory.GetFiles(_fileDirectory);
                if (files.Length == 0)
                {
                    context.WriteLine($"✅ No files to delete in {_fileDirectory}", ConsoleTextColor.Green);
                    span.SetAttribute("NoFilesToDelete", "true");
                    return;
                }

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