using Lebiru.FileService.Services;

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

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ValidateAsync(candidate));
    }
}
