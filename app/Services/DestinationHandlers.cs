#pragma warning disable CS1591
using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentFTP;
using Lebiru.FileService.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Lebiru.FileService.Services;

public sealed class DestinationException : Exception
{
    public DestinationException(string code, string message, bool retryable = false, Exception? inner = null)
        : base(message, inner) { Code = code; Retryable = retryable; }
    public string Code { get; }
    public bool Retryable { get; }
}

public sealed record DestinationFile(Guid Id, string FileName, long Length, string ContentType);
public sealed record DestinationHandlerContext(Guid DeliveryId, DestinationFile File,
    JsonElement Configuration, JsonElement Credentials);
public sealed record HandlerDeliveryResult(long BytesTransferred);

public interface IFileDestination
{
    DestinationType Type { get; }
    void Validate(JsonElement configuration, JsonElement? credentials, bool requireCredentials);
    Task TestAsync(JsonElement configuration, JsonElement credentials, CancellationToken cancellationToken);
    Task<HandlerDeliveryResult> DeliverAsync(DestinationHandlerContext context, Stream content,
        CancellationToken cancellationToken);
}

public interface IDestinationHandlerResolver
{
    IFileDestination Resolve(DestinationType type);
}

public sealed class DestinationHandlerResolver(IEnumerable<IFileDestination> handlers) : IDestinationHandlerResolver
{
    private readonly IReadOnlyDictionary<DestinationType, IFileDestination> _handlers =
        handlers.ToDictionary(handler => handler.Type);
    public IFileDestination Resolve(DestinationType type) => _handlers.TryGetValue(type, out var handler)
        ? handler : throw new DestinationException("UnsupportedDestination", "The destination type is not supported.");
}

public interface IS3DestinationTransport
{
    Task TestAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials,
        CancellationToken cancellationToken);
    Task UploadAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials,
        string key, Stream content, string contentType, CancellationToken cancellationToken);
}

public sealed class AwsS3DestinationTransport : IS3DestinationTransport
{
    public async Task TestAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var client = Create(configuration, credentials);
        await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = configuration.Bucket },
            cancellationToken);
    }
    public async Task UploadAsync(S3DestinationConfiguration configuration, S3DestinationCredentials credentials,
        string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using var client = Create(configuration, credentials);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = configuration.Bucket, Key = key, InputStream = content,
            ContentType = contentType, AutoCloseStream = false, IfNoneMatch = "*"
        }, cancellationToken);
    }
    private static AmazonS3Client Create(S3DestinationConfiguration configuration,
        S3DestinationCredentials credentials)
    {
        AWSCredentials awsCredentials = string.IsNullOrWhiteSpace(credentials.SessionToken)
            ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
            : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
        return new AmazonS3Client(awsCredentials, RegionEndpoint.GetBySystemName(configuration.Region));
    }
}

