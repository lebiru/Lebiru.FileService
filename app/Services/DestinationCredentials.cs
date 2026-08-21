#pragma warning disable CS1591
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Lebiru.FileService.Services;

public interface IDestinationCredentialProtector
{
    string Protect(JsonElement credentials);
    JsonElement Unprotect(string protectedCredentials);
}
public sealed class DestinationCredentialProtector : IDestinationCredentialProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _protector;
    public DestinationCredentialProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("Lebiru.FileService.DestinationCredentials.v1");
    public string Protect(JsonElement credentials) => Prefix + _protector.Protect(credentials.GetRawText());
    public JsonElement Unprotect(string protectedCredentials)
    {
        if (!protectedCredentials.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Destination credentials use an unsupported format.");
        using var document = JsonDocument.Parse(_protector.Unprotect(protectedCredentials[Prefix.Length..]));
        return document.RootElement.Clone();
    }
}
#pragma warning restore CS1591
