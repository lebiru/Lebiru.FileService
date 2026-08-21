using System;
using System.Threading.Tasks;
using System.Linq.Expressions;
using System.Security.Claims;
using Xunit;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Session;
using Hangfire.MemoryStorage;
using Lebiru.FileService.HangfireJobs;

namespace Lebiru.FileService.Tests.Controllers
{
    public class FileControllerTests : IDisposable
    {
        private readonly Mock<IConfiguration> _configMock;
        private readonly Mock<IBackgroundJobClient> _backgroundJobClientMock;
        private readonly Mock<IApiMetricsService> _metricsServiceMock;
        private readonly Mock<IUserService> _userServiceMock;
        private readonly FileController _controller;
        private readonly FileServiceConfig _config;
        private readonly string _tempPath;

        private const string UploadsFolder = "uploads";

        public FileControllerTests()
        {
            // Initialize Hangfire storage
            GlobalConfiguration.Configuration.UseMemoryStorage();

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
            _tempPath = Path.Combine(Path.GetTempPath(), "FileServiceTests");
            Directory.CreateDirectory(_tempPath);

            // Set up user service mock
            _userServiceMock = new Mock<IUserService>();
            _userServiceMock.Setup(u => u.AddFileToUser(It.IsAny<string>(), It.IsAny<string>()));
            _userServiceMock.Setup(u => u.IsFileOwner(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

            // Set up metrics service mock
            _metricsServiceMock.Setup(m => m.LastUpdated).Returns(DateTime.UtcNow);

            var cleanupJob = new CleanupJob(_tempPath, _userServiceMock.Object);
            var mimeValidationServiceMock = new Mock<IMimeValidationService>();

            // Setup validation to always return valid for test files
            mimeValidationServiceMock
                .Setup(m => m.ValidateFileDetailed(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((true, "File type is allowed"));

            var loggerMock = new Mock<ILogger<FileController>>();
            _controller = new FileController(
                cleanupJob,
                _backgroundJobClientMock.Object,
                _configMock.Object,
                _metricsServiceMock.Object,
                _userServiceMock.Object,
                mimeValidationServiceMock.Object,
                loggerMock.Object);

            // Setup controller context
            // Set up service provider with all required services
            var services = new ServiceCollection();
            services.AddDistributedMemoryCache();
            services.AddSession();
            services.AddMvc();
            services.AddSingleton(_configMock.Object);
            services.AddSingleton(_backgroundJobClientMock.Object);
            services.AddSingleton(_metricsServiceMock.Object);

            // Set up Hangfire storage for tests
            services.AddHangfire(config => config.UseMemoryStorage());
            services.AddHangfireServer();

            var serviceProvider = services.BuildServiceProvider();

            var httpContext = new DefaultHttpContext();
            httpContext.RequestServices = serviceProvider;
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "testuser"), new Claim(ClaimTypes.Role, UserRoles.Contributor)],
                "Test"));
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
        [InlineData("name_asc", "Alpha.txt")]
        [InlineData("name_desc", "Zulu.txt")]
        [InlineData("size_asc", "Alpha.txt")]
        [InlineData("size_desc", "Zulu.txt")]
        [InlineData("upload_asc", "Alpha.txt")]
        [InlineData("upload_desc", "Zulu.txt")]
        [InlineData("expiry_asc", "Alpha.txt")]
        [InlineData("expiry_desc", "Zulu.txt")]
        [InlineData("server_asc", "Alpha.txt")]
        [InlineData("server_desc", "Alpha.txt")]
        [InlineData("owner_asc", "Alpha.txt")]
        [InlineData("owner_desc", "Zulu.txt")]
        public void Given_SortColumn_When_ListingFiles_Then_ReturnsExpectedOrder(string sort, string expectedFirst)
        {
            var files = new List<Lebiru.FileService.Models.FileInfo>
            {
                new()
                {
                    FileName = "Zulu.txt", FilePath = "Zulu.txt", FileSize = 200,
                    UploadTime = new DateTime(2026, 2, 1), ExpiryTime = null, Owner = "Zoe"
                },
                new()
                {
                    FileName = "Alpha.txt", FilePath = "Alpha.txt", FileSize = 100,
                    UploadTime = new DateTime(2026, 1, 1), ExpiryTime = new DateTime(2027, 1, 1), Owner = "Alice"
                }
            };
            var metadataStore = new Mock<IFileMetadataStore>();
            metadataStore.Setup(store => store.GetAll()).Returns(files);
            var mimeValidation = new Mock<IMimeValidationService>();
            var controller = new FileController(
                new CleanupJob(_tempPath, _userServiceMock.Object),
                _backgroundJobClientMock.Object,
                _configMock.Object,
                _metricsServiceMock.Object,
                _userServiceMock.Object,
                mimeValidation.Object,
                Mock.Of<ILogger<FileController>>(),
                metadataStore.Object)
            {
                ControllerContext = _controller.ControllerContext
            };

            var result = Assert.IsType<PartialViewResult>(controller.List(1, 10, sort));
            var model = Assert.IsType<List<Lebiru.FileService.Models.FileInfo>>(result.Model);

            Assert.Equal(expectedFirst, model[0].FileName);
            Assert.Equal(sort, result.ViewData["Sort"]);
        }

        [Fact]
        public void Given_Directory_When_ListingFiles_Then_ReturnsOnlyOwnedFilesInThatDirectory()
        {
            var directoryId = Guid.NewGuid();
            var files = new List<Lebiru.FileService.Models.FileInfo>
            {
                new() { Id = Guid.NewGuid(), FileName = "inside.txt", FilePath = "inside.txt", Owner = "testuser", DirectoryId = directoryId },
                new() { Id = Guid.NewGuid(), FileName = "root.txt", FilePath = "root.txt", Owner = "testuser", DirectoryId = null },
                new() { Id = Guid.NewGuid(), FileName = "other-user.txt", FilePath = "other-user.txt", Owner = "other", DirectoryId = directoryId }
            };
            var metadataStore = new Mock<IFileMetadataStore>();
            metadataStore.Setup(store => store.GetAll()).Returns(files);
            var directories = new Mock<IVirtualDirectoryService>();
            directories.Setup(service => service.GetContents("testuser", directoryId)).Returns(
                new DirectoryContents(
                    new DirectoryItem(directoryId, "Reports", null, DateTime.UtcNow, DateTime.UtcNow),
                    [], [], [new DirectoryBreadcrumb(null, "Root"), new DirectoryBreadcrumb(directoryId, "Reports")]));
            var controller = new FileController(
                new CleanupJob(_tempPath, _userServiceMock.Object), _backgroundJobClientMock.Object,
                _configMock.Object, _metricsServiceMock.Object, _userServiceMock.Object,
                Mock.Of<IMimeValidationService>(), Mock.Of<ILogger<FileController>>(), metadataStore.Object,
                directoryService: directories.Object)
            {
                ControllerContext = _controller.ControllerContext
            };

            var result = Assert.IsType<PartialViewResult>(controller.List(directoryId: directoryId));
            var model = Assert.IsType<List<Lebiru.FileService.Models.FileInfo>>(result.Model);

            Assert.Equal("inside.txt", Assert.Single(model).FileName);
            Assert.Equal(directoryId, result.ViewData["CurrentDirectoryId"]);
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
            var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(400, badRequestResult.StatusCode);
        }

        [Fact]
        public async Task Given_FileUpload_When_UploadSucceeds_Then_AssignsFileOwnership()
        {
            // Arrange
            var fileName = "test.txt";
            var content = "test content";
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
            var username = "testuser";

            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.Length).Returns(content.Length);
            fileMock.Setup(f => f.FileName).Returns(fileName);
            fileMock.Setup(f => f.OpenReadStream()).Returns(stream);

            // Create a ClaimsPrincipal with the test username
            var claims = new System.Security.Claims.Claim[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username)
            };
            var identity = new System.Security.Claims.ClaimsIdentity(claims);
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            _controller.ControllerContext.HttpContext.User = principal;

