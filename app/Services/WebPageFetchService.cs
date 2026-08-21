using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Lebiru.FileService.Models;
using Microsoft.Extensions.Options;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Services;

/// <summary>Fetches one public HTML response and stores it as a normal managed file.</summary>
public interface IWebPageFetchService
{
    /// <summary>Fetches and stores one Web Page for the authenticated owner.</summary>
    Task<WebPageFetchResult> FetchAsync(
        string ownerUserId, string url, Guid? directoryId, CancellationToken cancellationToken);
}

/// <summary>A safe, client-facing Web Page fetch failure.</summary>
public sealed class WebPageFetchException : Exception
{
    /// <summary>Creates a fetch failure with its appropriate HTTP status.</summary>
    public WebPageFetchException(int statusCode, string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>The HTTP status returned by FileService.</summary>
    public int StatusCode { get; }

    /// <summary>A stable machine-readable error code.</summary>
    public string Code { get; }
}

/// <inheritdoc />
public sealed class WebPageFetchService : IWebPageFetchService
{
    private static readonly HashSet<string> HtmlMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "application/xhtml+xml"
    };

    private readonly object _storageSync = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SsrfProtectionService _ssrfProtection;
    private readonly IFileMetadataStore _metadataStore;
    private readonly IUserService _userService;
    private readonly IVirtualDirectoryService _directoryService;
    private readonly TelemetryService _telemetry;
    private readonly WebPageFetchOptions _options;
    private readonly ILogger<WebPageFetchService> _logger;
    private readonly string _uploadsRoot;
    private readonly long _maxStorageBytes;

    /// <summary>Creates the Web Page ingestion service.</summary>
    public WebPageFetchService(
        IHttpClientFactory httpClientFactory,
        SsrfProtectionService ssrfProtection,
        IFileMetadataStore metadataStore,
        IUserService userService,
        IVirtualDirectoryService directoryService,
        TelemetryService telemetry,
        IOptions<WebPageFetchOptions> options,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<WebPageFetchService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _ssrfProtection = ssrfProtection;
        _metadataStore = metadataStore;
        _userService = userService;
        _directoryService = directoryService;
        _telemetry = telemetry;
        _options = options.Value;
        _maxStorageBytes = Math.Max(1, configuration.GetValue<long>("FileService:MaxDiskSpaceGB", 1)) *
            1024L * 1024L * 1024L;
        _logger = logger;
        _uploadsRoot = Path.Combine(environment.ContentRootPath, "uploads");
    }

