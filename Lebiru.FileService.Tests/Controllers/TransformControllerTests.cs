using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Moq;
using Hangfire;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Xunit;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using System.Threading.Tasks;

namespace Lebiru.FileService.Tests.Controllers
{
  public class TransformControllerTests
  {
    private readonly Mock<IBackgroundJobClient> _mockBackgroundJobClient;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<IWebHostEnvironment> _mockEnvironment;
    private readonly Mock<ILogger<TransformController>> _mockLogger;
    private readonly string _testDirectory;

    public TransformControllerTests()
    {
      _mockBackgroundJobClient = new Mock<IBackgroundJobClient>();
      _mockConfiguration = new Mock<IConfiguration>();
      _mockEnvironment = new Mock<IWebHostEnvironment>();
      _mockLogger = new Mock<ILogger<TransformController>>();

      // Create a temporary test directory
      _testDirectory = Path.Combine(Path.GetTempPath(), "TransformControllerTests");
      Directory.CreateDirectory(_testDirectory);

      // Set up the environment mock
      _mockEnvironment.Setup(e => e.ContentRootPath).Returns(_testDirectory);

      // Create test data directory
      string dataFolder = Path.Combine(_testDirectory, "app-data");
      Directory.CreateDirectory(dataFolder);

      // Create empty data files
      File.WriteAllText(Path.Combine(dataFolder, "transforms.json"), "[]");
      File.WriteAllText(Path.Combine(dataFolder, "transformActivities.json"), "[]");

      // Mock configuration for FileServiceConfig
      var mockConfigSection = new Mock<IConfigurationSection>();
      mockConfigSection.Setup(s => s.Value).Returns("{}");
      _mockConfiguration.Setup(c => c.GetSection("FileServiceConfig")).Returns(mockConfigSection.Object);
    }

    [Fact]
    public void Index_ReturnsViewWithViewModel()
    {
      // Arrange
      var controller = new TransformController(
          _mockBackgroundJobClient.Object,
          _mockConfiguration.Object,
          _mockEnvironment.Object,
          _mockLogger.Object);

      // Act
      var result = controller.Index() as ViewResult;

      // Assert
      Assert.NotNull(result);
      Assert.Equal("Transform", result.ViewName);
      Assert.IsType<TransformViewModel>(result.Model);
    }

    [Fact]
    public void AddTransform_ReturnsViewWithEmptyModel()
    {
      // Arrange
      var controller = new TransformController(
          _mockBackgroundJobClient.Object,
          _mockConfiguration.Object,
          _mockEnvironment.Object,
          _mockLogger.Object);

      // Act
      var result = controller.AddTransform() as ViewResult;

      // Assert
      Assert.NotNull(result);
      Assert.IsType<TransformModel>(result.Model);
      var model = result.Model as TransformModel;
      Assert.NotNull(model);
      Assert.Empty(model.Title);
    }
  }
}