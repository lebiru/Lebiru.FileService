using System.Net;
using System.Net.Sockets;

namespace Lebiru.FileService.Services;

/// <summary>Validates outbound HTTP destinations against SSRF protections.</summary>
public sealed class SsrfProtectionService
{
    /// <summary>Validates a URI and rejects local, private, link-local, and metadata targets.</summary>
    public async Task<Uri> ValidateAsync(string candidate, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Only absolute HTTP and HTTPS URLs without embedded credentials are allowed.");

        if (uri.Port is not (80 or 443))
            throw new InvalidOperationException("Only standard HTTP and HTTPS ports are allowed.");

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsRestricted))
            throw new InvalidOperationException("The destination resolves to a restricted network address.");

        return uri;
    }

    private static bool IsRestricted(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is 0 or 10 or 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] >= 224;
        }

        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal ||
               address.Equals(IPAddress.IPv6Loopback) ||
               address.GetAddressBytes()[0] is 0xfc or 0xfd;
    }
}
