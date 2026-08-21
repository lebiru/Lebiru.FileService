using Lebiru.FileService.Services;
using System.Net;

namespace Lebiru.FileService.Tests.Services;

public sealed class SecurityServicesTests
{
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..%2Fsecret.txt")]
    [InlineData("folder/secret.txt")]
    [InlineData("C:\\secret.txt")]
    [InlineData("..\\secret.txt")]
    [InlineData("..%5Csecret.txt")]
    [InlineData("\\\\server\\share\\secret.txt")]
    [InlineData("report.txt:payload")]
    [InlineData("/etc/passwd")]
    public void ResolveFileRejectsPathsOutsideStorage(string candidate)
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "lebiru-security-tests");

        Assert.Throws<ArgumentException>(() => FilePathSecurity.ResolveFile(storageRoot, candidate));
    }

    [Fact]
    public void ResolveFileAllowsLeafNameWithinStorage()
    {
        var storageRoot = Path.Combine(Path.GetTempPath(), "lebiru-security-tests");

        var result = FilePathSecurity.ResolveFile(storageRoot, "report.pdf");

        Assert.Equal(Path.Combine(Path.GetFullPath(storageRoot), "report.pdf"), result);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("ftp://example.com/file")]
    [InlineData("https://user:password@example.com/")]
    public async Task SsrfValidationRejectsUnsafeDestinations(string candidate)
    {
        var service = new SsrfProtectionService();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.ValidateAsync(candidate));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.7")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("2001:db8::1")]
    public void SsrfValidationRecognizesNonPublicAddressRanges(string address) =>
        Assert.True(SsrfProtectionService.IsRestricted(IPAddress.Parse(address)));

    [Fact]
    public async Task SsrfValidationRejectsHostnameIfAnyDnsAnswerIsPrivate()
    {
        var service = new SsrfProtectionService(new StaticResolver(
            IPAddress.Parse("93.184.216.34"), IPAddress.Parse("127.0.0.1")));

        await Assert.ThrowsAsync<SsrfRejectedException>(() =>
            service.ValidateAsync("https://example.test/page"));
    }

    private sealed class StaticResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }
}