    /// <inheritdoc />
    public async Task<WebPageFetchResult> FetchAsync(
        string ownerUserId, string url, Guid? directoryId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            throw new WebPageFetchException(StatusCodes.Status503ServiceUnavailable,
                "Web Page fetching is disabled.", "web_page_fetch_disabled");
        if (string.IsNullOrWhiteSpace(ownerUserId))
            throw new WebPageFetchException(StatusCodes.Status401Unauthorized,
                "Authentication is required.", "authentication_required");
        if (directoryId.HasValue && !_directoryService.IsOwnedBy(directoryId.Value, ownerUserId))
            throw new WebPageFetchException(StatusCodes.Status404NotFound,
                "The destination directory was not found.", "directory_not_found");

        var gate = _ownerGates.GetOrAdd(ownerUserId, _ =>
            new SemaphoreSlim(_options.MaxConcurrentFetchesPerUser, _options.MaxConcurrentFetchesPerUser));
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new WebPageFetchException(StatusCodes.Status429TooManyRequests,
                "Too many Web Page fetches are already running.", "fetch_concurrency_exceeded");

        var stopwatch = Stopwatch.StartNew();
        var downloaded = 0L;
        var success = false;
        var ssrfBlocked = false;
        string? tempPath = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var token = timeout.Token;
            Uri current;
            try { current = await _ssrfProtection.ValidateAsync(url, token); }
            catch (SsrfRejectedException exception)
            {
                ssrfBlocked = true;
                throw new WebPageFetchException(StatusCodes.Status400BadRequest,
                    "The destination is blocked by outbound network policy.", "destination_blocked", exception);
            }
            catch (InvalidOperationException exception)
            {
                throw new WebPageFetchException(StatusCodes.Status400BadRequest,
                    "Provide an absolute HTTP or HTTPS URL on a standard port.", "invalid_url", exception);
            }

            var sourceUrl = current.AbsoluteUri;
            var client = _httpClientFactory.CreateClient("WebPageFetch");
            HttpResponseMessage? response = null;
            var redirects = 0;
            _logger.LogInformation("WebPageFetchStarted for {UserId} host {Host}", ownerUserId, current.IdnHost);
            try
            {
                while (true)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    if (!IsRedirect(response.StatusCode)) break;
                    if (response.Headers.Location is null)
                        throw new WebPageFetchException(StatusCodes.Status502BadGateway,
                            "The remote server returned an invalid redirect.", "invalid_redirect");
                    if (redirects >= _options.MaxRedirects)
                        throw new WebPageFetchException(StatusCodes.Status502BadGateway,
                            "The remote server exceeded the redirect limit.", "redirect_limit_exceeded");

                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location : new Uri(current, response.Headers.Location);
                    response.Dispose();
                    response = null;
                    try { current = await _ssrfProtection.ValidateAsync(next.AbsoluteUri, token); }
                    catch (SsrfRejectedException exception)
                    {
                        ssrfBlocked = true;
                        throw new WebPageFetchException(StatusCodes.Status400BadRequest,
                            "A redirect destination is blocked by outbound network policy.",
                            "redirect_destination_blocked", exception);
                    }
                    catch (InvalidOperationException exception)
                    {
                        throw new WebPageFetchException(StatusCodes.Status400BadRequest,
                            "A redirect used an unsupported or invalid URL.", "invalid_redirect_url", exception);
                    }
                    redirects++;
                    _logger.LogInformation("WebPageFetchRedirected for {UserId} to host {Host}; redirect {RedirectCount}",
                        ownerUserId, current.IdnHost, redirects);
                }

                if (!response.IsSuccessStatusCode)
                    throw new WebPageFetchException(StatusCodes.Status502BadGateway,
                        $"The remote server returned HTTP {(int)response.StatusCode}.", "upstream_http_error");

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !HtmlMediaTypes.Contains(mediaType))
                    throw new WebPageFetchException(StatusCodes.Status415UnsupportedMediaType,
                        "The remote response is not HTML or XHTML.", "unsupported_content_type");
                if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                    throw new WebPageFetchException(StatusCodes.Status413PayloadTooLarge,
                        "The remote HTML response exceeds the configured size limit.", "response_too_large");

