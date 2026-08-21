using System.Net;
using System.Text;
using System.Text.Json;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Tests.Services;

public sealed class DestinationServicesTests : IDisposable
{
    private readonly string _filePath = Path.GetTempFileName();
    private readonly MemoryDestinationStore _destinations = new();
    private readonly MemoryDeliveryStore _deliveries = new();
    private readonly TelemetryService _telemetry = new();

    public DestinationServicesTests() => File.WriteAllText(_filePath, "destination payload");

    [Fact]
    public void CrudIsOwnerScopedAndNeverReturnsCredentials()
    {
        var service = CreateService();
        var created = service.Create("alice", Request("top-secret"));

        Assert.True(created.CredentialsConfigured);
        Assert.DoesNotContain("top-secret", JsonSerializer.Serialize(created));
        Assert.Single(service.List("alice"));
        Assert.Empty(service.List("bob"));
        Assert.Null(service.Get("bob", created.Id));
        Assert.Null(service.Update("bob", created.Id, Request("replacement")));
        Assert.False(service.Delete("bob", created.Id));

        var updated = service.Update("alice", created.Id, Request(null, enabled: false));
        Assert.NotNull(updated);
        Assert.False(updated.IsEnabled);
        Assert.DoesNotContain("top-secret", JsonSerializer.Serialize(updated));
        Assert.True(service.Delete("alice", created.Id));
        Assert.Empty(service.List("alice"));
    }

