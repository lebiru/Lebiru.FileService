using Microsoft.OpenApi.Models;
using System.Reflection;
using Hangfire;
using Hangfire.MemoryStorage;
using Lebiru.FileService.HangfireJobs;
using Lebiru.FileService;
using Lebiru.FileService.Services;
using Lebiru.FileService.Models;
using Lebiru.FileService.HangfireScheduler;
using Microsoft.AspNetCore.Http.Features;
using Hangfire.Console;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Hangfire.Storage.SQLite;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// In development mode, add user secrets
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

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
builder.Services.AddHostedService(provider => (ApiMetricsService)provider.GetRequiredService<IApiMetricsService>());

// Register user service as singleton
builder.Services.AddSingleton<IUserService, UserService>();

// Register MIME validation service as singleton
builder.Services.AddSingleton<IMimeValidationService, MimeValidationService>();
builder.Services.AddSingleton<IFileMetadataStore, FileMetadataStore>();
builder.Services.AddSingleton<IVirtualDirectoryMetadataStore, VirtualDirectoryMetadataStore>();
builder.Services.AddSingleton<IVirtualDirectoryService, VirtualDirectoryService>();
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 100_000);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFileViewTrackingService, FileViewTrackingService>();
builder.Services.AddOptions<FileViewOptions>()
    .Bind(builder.Configuration.GetSection(FileViewOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
builder.Services.AddSingleton<SsrfProtectionService>();
builder.Services.AddSingleton<IWebPageFetchService, WebPageFetchService>();
builder.Services.AddSingleton<IDestinationStore, DestinationStore>();
builder.Services.AddSingleton<IDeliveryStore, DeliveryStore>();
builder.Services.AddSingleton<IDestinationCredentialProtector, DestinationCredentialProtector>();
builder.Services.AddSingleton<IS3DestinationTransport, AwsS3DestinationTransport>();
builder.Services.AddSingleton<IEmailDestinationTransport, MailKitDestinationTransport>();
builder.Services.AddSingleton<IFtpDestinationTransport, FluentFtpDestinationTransport>();
builder.Services.AddSingleton<IFileDestination, S3FileDestination>();
builder.Services.AddSingleton<IFileDestination, EmailFileDestination>();
builder.Services.AddSingleton<IFileDestination, FtpFileDestination>();
builder.Services.AddSingleton<IDestinationHandlerResolver, DestinationHandlerResolver>();
builder.Services.AddSingleton<IDestinationService, DestinationService>();
builder.Services.AddOptions<DestinationOptions>()
    .Bind(builder.Configuration.GetSection(DestinationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<WebPageFetchOptions>()
    .Bind(builder.Configuration.GetSection(WebPageFetchOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
var keyDirectory = Path.Combine(builder.Environment.ContentRootPath, "app-data", "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("Lebiru.FileService")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));

var configuredOtlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
var otlpEndpoint = string.IsNullOrWhiteSpace(configuredOtlpEndpoint)
    ? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    : configuredOtlpEndpoint;
var useConsoleExporter = builder.Configuration.GetValue("OpenTelemetry:UseConsoleExporter", true);
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ??
                  builder.Configuration["OpenTelemetry:ServiceName"] ??
                  "Lebiru.FileService";
var hasOtlpEndpoint = Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var parsedOtlpEndpoint);
if (hasOtlpEndpoint)
{
    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;
        logging.AddOtlpExporter(options => options.Endpoint = parsedOtlpEndpoint!);
    });
}

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: serviceName,
        serviceVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString()))
    .WithTracing(tracing =>
    {
        tracing.SetSampler(new AlwaysOnSampler())
            .AddAspNetCoreInstrumentation(options => options.RecordException = true)
            .AddHttpClientInstrumentation();
        if (hasOtlpEndpoint)
            tracing.AddOtlpExporter(options => options.Endpoint = parsedOtlpEndpoint!);
        else if (useConsoleExporter)
            tracing.AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(TelemetryService.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation();
        if (hasOtlpEndpoint)
            metrics.AddOtlpExporter(options => options.Endpoint = parsedOtlpEndpoint!);
        else if (useConsoleExporter)
            metrics.AddConsoleExporter();
    });

// Register HttpClient Factory for OAuth operations
builder.Services.AddHttpClient("GoogleOAuth");
builder.Services.AddHttpClient("GmailApi", client =>
{
    client.BaseAddress = new Uri("https://www.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).SetHandlerLifetime(TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("ExternalFetch", client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .SetHandlerLifetime(TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("WebPageFetch", client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Felix.FileService/1.0 WebPageFetcher");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/xhtml+xml;q=0.9");
}).ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    UseProxy = false,
    ConnectCallback = serviceProvider.GetRequiredService<SsrfProtectionService>().ConnectPublicAsync
}).SetHandlerLifetime(TimeSpan.FromMinutes(5));

var hangfirePath = Path.Combine(Directory.GetCurrentDirectory(), "app-data", "hangfire.db");
Directory.CreateDirectory(Path.GetDirectoryName(hangfirePath)!);
builder.Services.AddHangfire(config => config
    .UseSQLiteStorage(hangfirePath)
    .UseConsole());
builder.Services.AddHangfireServer();

// Register the cleanup jobs
builder.Services.AddTransient(provider =>
    new CleanupJob(
        "./uploads/",
        provider.GetRequiredService<IUserService>()));
builder.Services.AddTransient(provider =>
    new ExpiryJob("./uploads/", provider.GetRequiredService<IFileMetadataStore>()));


builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Felix File Service API",
        Version = "v1",
        Description = "API for managing files and user-owned virtual directory hierarchies. A null DirectoryId represents root.",
        Contact = new OpenApiContact
        {
            Name = "Felix File Service",
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


builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
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



builder.Services.AddRateLimiter(options =>
{
    var webPageFetch = builder.Configuration.GetSection(WebPageFetchOptions.SectionName)
        .Get<WebPageFetchOptions>() ?? new WebPageFetchOptions();
    var destinations = builder.Configuration.GetSection(DestinationOptions.SectionName)
        .Get<DestinationOptions>() ?? new DestinationOptions();
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5),
            QueueLimit = 0
        }));
    options.AddPolicy("web-page-fetch", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = webPageFetch.RequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("destinations", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = destinations.RequestsPerMinute, Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0, AutoReplenishment = true
        }));
});

