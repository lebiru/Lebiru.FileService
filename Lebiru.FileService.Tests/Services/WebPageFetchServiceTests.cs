using System.Net;
using System.Net.Http.Headers;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Tests.Services;

public sealed class WebPageFetchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"felix-web-fetch-{Guid.NewGuid():N}");

    [Fact]
    public async Task FetchStoresExactHtmlAsOwnedRootFile()
    {
        var body = "<!doctype html><title>Saved</title>";
        var store = new MemoryFileStore();
        var service = CreateService(store, Response(HttpStatusCode.OK, body, "text/html"));

        var result = await service.FetchAsync("alice", "https://example.test/news", null, default);

        var file = Assert.Single(store.GetAll());
        Assert.Equal("alice", file.Owner);
        Assert.Null(file.DirectoryId);
        Assert.Equal("news.html", result.FileName);
        Assert.Equal(body, await File.ReadAllTextAsync(file.FilePath));
        Assert.Equal(body.Length, result.BytesDownloaded);
    }

    [Fact]
    public async Task FetchUsesContentDispositionAndOwnedDestination()
    {
        var destination = Guid.NewGuid();
        var response = Response(HttpStatusCode.OK, "<p>ok</p>", "application/xhtml+xml");
        response.Content.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment") { FileName = "report.HTML" };
        var store = new MemoryFileStore();
        var service = CreateService(store, response, directoryOwned: true);

        var result = await service.FetchAsync("alice", "https://example.test/", destination, default);

        Assert.Equal("report.HTML", result.FileName);
        Assert.Equal(destination, Assert.Single(store.GetAll()).DirectoryId);
    }

    [Fact]
    public async Task FetchRejectsUnownedDestinationBeforeNetworkCall()
    {
        var service = CreateService(new MemoryFileStore(), Response(HttpStatusCode.OK, "<p>ok</p>"),
            directoryOwned: false);

        var exception = await Assert.ThrowsAsync<WebPageFetchException>(() =>
            service.FetchAsync("alice", "https://example.test/", Guid.NewGuid(), default));

        Assert.Equal("directory_not_found", exception.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FetchRejectsUnsuccessfulUpstreamStatus(HttpStatusCode status)
    {
        var service = CreateService(new MemoryFileStore(), Response(status, "error", "text/html"));

        var exception = await Assert.ThrowsAsync<WebPageFetchException>(() =>
            service.FetchAsync("alice", "https://example.test/", null, default));

        Assert.Equal("upstream_http_error", exception.Code);
    }

    [Fact]
    public async Task FetchRejectsNonHtmlWithoutCreatingAFile()
    {
        var store = new MemoryFileStore();
        var service = CreateService(store, Response(HttpStatusCode.OK, "{}", "application/json"));

        var exception = await Assert.ThrowsAsync<WebPageFetchException>(() =>
            service.FetchAsync("alice", "https://example.test/data", null, default));

        Assert.Equal("unsupported_content_type", exception.Code);
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task FetchRejectsChunkedResponseBeyondLimitAndRemovesTemporaryFile()
    {
        var store = new MemoryFileStore();
        var response = Response(HttpStatusCode.OK, new string('x', 2049), "text/html");
        response.Content.Headers.ContentLength = null;
        var service = CreateService(store, response, maxBytes: 2048);

        var exception = await Assert.ThrowsAsync<WebPageFetchException>(() =>
            service.FetchAsync("alice", "https://example.test/large", null, default));

        Assert.Equal("response_too_large", exception.Code);
        Assert.Empty(store.GetAll());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "uploads")));
    }

    [Fact]
    public async Task FetchFollowsValidatedRedirectAndUsesFinalUrl()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://cdn.example.test/page");
        var service = CreateService(new MemoryFileStore(), redirect,
            Response(HttpStatusCode.OK, "<p>ok</p>", "text/html"));

        var result = await service.FetchAsync("alice", "https://example.test/start", null, default);

        Assert.Equal("https://cdn.example.test/page", result.FinalUrl);
        Assert.Equal("page.html", result.FileName);
    }

    [Fact]
    public async Task FetchRejectsRedirectToPrivateDnsAnswer()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://internal.test/secret");
        var resolver = new HostResolver(host => host == "internal.test"
            ? [IPAddress.Loopback] : [IPAddress.Parse("93.184.216.34")]);
        var service = CreateService(new MemoryFileStore(), resolver, redirect);

        var exception = await Assert.ThrowsAsync<WebPageFetchException>(() =>
            service.FetchAsync("alice", "https://example.test/start", null, default));

        Assert.Equal("redirect_destination_blocked", exception.Code);
    }

    [Fact]
    public async Task CallerCancellationLeavesNoCompletedOrTemporaryFile()
    {
        var store = new MemoryFileStore();
        var handler = new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        var service = CreateService(store, handler, new HostResolver(_ => [IPAddress.Parse("93.184.216.34")]));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.FetchAsync("alice", "https://example.test/slow", null, cancellation.Token));

        Assert.Empty(store.GetAll());
        var uploads = Path.Combine(_root, "uploads");
        Assert.False(Directory.Exists(uploads) && Directory.EnumerateFiles(uploads).Any());
    }

    [Fact]
    public void FileNameFallsBackToHostnameAndSanitizesPathTraversal()
    {
        Assert.Equal("example.test.html", WebPageFetchService.DetermineFileName(null,
            new Uri("https://example.test/")));
        var disposition = new ContentDispositionHeaderValue("attachment") { FileName = "../../unsafe" };
        Assert.Equal("unsafe.html", WebPageFetchService.DetermineFileName(disposition,
            new Uri("https://example.test/")));
    }

    private WebPageFetchService CreateService(MemoryFileStore store, params HttpResponseMessage[] responses) =>
        CreateService(store, new QueueHandler(responses), new HostResolver(_ => [IPAddress.Parse("93.184.216.34")]));

    private WebPageFetchService CreateService(MemoryFileStore store, HttpResponseMessage response,
        bool directoryOwned = true, long maxBytes = 5 * 1024 * 1024) =>
        CreateService(store, new QueueHandler(response), new HostResolver(_ => [IPAddress.Parse("93.184.216.34")]),
            directoryOwned, maxBytes);

    private WebPageFetchService CreateService(MemoryFileStore store, IHostAddressResolver resolver,
        params HttpResponseMessage[] responses) => CreateService(store, new QueueHandler(responses), resolver);

    private WebPageFetchService CreateService(MemoryFileStore store, HttpMessageHandler handler,
        IHostAddressResolver resolver, bool directoryOwned = true, long maxBytes = 5 * 1024 * 1024)
    {
        Directory.CreateDirectory(_root);
        var clientFactory = new Mock<IHttpClientFactory>();
        clientFactory.Setup(factory => factory.CreateClient("WebPageFetch"))
            .Returns(new HttpClient(handler, disposeHandler: false));
        var userService = new Mock<IUserService>();
        var directoryService = new Mock<IVirtualDirectoryService>();
        directoryService.Setup(service => service.IsOwnedBy(It.IsAny<Guid>(), It.IsAny<string>()))
            .Returns(directoryOwned);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FileService:MaxDiskSpaceGB"] = "1" })
            .Build();
        return new WebPageFetchService(clientFactory.Object, new SsrfProtectionService(resolver), store,
            userService.Object, directoryService.Object, new TelemetryService(),
            Options.Create(new WebPageFetchOptions { MaxResponseBytes = maxBytes, TimeoutSeconds = 5 }),
            configuration, new TestEnvironment(_root), NullLogger<WebPageFetchService>.Instance);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body,
        string mediaType = "text/html") => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_responses.Dequeue());
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class HostResolver(Func<string, IPAddress[]> callback) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(callback(host));
    }

    private sealed class MemoryFileStore : IFileMetadataStore
    {
        private List<StoredFileInfo> _files = [];
        public List<StoredFileInfo> GetAll() => _files.Select(Clone).ToList();
        public void Replace(IEnumerable<StoredFileInfo> files) => _files = files.Select(Clone).ToList();
        public long UsedSpace => _files.Sum(file => file.FileSize);
        public StoredFileInfo? RecordView(Guid fileId, DateTime viewedAtUtc) => throw new NotSupportedException();
        private static StoredFileInfo Clone(StoredFileInfo file) => new()
        {
            Id = file.Id, FileName = file.FileName, FilePath = file.FilePath, Owner = file.Owner,
            DirectoryId = file.DirectoryId, FileSize = file.FileSize, UploadTime = file.UploadTime,
            ExpiryTime = file.ExpiryTime, ViewCount = file.ViewCount, LastViewedAt = file.LastViewedAt,
            DailyViewCounts = new Dictionary<string, long>(file.DailyViewCounts ?? [])
        };
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Lebiru.FileService.Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