public sealed class S3FileDestination(IS3DestinationTransport transport) : IFileDestination
{
    public DestinationType Type => DestinationType.S3;
    public void Validate(JsonElement configuration, JsonElement? credentials, bool requireCredentials)
    {
        var value = Parse<S3DestinationConfiguration>(configuration);
        if (string.IsNullOrWhiteSpace(value.Bucket) || value.Bucket.Length is < 3 or > 63 ||
            value.Bucket.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character is '.' or '-')))
            throw Invalid("A valid lowercase S3 bucket name is required.");
        if (string.IsNullOrWhiteSpace(value.Region) || value.Region.Length > 50) throw Invalid("A valid AWS region is required.");
        NormalizePrefix(value.Prefix);
        if (requireCredentials || credentials.HasValue) ParseCredentials(credentials);
    }
    public Task TestAsync(JsonElement configuration, JsonElement credentials, CancellationToken cancellationToken) =>
        transport.TestAsync(Parse<S3DestinationConfiguration>(configuration), Parse<S3DestinationCredentials>(credentials), cancellationToken);
    public async Task<HandlerDeliveryResult> DeliverAsync(DestinationHandlerContext context, Stream content,
        CancellationToken cancellationToken)
    {
        var config = Parse<S3DestinationConfiguration>(context.Configuration);
        var prefix = NormalizePrefix(config.Prefix);
        var key = prefix + SafeFileName(context.File.FileName);
        try
        {
            await transport.UploadAsync(config, Parse<S3DestinationCredentials>(context.Credentials), key,
                content, context.File.ContentType, cancellationToken);
            return new HandlerDeliveryResult(context.File.Length);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        { throw new DestinationException("ObjectAlreadyExists", "An object with this name already exists.", false, exception); }
    }
    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        var parts = prefix.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw Invalid("The S3 prefix is invalid.");
        return string.Join('/', parts) + "/";
    }
    private static S3DestinationCredentials ParseCredentials(JsonElement? value)
    {
        if (!value.HasValue) throw Invalid("S3 credentials are required.");
        var credentials = Parse<S3DestinationCredentials>(value.Value);
        if (string.IsNullOrWhiteSpace(credentials.AccessKey) || string.IsNullOrWhiteSpace(credentials.SecretKey))
            throw Invalid("S3 credentials are required.");
        return credentials;
    }
    private static DestinationException Invalid(string message) => new("InvalidConfiguration", message);
    internal static T Parse<T>(JsonElement value) => JsonSerializer.Deserialize<T>(value.GetRawText(),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw Invalid("Destination configuration is invalid.");
    internal static string SafeFileName(string value)
    {
        var result = Path.GetFileName(value.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..") throw Invalid("The file name is invalid.");
        return result;
    }
}

public interface IEmailDestinationTransport
{
    Task TestAsync(EmailDestinationConfiguration configuration, EmailDestinationCredentials credentials,
        CancellationToken cancellationToken);
    Task SendAsync(EmailDestinationConfiguration configuration, EmailDestinationCredentials credentials,
        string fileName, string contentType, Stream content, CancellationToken cancellationToken);
}

public sealed class MailKitDestinationTransport : IEmailDestinationTransport
{
    public async Task TestAsync(EmailDestinationConfiguration config, EmailDestinationCredentials credentials,
        CancellationToken token)
    {
        using var client = await Connect(config, credentials, token);
        await client.DisconnectAsync(true, token);
    }
    public async Task SendAsync(EmailDestinationConfiguration config, EmailDestinationCredentials credentials,
        string fileName, string contentType, Stream content, CancellationToken token)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(config.From));
        message.To.Add(MailboxAddress.Parse(config.To));
        message.Subject = config.Subject ?? "File from Felix File Service";
        var builder = new BodyBuilder { TextBody = config.Body ?? "A file was delivered by Felix File Service." };
        builder.Attachments.Add(fileName, content, ContentType.Parse(contentType));
        message.Body = builder.ToMessageBody();
        using var client = await Connect(config, credentials, token);
        await client.SendAsync(message, token);
        await client.DisconnectAsync(true, token);
    }
    private static async Task<SmtpClient> Connect(EmailDestinationConfiguration config,
        EmailDestinationCredentials credentials, CancellationToken token)
    {
        var client = new SmtpClient { CheckCertificateRevocation = true, Timeout = 30_000 };
        try
        {
            await client.ConnectAsync(config.Host, config.Port,
                config.UseTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, token);
            if (!string.IsNullOrWhiteSpace(credentials.Username))
                await client.AuthenticateAsync(credentials.Username, credentials.Password ?? string.Empty, token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
}

public sealed class EmailFileDestination(IEmailDestinationTransport transport, SsrfProtectionService ssrf,
    IOptions<DestinationOptions> options) : IFileDestination
{
    public DestinationType Type => DestinationType.Email;
    public void Validate(JsonElement configuration, JsonElement? credentials, bool requireCredentials)
    {
        var value = S3FileDestination.Parse<EmailDestinationConfiguration>(configuration);
        if (string.IsNullOrWhiteSpace(value.Host) || value.Port is < 1 or > 65535) throw Invalid("A valid SMTP host and port are required.");
        try { _ = MailboxAddress.Parse(value.From); _ = MailboxAddress.Parse(value.To); }
        catch (FormatException exception) { throw new DestinationException("InvalidConfiguration", "A valid sender and recipient are required.", false, exception); }
        if ((value.Subject?.Length ?? 0) > 200 || value.Subject?.IndexOfAny(['\r', '\n']) >= 0)
            throw Invalid("The email subject is invalid.");
        if ((value.Body?.Length ?? 0) > 10_000) throw Invalid("The email body is too long.");
        if (requireCredentials || credentials.HasValue) _ = ParseCredentials(credentials);
    }
    public async Task TestAsync(JsonElement configuration, JsonElement credentials, CancellationToken cancellationToken)
    {
        var config = S3FileDestination.Parse<EmailDestinationConfiguration>(configuration);
        await ValidateHost(config.Host, cancellationToken);
        await transport.TestAsync(config, S3FileDestination.Parse<EmailDestinationCredentials>(credentials), cancellationToken);
    }
    public async Task<HandlerDeliveryResult> DeliverAsync(DestinationHandlerContext context, Stream content,
        CancellationToken cancellationToken)
    {
        if (context.File.Length > options.Value.MaxEmailAttachmentBytes)
            throw new DestinationException("FileTooLarge", "The file exceeds the configured email attachment limit.");
        var config = S3FileDestination.Parse<EmailDestinationConfiguration>(context.Configuration);
        await ValidateHost(config.Host, cancellationToken);
        await transport.SendAsync(config, S3FileDestination.Parse<EmailDestinationCredentials>(context.Credentials),
            S3FileDestination.SafeFileName(context.File.FileName), context.File.ContentType, content, cancellationToken);
        return new HandlerDeliveryResult(context.File.Length);
    }
    private Task ValidateHost(string host, CancellationToken token) => ssrf.ValidateHostAsync(host, token);
    private static EmailDestinationCredentials ParseCredentials(JsonElement? value) => !value.HasValue
        ? throw Invalid("Email credentials must be provided, even when anonymous SMTP is intended.")
        : S3FileDestination.Parse<EmailDestinationCredentials>(value.Value);
    private static DestinationException Invalid(string message) => new("InvalidConfiguration", message);
}

public interface IFtpDestinationTransport
{
    Task TestAsync(FtpDestinationConfiguration configuration, FtpDestinationCredentials credentials,
        CancellationToken cancellationToken);
    Task UploadAsync(FtpDestinationConfiguration configuration, FtpDestinationCredentials credentials,
        string temporaryPath, string finalPath, Stream content, CancellationToken cancellationToken);
}

public sealed class FluentFtpDestinationTransport : IFtpDestinationTransport
{
    public async Task TestAsync(FtpDestinationConfiguration config, FtpDestinationCredentials credentials,
        CancellationToken token)
    {
        await using var client = Create(config, credentials);
        await client.Connect(token);
        if (!await client.DirectoryExists(config.RemotePath, token))
            throw new DestinationException("InvalidConfiguration", "The configured FTP directory does not exist.");
    }
    public async Task UploadAsync(FtpDestinationConfiguration config, FtpDestinationCredentials credentials,
        string temporaryPath, string finalPath, Stream content, CancellationToken token)
    {
        await using var client = Create(config, credentials);
        await client.Connect(token);
        if (await client.FileExists(finalPath, token))
            throw new DestinationException("ObjectAlreadyExists", "A remote file with this name already exists.");
        try
        {
            var status = await client.UploadStream(content, temporaryPath, FtpRemoteExists.Overwrite,
                false, null, token);
            if (status != FtpStatus.Success) throw new DestinationException("DestinationUnavailable", "FTP upload did not complete.", true);
            if (!await client.MoveFile(temporaryPath, finalPath, FtpRemoteExists.Skip, token))
                throw new DestinationException("ObjectAlreadyExists", "A remote file with this name already exists.");
        }
        catch
        {
            try { if (await client.FileExists(temporaryPath, token)) await client.DeleteFile(temporaryPath, token); } catch { }
            throw;
        }
    }
    private static AsyncFtpClient Create(FtpDestinationConfiguration config, FtpDestinationCredentials credentials)
    {
        var client = new AsyncFtpClient(config.Host, credentials.Username, credentials.Password, config.Port);
        client.Config.EncryptionMode = config.UseTls ? FtpEncryptionMode.Explicit : FtpEncryptionMode.None;
        client.Config.ConnectTimeout = 30_000;
        client.Config.ReadTimeout = 30_000;
        client.Config.DataConnectionConnectTimeout = 30_000;
        return client;
    }
}

public sealed class FtpFileDestination(IFtpDestinationTransport transport, SsrfProtectionService ssrf) : IFileDestination
{
    public DestinationType Type => DestinationType.Ftp;
    public void Validate(JsonElement configuration, JsonElement? credentials, bool requireCredentials)
    {
        var value = S3FileDestination.Parse<FtpDestinationConfiguration>(configuration);
        if (string.IsNullOrWhiteSpace(value.Host) || value.Port is < 1 or > 65535) throw Invalid("A valid FTP host and port are required.");
        NormalizeDirectory(value.RemotePath);
        if (requireCredentials || credentials.HasValue) _ = ParseCredentials(credentials);
    }
    public async Task TestAsync(JsonElement configuration, JsonElement credentials, CancellationToken cancellationToken)
    {
        var config = S3FileDestination.Parse<FtpDestinationConfiguration>(configuration);
        await ssrf.ValidateHostAsync(config.Host, cancellationToken);
        await transport.TestAsync(config, S3FileDestination.Parse<FtpDestinationCredentials>(credentials), cancellationToken);
    }
    public async Task<HandlerDeliveryResult> DeliverAsync(DestinationHandlerContext context, Stream content,
        CancellationToken cancellationToken)
    {
        var config = S3FileDestination.Parse<FtpDestinationConfiguration>(context.Configuration);
        await ssrf.ValidateHostAsync(config.Host, cancellationToken);
        var directory = NormalizeDirectory(config.RemotePath);
        var fileName = S3FileDestination.SafeFileName(context.File.FileName);
        var final = directory + fileName;
        var temporary = directory + $".{fileName}.{context.DeliveryId:N}.partial";
        await transport.UploadAsync(config, S3FileDestination.Parse<FtpDestinationCredentials>(context.Credentials),
            temporary, final, content, cancellationToken);
        return new HandlerDeliveryResult(context.File.Length);
    }
    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw Invalid("The FTP remote path is invalid.");
        return "/" + string.Join('/', parts) + (parts.Length == 0 ? string.Empty : "/");
    }
    private static FtpDestinationCredentials ParseCredentials(JsonElement? value)
    {
        if (!value.HasValue) throw Invalid("FTP credentials are required.");
        var result = S3FileDestination.Parse<FtpDestinationCredentials>(value.Value);
        if (string.IsNullOrWhiteSpace(result.Username) || string.IsNullOrWhiteSpace(result.Password)) throw Invalid("FTP credentials are required.");
        return result;
    }
    private static DestinationException Invalid(string message) => new("InvalidConfiguration", message);
}
#pragma warning restore CS1591
