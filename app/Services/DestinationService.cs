#pragma warning disable CS1591
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.S3;
using FluentFTP.Exceptions;
using Lebiru.FileService.Models;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Services;

public interface IDestinationService
{
    IReadOnlyList<DestinationSummary> List(string owner);
    DestinationDetails? Get(string owner, Guid id);
    DestinationDetails Create(string owner, DestinationUpsertRequest request);
    DestinationDetails? Update(string owner, Guid id, DestinationUpsertRequest request);
    bool Delete(string owner, Guid id);
    Task<DestinationTestResult?> TestAsync(string owner, Guid id, CancellationToken cancellationToken);
    Task<DeliveryModel> DeliverAsync(string owner, bool isAdministrator, Guid fileId, Guid destinationId,
        CancellationToken cancellationToken);
    IReadOnlyList<DeliveryModel>? GetDestinationDeliveries(string owner, Guid destinationId);
    IReadOnlyList<DeliveryModel>? GetFileDeliveries(string owner, bool isAdministrator, Guid fileId);
}

public sealed class DestinationService : IDestinationService
{
    private readonly object _destinationSync = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ownerGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDestinationStore _destinations;
    private readonly IDeliveryStore _deliveries;
    private readonly IFileMetadataStore _files;
    private readonly IDestinationCredentialProtector _credentials;
    private readonly IDestinationHandlerResolver _handlers;
    private readonly DestinationOptions _options;
    private readonly TelemetryService _telemetry;
    private readonly TimeProvider _time;
    private readonly ILogger<DestinationService> _logger;

    public DestinationService(IDestinationStore destinations, IDeliveryStore deliveries,
        IFileMetadataStore files, IDestinationCredentialProtector credentials,
        IDestinationHandlerResolver handlers, IOptions<DestinationOptions> options,
        TelemetryService telemetry, TimeProvider time, ILogger<DestinationService> logger)
    {
        _destinations = destinations; _deliveries = deliveries; _files = files;
        _credentials = credentials; _handlers = handlers; _options = options.Value;
        _telemetry = telemetry; _time = time; _logger = logger;
    }

    public IReadOnlyList<DestinationSummary> List(string owner) => _destinations.GetAll()
        .Where(item => IsOwner(item, owner)).OrderBy(item => item.Name).Select(Summary).ToList();

    public DestinationDetails? Get(string owner, Guid id)
    {
        var item = _destinations.GetAll().SingleOrDefault(value => value.Id == id && IsOwner(value, owner));
        return item is null ? null : Details(item);
    }

