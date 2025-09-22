using Microsoft.OpenApi.Models;
using System.Reflection;
using Hangfire;
using Hangfire.MemoryStorage;
using Lebiru.FileService.HangfireJobs;
using Lebiru.FileService;
using Lebiru.FileService.Services;
using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Http.Features;
using Hangfire.Console;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using Lebiru.FileService.Controllers;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Configure file size limits
var config = builder.Configuration.GetSection("FileService").Get<FileServiceConfig>();
var maxFileSizeBytes = 1024L * 1024L * (config?.MaxFileSizeMB ?? 100); // Convert MB to bytes

// Configure request size limits in Kestrel
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxFileSizeBytes; // Set Kestrel limit
});

// Configure form options
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxFileSizeBytes; // For multipart forms
    options.ValueLengthLimit = int.MaxValue; // For form values
    options.MultipartHeadersLengthLimit = int.MaxValue; // For multipart headers
});

// Configure IIS options
builder.Services.Configure<IISServerOptions>(options =>
{
    options.MaxRequestBodySize = maxFileSizeBytes; // Set IIS limit
});

// Register API metrics service as a singleton
builder.Services.AddSingleton<IApiMetricsService, ApiMetricsService>();

// Register user service as singleton
builder.Services.AddSingleton<IUserService, UserService>();

builder.Services.AddHangfire(config => config
    .UseMemoryStorage()
    .UseConsole());
builder.Services.AddHangfireServer();

// Register the cleanup jobs
builder.Services.AddTransient(provider => 
    new CleanupJob("./uploads/", provider.GetRequiredService<TracerProvider>()));
builder.Services.AddTransient(provider => 
    new ExpiryJob("./uploads/", provider.GetRequiredService<TracerProvider>()));


builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo 
    { 
        Title = "Lebiru.FileService API",
        Version = "v1",
        Description = "API for managing and serving files",
        Contact = new OpenApiContact
        {
            Name = "Lebiru",
            Url = new Uri("https://github.com/lebiru")
        }
    });
    
    // Add XML comments
    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    c.IncludeXmlComments(xmlPath);
    
    // Add security definition
    c.AddSecurityDefinition("CookieAuth", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Cookie,
        Name = ".AspNetCore.Cookies",
        Description = "Cookie-based authentication"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference 
                { 
                    Type = ReferenceType.SecurityScheme, 
                    Id = "CookieAuth" 
                }
            },
            Array.Empty<string>()
        }
    });
});


builder.Services.AddControllersWithViews(); // Add MVC services
builder.Services.AddRazorPages(); // Add Razor Pages services
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHealthChecks()
    .AddSystemHealthChecks();

// Configure cookie authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.Cookie.Name = "Lebiru.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
    });

// Add session services
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
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
var gitCommit = versionConfig["GitCommit"] ?? "unknown";

Console.WriteLine($"Application Version: {version}, Git Commit: {gitCommit}");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Lebiru.FileService v1");
        c.DocumentTitle = "Lebiru.FileService API Documentation";
        c.InjectStylesheet("/swagger-ui/custom.css");
        c.DefaultModelExpandDepth(2);
        c.DefaultModelsExpandDepth(-1);
        c.DefaultModelRendering(Swashbuckle.AspNetCore.SwaggerUI.ModelRendering.Model);
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        c.EnableDeepLinking();
        c.DisplayRequestDuration();
    });
}

// Make version information available globally via middleware or ViewBag.
app.Use(async (context, next) =>
{
    context.Items["Version"] = version;
    context.Items["GitCommit"] = gitCommit;
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

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Lebiru.FileService - Background Jobs",
    AppPath = "/File/Home"    // Redirects "Back to Site" link
});
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSession();

app.MapControllers();
// Map health checks to the controller instead of the default endpoint
app.MapControllerRoute(
    name: "healthcheck",
    pattern: "healthz",
    defaults: new { controller = "HealthCheck", action = "Index" });

app.Run();
