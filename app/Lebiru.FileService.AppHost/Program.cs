var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Lebiru_FileService>("felix-fileservice", launchProfileName: null)
    .WithHttpEndpoint(port: 3002, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("OpenTelemetry__UseConsoleExporter", "false")
    .WithEnvironment("OTEL_TRACES_SAMPLER", "always_on")
    .WithEnvironment("OTEL_BSP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("OTEL_BLRP_SCHEDULE_DELAY", "1000")
    .WithEnvironment("OTEL_METRIC_EXPORT_INTERVAL", "5000");

builder.Build().Run();