            // Act
            var result = await _controller.Upload(new List<IFormFile> { fileMock.Object }, ExpiryOption.Never);

            // Assert
            Assert.NotNull(result);
            var objectResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, objectResult.StatusCode);
            _userServiceMock.Verify(u => u.AddFileToUser(username, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Given_OwnedDirectory_When_Uploading_Then_PersistsDirectoryMetadata()
        {
            var directoryId = Guid.NewGuid();
            var stored = new List<Lebiru.FileService.Models.FileInfo>();
            var metadataStore = new Mock<IFileMetadataStore>();
            metadataStore.Setup(store => store.GetAll()).Returns(stored);
            metadataStore.SetupGet(store => store.UsedSpace).Returns(0);
            metadataStore.Setup(store => store.Replace(It.IsAny<IEnumerable<Lebiru.FileService.Models.FileInfo>>()))
                .Callback<IEnumerable<Lebiru.FileService.Models.FileInfo>>(files => stored = files.ToList());
            var directories = new Mock<IVirtualDirectoryService>();
            directories.Setup(service => service.IsOwnedBy(directoryId, "testuser")).Returns(true);
            var mimeValidation = new Mock<IMimeValidationService>();
            mimeValidation.Setup(service => service.ValidateFileDetailed(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((true, "valid"));
            var controller = new FileController(
                new CleanupJob(_tempPath, _userServiceMock.Object), _backgroundJobClientMock.Object,
                _configMock.Object, _metricsServiceMock.Object, _userServiceMock.Object,
                mimeValidation.Object, Mock.Of<ILogger<FileController>>(), metadataStore.Object,
                directoryService: directories.Object)
            {
                ControllerContext = _controller.ControllerContext
            };
            var fileName = $"directory-upload-{Guid.NewGuid():N}.txt";
            var file = new Mock<IFormFile>();
            file.SetupGet(item => item.FileName).Returns(fileName);
            file.SetupGet(item => item.Length).Returns(0);
            file.SetupGet(item => item.ContentType).Returns("text/plain");
            file.Setup(item => item.OpenReadStream()).Returns(() => new MemoryStream());

            try
            {
                var result = await controller.Upload([file.Object], ExpiryOption.Never, directoryId);

                Assert.IsType<OkObjectResult>(result);
                var metadata = Assert.Single(stored);
                Assert.Equal(directoryId, metadata.DirectoryId);
                Assert.NotEqual(Guid.Empty, metadata.Id);
            }
            finally
            {
                var uploaded = Path.Combine(Directory.GetCurrentDirectory(), UploadsFolder, fileName);
                if (File.Exists(uploaded)) File.Delete(uploaded);
            }
        }

        [Fact]
        public async Task Given_UnownedDirectory_When_Uploading_Then_ReturnsNotFoundWithoutWriting()
        {
            var directoryId = Guid.NewGuid();
            var directories = new Mock<IVirtualDirectoryService>();
            directories.Setup(service => service.IsOwnedBy(directoryId, "testuser")).Returns(false);
            var controller = new FileController(
                new CleanupJob(_tempPath, _userServiceMock.Object), _backgroundJobClientMock.Object,
                _configMock.Object, _metricsServiceMock.Object, _userServiceMock.Object,
                Mock.Of<IMimeValidationService>(), Mock.Of<ILogger<FileController>>(),
                directoryService: directories.Object)
            {
                ControllerContext = _controller.ControllerContext
            };

            var result = await controller.Upload([Mock.Of<IFormFile>()], ExpiryOption.Never, directoryId);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public void Given_IndexRequest_When_MetricsExist_Then_IncludesLastUpdatedTime()
        {
            // Arrange
            var lastUpdated = DateTime.UtcNow;
            _metricsServiceMock.Setup(m => m.LastUpdated).Returns(lastUpdated);

            // Act
            var result = _controller.Index() as ViewResult;

            // Assert
            Assert.NotNull(result);
            Assert.NotNull(result.ViewData["MetricsLastUpdated"]);
            Assert.Equal(lastUpdated, result.ViewData["MetricsLastUpdated"]);
        }

        [Fact]
        public void Given_DeleteAllFiles_When_Triggered_Then_ClearsFilesAndMetadata()
        {
            // Arrange
            var filesToDelete = new[] { "test1.txt", "test2.txt" };
            foreach (var file in filesToDelete)
            {
                var filePath = Path.Combine(_tempPath, file);
                File.WriteAllText(filePath, "test content");
            }

            // Set up Hangfire storage and background job client
            var storage = new MemoryStorage();
            JobStorage.Current = storage;
            var backgroundClient = new BackgroundJobClient(storage);
            var cleanupJob = new CleanupJob(_tempPath, _userServiceMock.Object);
            var mimeValidationServiceMock = new Mock<IMimeValidationService>();

            // Setup validation to always return valid for test files
            mimeValidationServiceMock
                .Setup(m => m.ValidateFileDetailed(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((true, "File type is allowed"));

            var loggerMock = new Mock<ILogger<FileController>>();
            var controller = new FileController(
                cleanupJob,
                backgroundClient,
                _configMock.Object,
                _metricsServiceMock.Object,
                _userServiceMock.Object,
                mimeValidationServiceMock.Object,
                loggerMock.Object);

            // Act
            var result = controller.TriggerCleanup() as ObjectResult;

            // Assert
            Assert.NotNull(result);
            var okResult = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, okResult.StatusCode);

            var jobStorage = JobStorage.Current;
            var count = jobStorage.GetMonitoringApi().EnqueuedCount("default");
            Assert.Equal(2, count);

            // Clean up test files
            if (Directory.Exists(_tempPath))
            {
                Directory.Delete(_tempPath, true);
            }
        }

        public void Dispose()
        {
            // Clean up test directory
            if (Directory.Exists(_tempPath))
            {
                Directory.Delete(_tempPath, true);
            }
        }
    }
}
