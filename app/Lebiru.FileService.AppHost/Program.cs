var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Lebiru_FileService>("felix-fileservice", launchProfileName: null)
    .WithHttpEndpoint(port: 3002, name: "http")
    .WithExternalHttpEndpoints()
    .WithEnvironment("OpenTelemetry__UseConsoleExporter", "false");

builder.Build().Run();
