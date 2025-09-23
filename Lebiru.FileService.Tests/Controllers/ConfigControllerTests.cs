using System;
using System.Collections.Generic;
using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Session;
using Lebiru.FileService.Services;

namespace Lebiru.FileService.Tests.Controllers
{
    public class ConfigControllerTests
    {
        private readonly Mock<IConfiguration> _configMock;
        private readonly Mock<IUserService> _userServiceMock;
        private readonly ConfigController _controller;

        public ConfigControllerTests()
        {
            // Set up configuration with FileService section
            var configSection = new Mock<IConfigurationSection>();
            configSection.Setup(s => s.Value).Returns("100");
            
            _configMock = new Mock<IConfiguration>();
            _configMock.Setup(c => c.GetSection("FileService"))
                      .Returns(configSection.Object);
            _configMock.Setup(c => c.GetSection(It.IsAny<string>()))
                      .Returns(configSection.Object);

            _userServiceMock = new Mock<IUserService>();
            _controller = new ConfigController(_userServiceMock.Object);

            // Setup service provider with all required services
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(_configMock.Object);
            services.AddDistributedMemoryCache();
            services.AddSession();
            services.AddMvc();
            var serviceProvider = services.BuildServiceProvider();
            
            // Setup controller context with service provider and session
            var httpContext = new DefaultHttpContext
            {
                RequestServices = serviceProvider
            };
            var sessionFeature = new SessionFeature
            {
                Session = new DistributedSession(
                    serviceProvider.GetRequiredService<IDistributedCache>(),
                    "test",
                    TimeSpan.FromMinutes(20),
                    TimeSpan.FromMinutes(1),
                    () => true,
                    Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
                    true)
            };
            httpContext.Features.Set<ISessionFeature>(sessionFeature);
            
            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
        }

        [Fact]
        public void Given_ValidConfiguration_When_AccessingIndex_Then_ReturnsEnvironmentVariables()
        {
            // Arrange
            var testVars = new Dictionary<string, string>
            {
                { "FileService:MaxFileSizeMB", "100" },
                { "FileService:MaxDiskSpaceGB", "10" },
                { "ASPNETCORE_ENVIRONMENT", "Development" }
            };

            // Act
            var result = _controller.Index() as ViewResult;
            var model = result?.Model as Dictionary<string, string>;

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(model);
            Assert.True(model?.Count > 0);
        }

        [Fact]
        public void Given_DarkModeRequest_When_TogglingDarkMode_Then_UpdatesSessionAndReturnsSuccess()
        {
            // Arrange
            var session = new Mock<ISession>();
            var httpContext = new DefaultHttpContext();
            httpContext.Session = session.Object;
            _controller.ControllerContext = new ControllerContext()
            {
                HttpContext = httpContext
            };

            // Act
            var result = _controller.ToggleDarkMode(true);

            // Assert
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(okResult);
            Assert.Equal(200, okResult.StatusCode);
            
            // Convert anonymous type to Dictionary
            var resultDict = okResult.Value?.GetType()
                .GetProperties()
                .ToDictionary(prop => prop.Name, prop => prop.GetValue(okResult.Value));

            Assert.NotNull(resultDict);
            Assert.True((bool?)resultDict["Success"] ?? false);
            Assert.True((bool?)resultDict["IsDarkMode"] ?? false);
            
            // Verify session was updated
            session.Verify(s => s.Set(
                It.Is<string>(key => key == "DarkMode"),
                It.Is<byte[]>(value => System.Text.Encoding.UTF8.GetString(value) == "true")
            ), Times.Once);
            session.Verify(s => s.Set(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Once);
        }
    }
}