#pragma warning disable CS1591
using Lebiru.FileService.Models;

namespace Lebiru.FileService.Services;

public interface IDestinationStore
{
    IReadOnlyList<DestinationModel> GetAll();
    void Replace(IEnumerable<DestinationModel> destinations);
}
public interface IDeliveryStore
{
    IReadOnlyList<DeliveryModel> GetAll();
    void Upsert(DeliveryModel delivery);
}
public sealed class DestinationStore : IDestinationStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private List<DestinationModel> _items;
    public DestinationStore(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "app-data", "destinations.json");
        _items = AtomicJsonStore.Read<List<DestinationModel>>(_path) ?? [];
    }
    public IReadOnlyList<DestinationModel> GetAll() { lock (_sync) return _items.Select(Clone).ToList(); }
    public void Replace(IEnumerable<DestinationModel> destinations)
    {
        lock (_sync) { var next = destinations.Select(Clone).ToList(); AtomicJsonStore.Write(_path, next); _items = next; }
    }
    private static DestinationModel Clone(DestinationModel item) => new()
    {
        Id = item.Id, OwnerUserId = item.OwnerUserId, Name = item.Name, Type = item.Type,
        IsEnabled = item.IsEnabled, ConfigurationJson = item.ConfigurationJson,
        ProtectedCredentials = item.ProtectedCredentials, CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt, LastTestedAt = item.LastTestedAt,
        LastTestSucceeded = item.LastTestSucceeded
    };
}
public sealed class DeliveryStore : IDeliveryStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private List<DeliveryModel> _items;
    public DeliveryStore(IWebHostEnvironment environment)
    {
        _path = Path.Combine(environment.ContentRootPath, "app-data", "deliveries.json");
        _items = AtomicJsonStore.Read<List<DeliveryModel>>(_path) ?? [];
    }
    public IReadOnlyList<DeliveryModel> GetAll() { lock (_sync) return _items.Select(Clone).ToList(); }
    public void Upsert(DeliveryModel delivery)
    {
        lock (_sync)
        {
            var next = _items.Select(Clone).ToList();
            var index = next.FindIndex(item => item.Id == delivery.Id);
            if (index < 0) next.Add(Clone(delivery)); else next[index] = Clone(delivery);
            AtomicJsonStore.Write(_path, next); _items = next;
        }
    }
    private static DeliveryModel Clone(DeliveryModel item) => new()
    {
        Id = item.Id, OwnerUserId = item.OwnerUserId, FileId = item.FileId,
        DestinationId = item.DestinationId, DestinationType = item.DestinationType,
        Status = item.Status, StartedAt = item.StartedAt, CompletedAt = item.CompletedAt,
        BytesTransferred = item.BytesTransferred, AttemptCount = item.AttemptCount,
        ErrorCode = item.ErrorCode, ErrorMessage = item.ErrorMessage
    };
}
#pragma warning restore CS1591