// Load version configuration
var versionConfig = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())  // Use current directory instead of base directory
    .AddJsonFile("appsettings.version.json", optional: true, reloadOnChange: true)
    .Build();

// Read version from configuration
var version = versionConfig["Version"] ?? "Unknown";
var gitCommit = versionConfig["GitCommit"] ?? "unknown";

Console.WriteLine($"Application Version: {version}, Git Commit: {gitCommit}");

var app = builder.Build();

// Make version information available globally via middleware or ViewBag.
app.Use(async (context, next) =>
{
    context.Items["Version"] = version;
    context.Items["GitCommit"] = gitCommit;
    await next();
});

app.Use(async (context, next) =>
{
    var telemetry = context.RequestServices.GetRequiredService<TelemetryService>();
    var stopwatch = Stopwatch.StartNew();
    telemetry.RequestStarted();
    try
    {
        await next();
    }
    finally
    {
        stopwatch.Stop();
        telemetry.RequestCompleted(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Response.StatusCode,
            stopwatch.Elapsed.TotalMilliseconds);
    }
});

// Execute next middleware
app.Use(async (context, next) =>
{
    await next();
});

app.UseRouting();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Keep the API reference available in every environment, but only to authenticated users.
// Swagger handles its own responses, so its framing and security headers are applied here.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/swagger"), swaggerApp =>
{
    swaggerApp.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers.XFrameOptions = "SAMEORIGIN";
        await next();
    });
    swaggerApp.UseSwagger();
    swaggerApp.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Felix File Service v1");
        options.DocumentTitle = "Felix File Service API Documentation";
        options.InjectStylesheet("/swagger-ui/custom.css");
        options.DefaultModelExpandDepth(2);
        options.DefaultModelsExpandDepth(-1);
        options.DefaultModelRendering(Swashbuckle.AspNetCore.SwaggerUI.ModelRendering.Model);
        options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        options.EnableDeepLinking();
        options.DisplayRequestDuration();
    });
});

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    DashboardTitle = "Felix File Service - Background Jobs",
    AppPath = "/File/Home",    // Redirects "Back to Site" link
    Authorization = new[] { new HangfireAuthorizationFilter() }
});
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.XFrameOptions = context.Request.Path.StartsWithSegments("/swagger")
        ? "SAMEORIGIN"
        : "DENY";
    await next();
});

app.UseStaticFiles();
app.UseSession();

// Configure custom error pages
app.UseStatusCodePagesWithReExecute("/Error/{0}");
app.UseExceptionHandler("/Error/500"); // Handle unhandled exceptions

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
// Map health checks to the controller instead of the default endpoint
app.MapControllerRoute(
    name: "healthcheck",
    pattern: "healthz",
    defaults: new { controller = "HealthCheck", action = "Index" });

// Add conventional routing with a catch-all route at the end
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=File}/{action=Home}/{id?}");

// Map a catch-all route for 404s - must be the last route
app.MapFallback(context =>
{
    context.Response.Redirect("/Error/404");
    return Task.CompletedTask;
});

app.Run();
