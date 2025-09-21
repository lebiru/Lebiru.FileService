using System;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Hangfire;
using OpenTelemetry.Trace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Session;

namespace Lebiru.FileService.Tests.Controllers
{
    public class FileControllerTests
    {
        private readonly Mock<IConfiguration> _configMock;
        private readonly Mock<IBackgroundJobClient> _backgroundJobClientMock;
        private readonly Mock<IApiMetricsService> _metricsServiceMock;
        private readonly FileController _controller;
        private readonly FileServiceConfig _config;

        private const string UploadsFolder = "uploads";

        public FileControllerTests()
        {
            // Set up configuration
            _config = new FileServiceConfig
            {
                MaxFileSizeMB = 100,
                MaxDiskSpaceGB = 10,
                WarningThresholdPercent = 90
            };

            var configSection = new Mock<IConfigurationSection>();
            configSection.Setup(s => s.Value).Returns("100");
            
            _configMock = new Mock<IConfiguration>();
            _configMock.Setup(c => c.GetSection("FileService"))
                      .Returns(configSection.Object);

            _backgroundJobClientMock = new Mock<IBackgroundJobClient>();
            _metricsServiceMock = new Mock<IApiMetricsService>();

            // Set up temp directory for uploads
            var tempPath = Path.Combine(Path.GetTempPath(), "FileServiceTests");
            Directory.CreateDirectory(tempPath);

            var tracerProvider = TracerProvider.Default;
            var cleanupJob = new CleanupJob(tempPath, tracerProvider);

            _controller = new FileController(
                cleanupJob,
                _backgroundJobClientMock.Object,
                _configMock.Object,
                _metricsServiceMock.Object);

            // Setup controller context
            // Set up service provider with all required services
            var services = new ServiceCollection();
            services.AddDistributedMemoryCache();
            services.AddSession();
            services.AddMvc();
            services.AddSingleton(_configMock.Object);
            services.AddSingleton(_backgroundJobClientMock.Object);
            services.AddSingleton(_metricsServiceMock.Object);
            var serviceProvider = services.BuildServiceProvider();

            var httpContext = new DefaultHttpContext();
            httpContext.RequestServices = serviceProvider;
            var sessionStorage = serviceProvider.GetRequiredService<IDistributedCache>();
            var sessionFeature = new SessionFeature { Session = new DistributedSession(sessionStorage, "test", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(1), () => true, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, true) };
            httpContext.Features.Set<ISessionFeature>(sessionFeature);

            _controller.ControllerContext = new ControllerContext
            {
                HttpContext = httpContext
            };
        }

        [Fact]
        public void Given_ValidRequest_When_AccessingIndex_Then_ReturnsViewWithExpiryOptions()
        {
            // Act
            var result = _controller.Index() as ViewResult;

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.ViewData["ExpiryOptions"]);
        }

        [Theory]
        [InlineData(ExpiryOption.Never)]      // No expiry
        [InlineData(ExpiryOption.OneHour)]    // 1 hour
        [InlineData(ExpiryOption.OneDay)]     // 24 hours
        [InlineData(ExpiryOption.OneWeek)]    // 1 week
        public async Task Given_ValidFile_When_UploadingWithExpiryOption_Then_ReturnsSuccess(ExpiryOption expiryOption)
        {
            // Arrange
            var fileName = "test.txt";
            var content = "test content";
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.Length).Returns(content.Length);
            fileMock.Setup(f => f.FileName).Returns(fileName);
            fileMock.Setup(f => f.OpenReadStream()).Returns(stream);

            // Act
            var result = await _controller.Upload(new List<IFormFile> { fileMock.Object }, expiryOption) as ObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(200, result.StatusCode);
        }

        [Fact]
        public async Task Given_OversizedFile_When_Uploading_Then_ReturnsBadRequest()
        {
            // Arrange
            var fileName = "large.txt";
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.Length).Returns((_config.MaxFileSizeMB + 1) * 1024 * 1024); // Slightly over limit
            fileMock.Setup(f => f.FileName).Returns(fileName);

            // Act
            var result = await _controller.Upload(new List<IFormFile> { fileMock.Object }, ExpiryOption.OneHour) as ObjectResult;

            // Assert
            Assert.NotNull(result);
            Assert.Equal(400, result.StatusCode);
        }
    }
}