                Directory.CreateDirectory(_uploadsRoot);
                tempPath = Path.Combine(_uploadsRoot, $".web-fetch-{Guid.NewGuid():N}.tmp");
                await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var input = await response.Content.ReadAsStreamAsync(token))
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, token);
                        if (read == 0) break;
                        downloaded += read;
                        if (downloaded > _options.MaxResponseBytes)
                            throw new WebPageFetchException(StatusCodes.Status413PayloadTooLarge,
                                "The remote HTML response exceeds the configured size limit.", "response_too_large");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    await output.FlushAsync(token);
                }

                var requestedName = DetermineFileName(response.Content.Headers.ContentDisposition, current);
                StoredFileInfo stored;
                lock (_storageSync)
                {
                    if (_metadataStore.UsedSpace + downloaded > _maxStorageBytes)
                        throw new WebPageFetchException(StatusCodes.Status507InsufficientStorage,
                            "The fetched page would exceed configured FileService storage.", "storage_limit_exceeded");
                    var fileName = MakeUniqueFileName(requestedName);
                    var finalPath = FilePathSecurity.ResolveFile(_uploadsRoot, fileName);
                    System.IO.File.Move(tempPath, finalPath);
                    tempPath = null;
                    try
                    {
                        _userService.AddFileToUser(ownerUserId, finalPath);
                        stored = new StoredFileInfo
                        {
                            Id = Guid.NewGuid(), FileName = fileName, FilePath = finalPath,
                            UploadTime = DateTime.UtcNow, FileSize = downloaded, Owner = ownerUserId,
                            DirectoryId = directoryId
                        };
                        var files = _metadataStore.GetAll();
                        files.Add(stored);
                        _metadataStore.Replace(files);
                    }
                    catch
                    {
                        _userService.RemoveFileFromUser(finalPath);
                        try { System.IO.File.Delete(finalPath); } catch (IOException) { }
                        throw;
                    }
                }

                success = true;
                _logger.LogInformation(
                    "WebPageFetchCompleted for {UserId} host {Host} status {StatusCode} bytes {BytesDownloaded} redirects {RedirectCount} file {FileId} duration {DurationMs}",
                    ownerUserId, current.IdnHost, (int)response.StatusCode, downloaded, redirects,
                    stored.Id, stopwatch.Elapsed.TotalMilliseconds);
                return new WebPageFetchResult(stored.Id, stored.FileName, directoryId, sourceUrl,
                    current.AbsoluteUri, (int)response.StatusCode, mediaType, downloaded, stored.UploadTime);
            }
            finally
            {
                response?.Dispose();
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebPageFetchException(StatusCodes.Status504GatewayTimeout,
                "The remote page did not complete within the configured timeout.", "fetch_timeout", exception);
        }
        catch (WebPageFetchException exception)
        {
            _logger.LogWarning("WebPageFetchRejected for {UserId}: {ErrorCode}", ownerUserId, exception.Code);
            throw;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "WebPageFetchFailed for {UserId}", ownerUserId);
            throw new WebPageFetchException(StatusCodes.Status502BadGateway,
                "The remote page could not be retrieved.", "fetch_failed", exception);
        }
        catch (SocketException exception)
        {
            _logger.LogWarning(exception, "WebPageFetchDnsFailed for {UserId}", ownerUserId);
            throw new WebPageFetchException(StatusCodes.Status502BadGateway,
                "The remote page hostname could not be resolved.", "dns_resolution_failed", exception);
        }
        finally
        {
            if (tempPath is not null)
            {
                try { System.IO.File.Delete(tempPath); } catch (IOException) { }
            }
            stopwatch.Stop();
            _telemetry.RecordWebPageFetch(success, downloaded, stopwatch.Elapsed.TotalMilliseconds, ssrfBlocked);
            gate.Release();
        }
    }

    /// <summary>Derives and sanitizes an HTML filename from response metadata and final URL.</summary>
    public static string DetermineFileName(ContentDispositionHeaderValue? contentDisposition, Uri finalUri)
    {
        var candidate = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        candidate = candidate?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Uri.UnescapeDataString(finalUri.Segments.LastOrDefault()?.Trim('/') ?? string.Empty);
        if (string.IsNullOrWhiteSpace(candidate)) candidate = finalUri.IdnHost;
        candidate = Path.GetFileName(candidate.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        candidate = new string(candidate.Where(character => !invalid.Contains(character) && !char.IsControl(character)).ToArray()).Trim();
        if (candidate.Length > 180) candidate = candidate[..180];
        if (string.IsNullOrWhiteSpace(candidate) || candidate is "." or "..") candidate = "page";
        var extension = Path.GetExtension(candidate).ToLowerInvariant();
        if (extension is not (".html" or ".htm" or ".xhtml")) candidate += ".html";
        return candidate;
    }

    private string MakeUniqueFileName(string requestedName)
    {
        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var extension = Path.GetExtension(requestedName);
        var candidate = requestedName;
        for (var suffix = 1; System.IO.File.Exists(FilePathSecurity.ResolveFile(_uploadsRoot, candidate)); suffix++)
            candidate = $"{stem}-{suffix}{extension}";
        return candidate;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