    [Fact]
    public async Task DeliveryStreamsOwnedFileAndPersistsSafeHistory()
    {
        var handler = new FakeHandler();
        var service = CreateService(handler);
        var destination = service.Create("alice", Request("secret"));

        var result = await service.DeliverAsync("alice", false, FileId, destination.Id, default);

        Assert.Equal(DeliveryStatus.Succeeded, result.Status);
        Assert.Equal(new System.IO.FileInfo(_filePath).Length, result.BytesTransferred);
        Assert.Equal("destination payload", handler.Content);
        Assert.Single(service.GetFileDeliveries("alice", false, FileId)!);
        Assert.Single(service.GetDestinationDeliveries("alice", destination.Id)!);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task DeliveryRejectsCrossUserFileAndDestinationAccess()
    {
        var service = CreateService();
        var destination = service.Create("alice", Request("secret"));

        var fileError = await Assert.ThrowsAsync<DestinationException>(() =>
            service.DeliverAsync("bob", false, FileId, destination.Id, default));
        Assert.Equal("FileNotFound", fileError.Code);

        var destinationError = await Assert.ThrowsAsync<DestinationException>(() =>
            service.DeliverAsync("bob", true, FileId, destination.Id, default));
        Assert.Equal("DestinationNotFound", destinationError.Code);

        var bobsDestination = service.Create("bob", Request("bobs-secret"));
        var mixedOwnershipError = await Assert.ThrowsAsync<DestinationException>(() =>
            service.DeliverAsync("alice", false, FileId, bobsDestination.Id, default));
        Assert.Equal("DestinationNotFound", mixedOwnershipError.Code);
        Assert.Empty(_deliveries.GetAll());
    }

    [Fact]
    public void ProductionCredentialProtectorEncryptsAtRestAndRoundTrips()
    {
        var keyDirectory = Directory.CreateTempSubdirectory("destination-keys-");
        try
        {
            var protector = new DestinationCredentialProtector(DataProtectionProvider.Create(keyDirectory));
            var credentials = Json("{\"password\":\"plaintext-secret\"}");

            var protectedValue = protector.Protect(credentials);

            Assert.DoesNotContain("plaintext-secret", protectedValue);
            Assert.Equal("plaintext-secret", protector.Unprotect(protectedValue).GetProperty("password").GetString());
        }
        finally { keyDirectory.Delete(true); }
    }

    [Fact]
    public async Task DisabledDestinationIsRejectedBeforeHistoryIsCreated()
    {
        var service = CreateService();
        var destination = service.Create("alice", Request("secret", enabled: false));

        var error = await Assert.ThrowsAsync<DestinationException>(() =>
            service.DeliverAsync("alice", false, FileId, destination.Id, default));

        Assert.Equal("DestinationDisabled", error.Code);
        Assert.Empty(_deliveries.GetAll());
    }

    [Fact]
    public async Task HandlerFailureIsSanitizedAndRecorded()
    {
        var handler = new FakeHandler(new InvalidOperationException("secret=credential-value"));
        var service = CreateService(handler);
        var destination = service.Create("alice", Request("credential-value"));

        var result = await service.DeliverAsync("alice", false, FileId, destination.Id, default);

        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.Equal("DeliveryFailed", result.ErrorCode);
        Assert.DoesNotContain("credential-value", result.ErrorMessage);
        Assert.Single(_deliveries.GetAll());
    }

    [Fact]
    public async Task CancellationIsPersistedAndDoesNotLeakTheConcurrencySlot()
    {
        var handler = new FakeHandler(cancel: true);
        var service = CreateService(handler);
        var destination = service.Create("alice", Request("secret"));

        var cancelled = await service.DeliverAsync("alice", false, FileId, destination.Id, default);
        handler.Cancel = false;
        var retry = await service.DeliverAsync("alice", false, FileId, destination.Id, default);

        Assert.Equal(DeliveryStatus.Cancelled, cancelled.Status);
        Assert.Equal(DeliveryStatus.Succeeded, retry.Status);
    }

    [Fact]
    public async Task ConnectionTestIsOwnerScoped()
    {
        var service = CreateService();
        var destination = service.Create("alice", Request("secret"));

        Assert.Null(await service.TestAsync("bob", destination.Id, default));
        Assert.True((await service.TestAsync("alice", destination.Id, default))!.Success);
    }

    private Guid FileId { get; } = Guid.NewGuid();

    private DestinationService CreateService(FakeHandler? handler = null)
    {
        handler ??= new FakeHandler();
        return new DestinationService(_destinations, _deliveries,
            new MemoryFileStore(new StoredFileInfo
            {
                Id = FileId, FileName = "report.txt", FilePath = _filePath, FileSize = new System.IO.FileInfo(_filePath).Length,
                UploadTime = DateTime.UtcNow, Owner = "alice"
            }), new FakeProtector(), new DestinationHandlerResolver([handler]),
            Options.Create(new DestinationOptions { MaxRetries = 0, MaxConcurrentDeliveriesPerUser = 1 }),
            _telemetry, TimeProvider.System, NullLogger<DestinationService>.Instance);
    }

    private static DestinationUpsertRequest Request(string? secret, bool enabled = true) => new()
    {
        Name = "Archive", Type = DestinationType.S3, IsEnabled = enabled,
        Configuration = Json("{\"bucket\":\"example-bucket\"}"),
        Credentials = secret is null ? null : Json($"{{\"secret\":\"{secret}\"}}")
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public void Dispose()
    {
        _telemetry.Dispose();
        File.Delete(_filePath);
    }

    private sealed class FakeHandler(Exception? failure = null, bool cancel = false) : IFileDestination
    {
        public DestinationType Type => DestinationType.S3;
        public string? Content { get; private set; }
        public bool Cancel { get; set; } = cancel;
        public void Validate(JsonElement configuration, JsonElement? credentials, bool requireCredentials)
        {
            if (requireCredentials && !credentials.HasValue) throw new DestinationException("InvalidConfiguration", "Credentials required.");
        }
        public Task TestAsync(JsonElement configuration, JsonElement credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<HandlerDeliveryResult> DeliverAsync(DestinationHandlerContext context, Stream content, CancellationToken cancellationToken)
        {
            if (Cancel) throw new OperationCanceledException(cancellationToken);
            if (failure is not null) throw failure;
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            Content = await reader.ReadToEndAsync(cancellationToken);
            return new HandlerDeliveryResult(context.File.Length);
        }
    }

    private sealed class FakeProtector : IDestinationCredentialProtector
    {
        public string Protect(JsonElement credentials) => Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.GetRawText()));
        public JsonElement Unprotect(string protectedCredentials) => Json(Convert.FromBase64String(protectedCredentials) is var bytes
            ? Encoding.UTF8.GetString(bytes) : "{}");
    }

    private sealed class MemoryDestinationStore : IDestinationStore
    {
        private List<DestinationModel> _items = [];
        public IReadOnlyList<DestinationModel> GetAll() => _items.ToList();
        public void Replace(IEnumerable<DestinationModel> destinations) => _items = destinations.ToList();
    }

    private sealed class MemoryDeliveryStore : IDeliveryStore
    {
        private readonly List<DeliveryModel> _items = [];
        public IReadOnlyList<DeliveryModel> GetAll() => _items.ToList();
        public void Upsert(DeliveryModel delivery)
        {
            var index = _items.FindIndex(item => item.Id == delivery.Id);
            if (index < 0) _items.Add(delivery); else _items[index] = delivery;
        }
    }

    private sealed class MemoryFileStore(StoredFileInfo file) : IFileMetadataStore
    {
        public List<StoredFileInfo> GetAll() => [file];
        public void Replace(IEnumerable<StoredFileInfo> files) { }
        public long UsedSpace => file.FileSize;
        public StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc) => null;
    }
}
