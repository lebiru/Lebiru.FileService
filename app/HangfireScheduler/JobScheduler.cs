using Hangfire;

/// <summary>
/// Utility class for scheduling Hangfire background jobs
/// </summary>
public static class JobScheduler
{
    /// <summary>
    /// Enqueues a cleanup job to run in the background using Hangfire
    /// </summary>
    public static void EnqueueCleanupJob()
    {
        // Enqueue the CleanupJob
                BackgroundJob.Enqueue<CleanupJob>(job => job.Execute(null!));
    }
}