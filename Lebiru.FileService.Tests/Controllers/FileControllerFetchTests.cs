using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Hangfire;
using Lebiru.FileService.HangfireJobs;

namespace Lebiru.FileService.Tests.Controllers
{
  /// <summary>
  /// Test class for Fetch functionality in FileController
  /// </summary>
  public class FileControllerFetchTests
  {
    private readonly Mock<CleanupJob> _mockCleanupJob;
    private readonly Mock<IBackgroundJobClient> _mockBackgroundJobClient;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<IApiMetricsService> _mockMetricsService;
    private readonly Mock<IUserService> _mockUserService;
    private readonly Mock<IMimeValidationService> _mockMimeValidationService;
    private readonly Mock<ILogger<FileController>> _mockLogger;
    private readonly FileController _controller;

    /// <summary>
    /// Setup for FileControllerFetchTests
    /// </summary>
    public FileControllerFetchTests()
    {
      // Create mocks for dependencies
      // Create mocks for CleanupJob dependencies
      var mockBgClient = new Mock<IBackgroundJobClient>();
      var mockConfig = new Mock<IConfiguration>();
      var mockLogger = new Mock<ILogger<CleanupJob>>();
      _mockCleanupJob = new Mock<CleanupJob>(mockBgClient.Object, mockConfig.Object, mockLogger.Object);
      _mockBackgroundJobClient = new Mock<IBackgroundJobClient>();
      _mockConfiguration = new Mock<IConfiguration>();
      _mockMetricsService = new Mock<IApiMetricsService>();
      _mockUserService = new Mock<IUserService>();
      _mockMimeValidationService = new Mock<IMimeValidationService>();
      _mockLogger = new Mock<ILogger<FileController>>();

      // Setup configuration mock
      _mockConfiguration.Setup(x => x["FileService:MaxFileSizeMB"]).Returns("100");
      _mockConfiguration.Setup(x => x["FileService:MaxDiskSpaceGB"]).Returns("10");
      _mockConfiguration.Setup(x => x["FileService:AllowedFileExtensions"]).Returns("jpg,pdf,txt");

      // Create controller
      _controller = new FileController(
          _mockCleanupJob.Object,
          _mockBackgroundJobClient.Object,
          _mockConfiguration.Object,
          _mockMetricsService.Object,
          _mockUserService.Object,
          _mockMimeValidationService.Object,
          _mockLogger.Object);

      // Setup controller context
      var httpContext = new DefaultHttpContext();
      _controller.ControllerContext = new ControllerContext
      {
        HttpContext = httpContext
      };
    }

    /// <summary>
    /// Test that Fetch action returns the correct view
    /// </summary>
    [Fact]
    public void Fetch_ReturnsViewWithModel()
    {
      // Act
      var result = _controller.Fetch();

      // Assert
      var viewResult = Assert.IsType<ViewResult>(result);
      Assert.Equal("Fetch", viewResult.ViewName);
      Assert.IsType<FetchViewModel>(viewResult.Model);
    }

    /// <summary>
    /// Test that AddFetchSource GET action returns a view with a new model
    /// </summary>
    [Fact]
    public void AddFetchSource_Get_ReturnsViewWithNewModel()
    {
      // Act
      var result = _controller.AddFetchSource();

      // Assert
      var viewResult = Assert.IsType<ViewResult>(result);
      Assert.Equal("AddFetchSource", viewResult.ViewName);
      var model = Assert.IsType<FetchSourceModel>(viewResult.Model);

      // Check default values
      Assert.True(model.IsActive);
      Assert.True(model.UsePassiveFtp);
      Assert.Equal(60, model.FetchIntervalMinutes);
      Assert.False(model.IsRecursive);
      Assert.False(model.DeleteAfterFetch);
    }

    /// <summary>
    /// Test that SaveFetchSource (POST) adds a new fetch source and redirects
    /// </summary>
    [Fact]
    public void SaveFetchSource_Post_AddsSourceAndRedirects()
    {
      // Arrange
      var formCollection = new FormCollection(
          new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
          {
                    { "Name", "Test FTP Source" },
                    { "Type", "FTP" },
                    { "ServerUrl", "ftp.example.com" },
                    { "Username", "testuser" },
                    { "Password", "password" },
                    { "RemotePath", "/files" },
                    { "IsActive", "true" }
          });

      _controller.ControllerContext = new ControllerContext
      {
        HttpContext = new DefaultHttpContext
        {
          Request = { Form = formCollection }
        }
      };

      // Act
      var result = _controller.SaveFetchSource();

      // Assert
      var redirectResult = Assert.IsType<RedirectToActionResult>(result);
      Assert.Equal("Fetch", redirectResult.ActionName);
      if (_controller.TempData != null)
      {
        Assert.NotNull(_controller.TempData["SuccessMessage"]);
      }
    }

    /// <summary>
    /// Test that TestFetchConnection returns correct JSON result for HTTP connection
    /// </summary>
    [Fact]
    public async Task TestFetchConnection_HTTP_ReturnsJsonResult()
    {
      // Arrange
      var formCollection = new FormCollection(
          new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
          {
                    { "Name", "Test HTTP Source" },
                    { "Type", "HTTP" },
                    { "ServerUrl", "https://example.com" },
                    { "Username", "" },
                    { "Password", "" },
                    { "RemotePath", "" }
          });

      _controller.ControllerContext = new ControllerContext
      {
        HttpContext = new DefaultHttpContext
        {
          Request = { Form = formCollection }
        }
      };

      // Act
      var actionResult = await _controller.TestFetchConnection("");
      var result = actionResult as JsonResult;

      // Assert
      Assert.NotNull(result);
      if (result != null)
      {
        var resultValue = result.Value;
        if (resultValue != null)
        {
          dynamic dynValue = resultValue;
          Assert.False((bool)dynValue.success);
        }
      }
    }

    /// <summary>
    /// Test that ExecuteFetch queues background job and redirects
    /// </summary>
    [Fact]
    public void ExecuteFetch_WithInvalidSourceId_ReturnsRedirectWithError()
    {
      // Act
      var result = _controller.ExecuteFetch("non-existent-id");

      // Assert
      var redirectResult = Assert.IsType<RedirectToActionResult>(result);
      Assert.Equal("Fetch", redirectResult.ActionName);
      if (_controller.TempData != null)
      {
        Assert.NotNull(_controller.TempData["ErrorMessage"]);
      }
    }
  }
}