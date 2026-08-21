using System.Net;
using System.Net.Sockets;

namespace Lebiru.FileService.Services;

/// <summary>Resolves hostnames for deterministic SSRF validation.</summary>
public interface IHostAddressResolver
{
    /// <summary>Returns every address currently published for a hostname.</summary>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Indicates that an outbound destination is not publicly routable.</summary>
public sealed class SsrfRejectedException : InvalidOperationException
{
    /// <summary>Creates a safe SSRF rejection.</summary>
    public SsrfRejectedException() : base("The destination is not allowed by outbound network policy.") { }
}

/// <summary>Validates outbound HTTP destinations against SSRF protections.</summary>
public sealed class SsrfProtectionService
{
    private readonly IHostAddressResolver _resolver;

    /// <summary>Creates the validator with the system DNS resolver.</summary>
    public SsrfProtectionService(IHostAddressResolver? resolver = null) =>
        _resolver = resolver ?? new SystemHostAddressResolver();

    /// <summary>Validates a URI and rejects local, private, link-local, and metadata targets.</summary>
    public async Task<Uri> ValidateAsync(string candidate, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Only absolute HTTP and HTTPS URLs without embedded credentials are allowed.");

        if (uri.Port is not (80 or 443))
            throw new InvalidOperationException("Only standard HTTP and HTTPS ports are allowed.");

        await ResolvePublicAddressesAsync(uri.DnsSafeHost, cancellationToken);

        return uri;
    }

    /// <summary>Resolves a host and rejects the entire destination if any answer is non-public.</summary>
    public async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await _resolver.ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsRestricted))
            throw new SsrfRejectedException();
        return addresses;
    }

    /// <summary>
    /// Resolves, validates, and connects directly to an approved address so DNS cannot be rebound between checks.
    /// </summary>
    public async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastException = exception;
                if (exception is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException("The validated public destination could not be reached.", lastException);
    }

    /// <summary>Returns true when an address is not publicly routable.</summary>
    public static bool IsRestricted(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && (bytes[1] == 0 || bytes[1] == 168) ||
                   bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100) ||
                   bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
                   bytes[0] >= 224;
        }

        var ipv6 = address.GetAddressBytes();
        return address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal ||
               address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               address.Equals(IPAddress.IPv6Loopback) ||
               ipv6[0] is 0xfc or 0xfd ||
               ipv6[0] == 0x20 && ipv6[1] == 0x01 && ipv6[2] == 0x0d && ipv6[3] == 0xb8;
    }
}
