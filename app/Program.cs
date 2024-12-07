using Microsoft.OpenApi.Models;
using System.Reflection;
using Hangfire;
using Hangfire.MemoryStorage;
using Hangfire.Console;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddHangfire(config => config
    .UseMemoryStorage()
    .UseConsole());
builder.Services.AddHangfireServer();

// Register the CleanupJob with the target directory
builder.Services.AddTransient(provider => 
    new CleanupJob("./uploads/"));


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

app.UseRouting();
app.UseHangfireDashboard("/hangfire");
app.UseHttpsRedirection();
app.UseAuthorization();
app.UseStaticFiles();
app.MapControllers();

app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();
});

app.MapHealthChecks("/healthz");

app.Run();