    public DestinationDetails Create(string owner, DestinationUpsertRequest request)
    {
        EnsureOwner(owner); ValidateName(request.Name);
        _handlers.Resolve(request.Type).Validate(request.Configuration, request.Credentials, true);
        if (!request.Credentials.HasValue) throw new DestinationException("InvalidConfiguration", "Credentials are required.");
        var now = _time.GetUtcNow().UtcDateTime;
        var model = new DestinationModel
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, Name = request.Name.Trim(), Type = request.Type,
            IsEnabled = request.IsEnabled, ConfigurationJson = request.Configuration.GetRawText(),
            ProtectedCredentials = _credentials.Protect(request.Credentials.Value), CreatedAt = now, UpdatedAt = now
        };
        lock (_destinationSync) { var all = _destinations.GetAll().ToList(); all.Add(model); _destinations.Replace(all); }
        _logger.LogInformation("DestinationCreated {DestinationId} type {DestinationType}", model.Id, model.Type);
        return Details(model);
    }

    public DestinationDetails? Update(string owner, Guid id, DestinationUpsertRequest request)
    {
        EnsureOwner(owner); ValidateName(request.Name);
        lock (_destinationSync)
        {
            var all = _destinations.GetAll().ToList();
            var item = all.SingleOrDefault(value => value.Id == id && IsOwner(value, owner));
            if (item is null) return null;
            if (request.Type != item.Type && !request.Credentials.HasValue)
                throw new DestinationException("InvalidConfiguration", "Credentials are required when changing destination type.");
            _handlers.Resolve(request.Type).Validate(request.Configuration, request.Credentials,
                string.IsNullOrEmpty(item.ProtectedCredentials));
            item.Name = request.Name.Trim(); item.Type = request.Type; item.IsEnabled = request.IsEnabled;
            item.ConfigurationJson = request.Configuration.GetRawText(); item.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            if (request.Credentials.HasValue) item.ProtectedCredentials = _credentials.Protect(request.Credentials.Value);
            item.LastTestSucceeded = null; item.LastTestedAt = null;
            _destinations.Replace(all);
            _logger.LogInformation("DestinationUpdated {DestinationId} type {DestinationType}", item.Id, item.Type);
            return Details(item);
        }
    }

    public bool Delete(string owner, Guid id)
    {
        lock (_destinationSync)
        {
            var all = _destinations.GetAll().ToList();
            var removed = all.RemoveAll(item => item.Id == id && IsOwner(item, owner)) > 0;
            if (!removed) return false;
            _destinations.Replace(all);
            _logger.LogInformation("DestinationDeleted {DestinationId}", id);
            return true;
        }
    }

    public async Task<DestinationTestResult?> TestAsync(string owner, Guid id, CancellationToken cancellationToken)
    {
        var destination = Owned(owner, id);
        if (destination is null) return null;
        var success = false;
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(_options.TimeoutSeconds, 30)));
            var config = Parse(destination.ConfigurationJson);
            var credentials = Unprotect(destination);
            var handler = _handlers.Resolve(destination.Type);
            handler.Validate(config, credentials, true);
            await handler.TestAsync(config, credentials, timeout.Token);
            success = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { error = "The connection test timed out."; }
        catch (Exception exception) { error = SafeFailure(exception).Message; }
        lock (_destinationSync)
        {
            var all = _destinations.GetAll().ToList();
            var current = all.SingleOrDefault(item => item.Id == id && IsOwner(item, owner));
            if (current is not null)
            {
                current.LastTestedAt = _time.GetUtcNow().UtcDateTime; current.LastTestSucceeded = success;
                _destinations.Replace(all);
            }
        }
        _logger.LogInformation("DestinationTested {DestinationId} type {DestinationType} result {Result}", id, destination.Type, success);
        return new DestinationTestResult(success, error);
    }

    public async Task<DeliveryModel> DeliverAsync(string owner, bool isAdministrator, Guid fileId,
        Guid destinationId, CancellationToken cancellationToken)
    {
        var file = _files.GetAll().SingleOrDefault(item => item.Id == fileId &&
            (isAdministrator || string.Equals(item.Owner, owner, StringComparison.OrdinalIgnoreCase)));
        if (file is null || !File.Exists(file.FilePath))
            throw new DestinationException("FileNotFound", "The file was not found.");
        var destination = Owned(owner, destinationId) ??
            throw new DestinationException("DestinationNotFound", "The destination was not found.");
        if (!destination.IsEnabled) throw new DestinationException("DestinationDisabled", "The destination is disabled.");

        var delivery = new DeliveryModel
        {
            Id = Guid.NewGuid(), OwnerUserId = owner, FileId = file.Id, DestinationId = destination.Id,
            DestinationType = destination.Type, Status = DeliveryStatus.Pending,
            StartedAt = _time.GetUtcNow().UtcDateTime
        };
        _deliveries.Upsert(delivery);
        var gate = _ownerGates.GetOrAdd(owner, _ => new SemaphoreSlim(
            _options.MaxConcurrentDeliveriesPerUser, _options.MaxConcurrentDeliveriesPerUser));
        var stopwatch = Stopwatch.StartNew();
        var enteredGate = false;
        try
        {
            if (!await gate.WaitAsync(0, cancellationToken))
            {
                Fail(delivery, "ConcurrencyLimit", "Too many deliveries are already running.");
                return delivery;
            }
            enteredGate = true;
            delivery.Status = DeliveryStatus.InProgress; _deliveries.Upsert(delivery);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var handler = _handlers.Resolve(destination.Type);
            var config = Parse(destination.ConfigurationJson); var secrets = Unprotect(destination);
            handler.Validate(config, secrets, true);
            var descriptor = new DestinationFile(file.Id, file.FileName, file.FileSize, MimeFor(file.FileName));
            for (var attempt = 0; ; attempt++)
            {
                delivery.AttemptCount = attempt + 1; _deliveries.Upsert(delivery);
                try
                {
                    await using var stream = new FileStream(file.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var result = await handler.DeliverAsync(
                        new DestinationHandlerContext(delivery.Id, descriptor, config, secrets), stream, timeout.Token);
                    delivery.Status = DeliveryStatus.Succeeded; delivery.BytesTransferred = result.BytesTransferred;
                    delivery.CompletedAt = _time.GetUtcNow().UtcDateTime; _deliveries.Upsert(delivery);
                    _telemetry.RecordDelivery(destination.Type, true, result.BytesTransferred, stopwatch.Elapsed.TotalMilliseconds);
                    _logger.LogInformation("DeliverySucceeded {DeliveryId} file {FileId} destination {DestinationId} type {DestinationType} bytes {BytesTransferred}",
                        delivery.Id, file.Id, destination.Id, destination.Type, result.BytesTransferred);
                    return delivery;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    var failure = SafeFailure(exception);
                    if (!failure.Retryable || attempt >= _options.MaxRetries)
                    { Fail(delivery, failure.Code, failure.Message); _telemetry.RecordDelivery(destination.Type, false, 0, stopwatch.Elapsed.TotalMilliseconds); return delivery; }
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << attempt)), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            delivery.Status = DeliveryStatus.Cancelled; delivery.CompletedAt = _time.GetUtcNow().UtcDateTime;
            delivery.ErrorCode = "Cancelled"; delivery.ErrorMessage = "The delivery was cancelled."; _deliveries.Upsert(delivery);
            _telemetry.RecordDelivery(destination.Type, false, 0, stopwatch.Elapsed.TotalMilliseconds);
            _logger.LogInformation("DeliveryCancelled {DeliveryId} type {DestinationType}", delivery.Id, destination.Type);
            return delivery;
        }
        catch (Exception exception)
        {
            var failure = SafeFailure(exception);
            Fail(delivery, failure.Code, failure.Message);
            _telemetry.RecordDelivery(destination.Type, false, 0, stopwatch.Elapsed.TotalMilliseconds);
            return delivery;
        }
        finally
        {
            stopwatch.Stop();
            if (enteredGate) gate.Release();
        }
    }

    public IReadOnlyList<DeliveryModel>? GetDestinationDeliveries(string owner, Guid destinationId)
    {
        if (Owned(owner, destinationId) is null) return null;
        return _deliveries.GetAll().Where(item => item.DestinationId == destinationId && IsOwner(item, owner))
            .OrderByDescending(item => item.StartedAt).ToList();
    }

    public IReadOnlyList<DeliveryModel>? GetFileDeliveries(string owner, bool isAdministrator, Guid fileId)
    {
        var file = _files.GetAll().SingleOrDefault(item => item.Id == fileId &&
            (isAdministrator || string.Equals(item.Owner, owner, StringComparison.OrdinalIgnoreCase)));
        if (file is null) return null;
        return _deliveries.GetAll().Where(item => item.FileId == fileId &&
            (isAdministrator || IsOwner(item, owner))).OrderByDescending(item => item.StartedAt).ToList();
    }

    private DestinationModel? Owned(string owner, Guid id) =>
        _destinations.GetAll().SingleOrDefault(item => item.Id == id && IsOwner(item, owner));
    private JsonElement Unprotect(DestinationModel destination) => string.IsNullOrWhiteSpace(destination.ProtectedCredentials)
        ? throw new DestinationException("InvalidConfiguration", "Destination credentials are not configured.")
        : _credentials.Unprotect(destination.ProtectedCredentials);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private void Fail(DeliveryModel delivery, string code, string message)
    {
        delivery.Status = DeliveryStatus.Failed; delivery.CompletedAt = _time.GetUtcNow().UtcDateTime;
        delivery.ErrorCode = code; delivery.ErrorMessage = message; _deliveries.Upsert(delivery);
        _logger.LogWarning("DeliveryFailed {DeliveryId} type {DestinationType} error {ErrorCode}", delivery.Id, delivery.DestinationType, code);
    }
    private static DestinationException SafeFailure(Exception exception) => exception switch
    {
        DestinationException known => known,
        AmazonS3Exception s3 when s3.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new("AuthenticationFailed", "S3 authentication or authorization failed.", false, s3),
        AmazonS3Exception s3 when (int)s3.StatusCode >= 500 || (int)s3.StatusCode == 429 =>
            new("DestinationUnavailable", "S3 is temporarily unavailable.", true, s3),
        MailKit.Security.AuthenticationException auth =>
            new("AuthenticationFailed", "SMTP authentication failed.", false, auth),
        SmtpCommandException smtp when (int)smtp.StatusCode >= 400 && (int)smtp.StatusCode < 500 =>
            new("DestinationUnavailable", "SMTP is temporarily unavailable.", true, smtp),
        FtpAuthenticationException ftp =>
            new("AuthenticationFailed", "FTP authentication failed.", false, ftp),
        TimeoutException timeout => new("ConnectionTimeout", "The destination timed out.", true, timeout),
        IOException io => new("DestinationUnavailable", "The destination connection failed.", true, io),
        _ => new("DeliveryFailed", "The destination could not accept the file.", false, exception)
    };
    private static DestinationSummary Summary(DestinationModel item) => new(item.Id, item.Name, item.Type,
        item.IsEnabled, item.CreatedAt, item.UpdatedAt, !string.IsNullOrEmpty(item.ProtectedCredentials),
        item.LastTestedAt, item.LastTestSucceeded);
    private static DestinationDetails Details(DestinationModel item) => new(item.Id, item.Name, item.Type,
        item.IsEnabled, Parse(item.ConfigurationJson), item.CreatedAt, item.UpdatedAt,
        !string.IsNullOrEmpty(item.ProtectedCredentials), item.LastTestedAt, item.LastTestSucceeded);
    private static bool IsOwner(DestinationModel item, string owner) =>
        string.Equals(item.OwnerUserId, owner, StringComparison.OrdinalIgnoreCase);
    private static bool IsOwner(DeliveryModel item, string owner) =>
        string.Equals(item.OwnerUserId, owner, StringComparison.OrdinalIgnoreCase);
    private static void EnsureOwner(string owner) { if (string.IsNullOrWhiteSpace(owner)) throw new UnauthorizedAccessException(); }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name)) throw new DestinationException("InvalidConfiguration", "A destination name is required."); }
    private static string MimeFor(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    { ".pdf" => "application/pdf", ".html" or ".htm" => "text/html", ".txt" => "text/plain", ".json" => "application/json", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => "application/octet-stream" };
}
#pragma warning restore CS1591
