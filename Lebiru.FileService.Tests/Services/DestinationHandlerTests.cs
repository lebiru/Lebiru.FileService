using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.Extensions.Options;

namespace Lebiru.FileService.Tests.Services;

public sealed class DestinationHandlerTests
{
    private static readonly SsrfProtectionService PublicSsrf = new(new PublicResolver());

    [Fact]
    public async Task S3StreamsToNormalizedCollisionSafeKey()
    {
        var transport = new S3Transport();
        var handler = new S3FileDestination(transport);
        var content = new MemoryStream(Encoding.UTF8.GetBytes("payload"));

        var result = await handler.DeliverAsync(Context("../report.txt",
            "{\"bucket\":\"example-bucket\",\"region\":\"us-east-1\",\"prefix\":\"exports/daily\"}",
            "{\"accessKey\":\"key\",\"secretKey\":\"secret\"}"), content, default);

        Assert.Equal("exports/daily/report.txt", transport.Key);
        Assert.Equal("payload", transport.Content);
        Assert.Equal(7, result.BytesTransferred);
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("safe/../../private")]
    public void S3RejectsTraversalPrefixes(string prefix)
    {
        var handler = new S3FileDestination(new S3Transport());
        Assert.Throws<DestinationException>(() => handler.Validate(
            Json($"{{\"bucket\":\"example-bucket\",\"region\":\"us-east-1\",\"prefix\":\"{prefix}\"}}"),
            Json("{\"accessKey\":\"key\",\"secretKey\":\"secret\"}"), true));
    }

    [Fact]
    public async Task S3MapsPreconditionFailureToSafeCollisionError()
    {
        var handler = new S3FileDestination(new S3Transport(collision: true));

        var error = await Assert.ThrowsAsync<DestinationException>(() => handler.DeliverAsync(Context("report.txt",
            "{\"bucket\":\"example-bucket\",\"region\":\"us-east-1\"}",
            "{\"accessKey\":\"key\",\"secretKey\":\"secret\"}"), Stream.Null, default));

        Assert.Equal("ObjectAlreadyExists", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task EmailPassesExactStreamAndEnforcesAttachmentLimit()
    {
        var transport = new EmailTransport();
        var handler = new EmailFileDestination(transport, PublicSsrf,
            Options.Create(new DestinationOptions { MaxEmailAttachmentBytes = 7 }));
        var context = Context("report.txt",
            "{\"host\":\"smtp.example.test\",\"port\":587,\"useTls\":true,\"from\":\"from@example.com\",\"to\":\"to@example.com\"}", "{}");

        await handler.DeliverAsync(context, new MemoryStream(Encoding.UTF8.GetBytes("payload")), default);
        Assert.Equal("payload", transport.Content);
        Assert.Equal("to@example.com", transport.Configuration!.To);

        var oversized = context with { File = context.File with { Length = 8 } };
        var error = await Assert.ThrowsAsync<DestinationException>(() =>
            handler.DeliverAsync(oversized, Stream.Null, default));
        Assert.Equal("FileTooLarge", error.Code);
    }

    [Fact]
    public void EmailRejectsHeaderInjection()
    {
        var handler = new EmailFileDestination(new EmailTransport(), PublicSsrf, Options.Create(new DestinationOptions()));
        Assert.Throws<DestinationException>(() => handler.Validate(Json(
            "{\"host\":\"smtp.example.test\",\"port\":25,\"useTls\":false,\"from\":\"from@example.com\",\"to\":\"to@example.com\",\"subject\":\"ok\\r\\nBcc: victim@example.com\"}"), Json("{}"), true));
    }

    [Fact]
    public async Task FtpUsesTemporaryPartialPathThenFinalPath()
    {
        var transport = new FtpTransport();
        var handler = new FtpFileDestination(transport, PublicSsrf);
        var context = Context("../report.txt",
            "{\"host\":\"ftp.example.test\",\"port\":21,\"useTls\":true,\"remotePath\":\"exports/daily\"}",
            "{\"username\":\"user\",\"password\":\"secret\"}");

        await handler.DeliverAsync(context, new MemoryStream(Encoding.UTF8.GetBytes("payload")), default);

        Assert.Equal("/exports/daily/report.txt", transport.FinalPath);
        Assert.EndsWith(".partial", transport.TemporaryPath);
        Assert.StartsWith("/exports/daily/.report.txt.", transport.TemporaryPath);
        Assert.Equal("payload", transport.Content);
    }

    [Fact]
    public void FtpRejectsRemoteTraversal()
    {
        var handler = new FtpFileDestination(new FtpTransport(), PublicSsrf);
        Assert.Throws<DestinationException>(() => handler.Validate(Json(
            "{\"host\":\"ftp.example.test\",\"port\":21,\"useTls\":true,\"remotePath\":\"../private\"}"),
            Json("{\"username\":\"user\",\"password\":\"secret\"}"), true));
    }

    [Fact]
    public async Task NonHttpDestinationHostsAreSsrfProtected()
    {
        var handler = new FtpFileDestination(new FtpTransport(), new SsrfProtectionService(new PrivateResolver()));
        await Assert.ThrowsAsync<SsrfRejectedException>(() => handler.TestAsync(Json(
            "{\"host\":\"internal.test\",\"port\":21,\"useTls\":true,\"remotePath\":\"/\"}"),
            Json("{\"username\":\"user\",\"password\":\"secret\"}"), default));
    }

    private static DestinationHandlerContext Context(string fileName, string config, string credentials) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), new(Guid.NewGuid(), fileName, 7, "text/plain"),
            Json(config), Json(credentials));
    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class S3Transport(bool collision = false) : IS3DestinationTransport
    {
        public string? Key { get; private set; }
        public string? Content { get; private set; }
        public Task TestAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task UploadAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials, string key, Stream content, string contentType, CancellationToken cancellationToken)
        {
            if (collision) throw new AmazonS3Exception("collision") { StatusCode = HttpStatusCode.PreconditionFailed };
            Key = key; using var reader = new StreamReader(content, leaveOpen: true); Content = await reader.ReadToEndAsync(cancellationToken);
        }
    }
    private sealed class EmailTransport : IEmailDestinationTransport
    {
        public EmailDestinationConfiguration? Configuration { get; private set; }
        public string? Content { get; private set; }
        public Task TestAsync(EmailDestinationConfiguration configuration, EmailDestinationCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task SendAsync(EmailDestinationConfiguration configuration, EmailDestinationCredentials credentials, string fileName, string contentType, Stream content, CancellationToken cancellationToken)
        { Configuration = configuration; using var reader = new StreamReader(content, leaveOpen: true); Content = await reader.ReadToEndAsync(cancellationToken); }
    }
    private sealed class FtpTransport : IFtpDestinationTransport
    {
        public string? TemporaryPath { get; private set; }
        public string? FinalPath { get; private set; }
        public string? Content { get; private set; }
        public Task TestAsync(FtpDestinationConfiguration configuration, FtpDestinationCredentials credentials, CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task UploadAsync(FtpDestinationConfiguration configuration, FtpDestinationCredentials credentials, string temporaryPath, string finalPath, Stream content, CancellationToken cancellationToken)
        { TemporaryPath = temporaryPath; FinalPath = finalPath; using var reader = new StreamReader(content, leaveOpen: true); Content = await reader.ReadToEndAsync(cancellationToken); }
    }
    private sealed class PublicResolver : IHostAddressResolver
    { public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }); }
    private sealed class PrivateResolver : IHostAddressResolver
    { public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(new[] { IPAddress.Loopback }); }
}
