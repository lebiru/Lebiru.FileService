using Microsoft.OpenApi.Models;
using System.Reflection;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Console;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddHangfire(config => config
    .UseMemoryStorage()
    .UseConsole());
builder.Services.AddHangfireServer();

// Generate admin password at startup
var adminPassword = AuthController.GetOrGeneratePassword();
var adminUsername = AuthController.GetUsername();

// Register the CleanupJob with the target directory
builder.Services.AddTransient(provider => 
    new CleanupJob("./uploads/", provider.GetRequiredService<TracerProvider>()));


builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Lebiru.FileService API", Version = "v1" });
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
});


builder.Services.AddControllersWithViews(); // Add MVC services
builder.Services.AddRazorPages(); // Add Razor Pages services
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHealthChecks();

// Add session services
builder.Services.AddDistributedMemoryCache(); // Required for session state
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30); // Set session timeout
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true; // Required for GDPR compliance
});

var jaegerEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") ?? "http://localhost:4317";

// Add OpenTelemetry
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Lebiru.FileService"))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("Hangfire")
            .AddOtlpExporter(otlpOptions =>
                {
                    otlpOptions.Endpoint = new Uri(jaegerEndpoint);
                });
    });

// Load version configuration
var versionConfig = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.version.json", optional: true, reloadOnChange: true)
    .Build();

var version = versionConfig["Version"] ?? "Unknown";
var gitHeight = versionConfig["GitHeight"] ?? "0";

Console.WriteLine($"Application Version: {version}, Git Height: {gitHeight}");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Make version information available globally via middleware or ViewBag.
app.Use(async (context, next) =>
{
    context.Items["Version"] = version;
    context.Items["GitHeight"] = gitHeight;
    await next();
});

// Add OpenTelemetry Middleware for Hangfire
app.Use(async (context, next) =>
{
    var tracer = app.Services.GetRequiredService<TracerProvider>().GetTracer("Hangfire");
    using (var span = tracer.StartActiveSpan("Hangfire Job Execution"))
    {
        await next();
    }
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Use Basic Authentication middleware
app.UseMiddleware<BasicAuthMiddleware>(adminUsername, adminPassword);

app.UseHangfireDashboard("/hangfire");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSession();

app.MapControllers();
app.MapHealthChecks("/healthz");

app.Run();
