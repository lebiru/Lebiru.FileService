using Hangfire;
using Hangfire.Console;
using Hangfire.Server;
using OpenTelemetry.Trace;

namespace Lebiru.FileService.HangfireJobs
{
    /// <summary>
    /// Job that handles deleting expired files
    /// </summary>
    public class ExpiryJob
    {
    private readonly string _uploadsDirectory;
    private readonly string _dataDirectory;
        private readonly TracerProvider _tracerProvider;

        /// <summary>
        /// Creates a new instance of the ExpiryJob
        /// </summary>
        /// <param name="uploadsDirectory">Directory where files are stored</param>
        /// <param name="tracerProvider">Tracer for telemetry</param>
        public ExpiryJob(string uploadsDirectory, TracerProvider tracerProvider)
        {
            _uploadsDirectory = uploadsDirectory;
            _tracerProvider = tracerProvider;
            _dataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "app-data");
            if (!Directory.Exists(_dataDirectory))
            {
                Directory.CreateDirectory(_dataDirectory);
            }
        }

        /// <summary>
        /// Delete files that have passed their expiry time
        /// </summary>
        public void DeleteExpiredFiles(PerformContext? context = null)
        {
            context?.WriteLine("Starting expired files cleanup job...");
            
            var tracer = _tracerProvider.GetTracer("ExpiryJob");
            using var span = tracer.StartActiveSpan("DeleteExpiredFiles");

            var fileInfoPath = Path.Combine(_dataDirectory, "fileInfo.json");
            if (!File.Exists(fileInfoPath))
            {
                return;
            }

            try
            {
                var fileInfoJson = File.ReadAllText(fileInfoPath);
                var files = System.Text.Json.JsonSerializer.Deserialize<List<Models.FileInfo>>(fileInfoJson) ?? new();
                var now = DateTime.UtcNow;
                var expiredFiles = files.Where(f => f.ExpiryTime.HasValue && f.ExpiryTime.Value <= now).ToList();
                
                if (!expiredFiles.Any())
                {
                    context?.WriteLine("No expired files found.");
                    return;
                }

                var successCount = 0;
                var failureCount = 0;

                foreach (var file in expiredFiles)
                {
                    var filePath = Path.Combine(_uploadsDirectory, file.FileName);
                    try 
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            span.AddEvent($"Deleted expired file: {file.FileName}");
                            context?.WriteLine($"Successfully deleted expired file: {file.FileName} (Expired at: {file.ExpiryTime:yyyy-MM-dd HH:mm:ss UTC})");
                            successCount++;
                        }
                        else
                        {
                            context?.WriteLine($"File not found on disk: {file.FileName}", ConsoleTextColor.Yellow);
                            failureCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        context?.WriteLine($"Failed to delete file {file.FileName}: {ex.Message}", ConsoleTextColor.Red);
                        failureCount++;
                        span.RecordException(ex);
                    }
                }

                // Update the fileInfo.json to remove expired files
                files.RemoveAll(f => expiredFiles.Contains(f));
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(files);
                File.WriteAllText(fileInfoPath, updatedJson);

                context?.WriteLine($"Cleanup job completed. Successfully deleted {successCount} files, {failureCount} failures.");
            }
            catch (Exception ex)
            {
                context?.WriteLine($"Error during cleanup job: {ex.Message}", ConsoleTextColor.Red);
                span.RecordException(ex);
                throw;
            }
        }
    }
}