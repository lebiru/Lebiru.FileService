using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lebiru.FileService.Tests.Controllers;

public sealed class AspireControllerTests
{
    [Fact]
    public void ControllerRequiresAdminRole()
    {
        var attribute = Assert.Single(typeof(AspireController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(UserRoles.Admin, attribute.Roles);
    }

    [Fact]
    public void IndexRedirectsToConfiguredHttpsDashboard()
    {
        var controller = CreateController("https://aspire.example.com", Environments.Production);

        var result = Assert.IsType<RedirectResult>(controller.Index());

        Assert.Equal("https://aspire.example.com/", result.Url);
    }

    [Fact]
    public void IndexRejectsInsecureProductionDashboard()
    {
        var controller = CreateController("http://aspire.example.com", Environments.Production);

        var result = Assert.IsType<ObjectResult>(controller.Index());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost:18888", "http://localhost:18888/")]
    [InlineData("http://127.0.0.1:18888", "http://127.0.0.1:18888/")]
    [InlineData("http://[::1]:18888", "http://[::1]:18888/")]
    public void IndexAllowsLoopbackHttpDashboardInProduction(string dashboardUrl, string expectedUrl)
    {
        var controller = CreateController(dashboardUrl, Environments.Production);

        var result = Assert.IsType<RedirectResult>(controller.Index());

        Assert.Equal(expectedUrl, result.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("javascript:alert(1)")]
    public void IndexRejectsMissingOrUnsafeDashboardUrl(string dashboardUrl)
    {
        var controller = CreateController(dashboardUrl, Environments.Development);

        Assert.IsType<NotFoundResult>(controller.Index());
    }

    private static AspireController CreateController(string dashboardUrl, string environmentName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aspire:DashboardUrl"] = dashboardUrl
            })
            .Build();

        return new AspireController(configuration, new TestEnvironment(environmentName));
    }

    private sealed class TestEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Lebiru.FileService.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
