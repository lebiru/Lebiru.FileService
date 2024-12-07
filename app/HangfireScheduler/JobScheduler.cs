using Hangfire;

public static class JobScheduler
{
    public static void EnqueueCleanupJob()
    {
        // Enqueue the CleanupJob
        BackgroundJob.Enqueue<CleanupJob>(job => job.Execute(null));
    }
}