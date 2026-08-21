using System.Security.Claims;
using Hangfire;
using Lebiru.FileService.Controllers;
using Lebiru.FileService.HangfireJobs;
using Lebiru.FileService.Models;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StoredFileInfo = Lebiru.FileService.Models.FileInfo;

namespace Lebiru.FileService.Tests.Controllers;

public sealed class FileViewControllerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"felix-file-view-{Guid.NewGuid():N}");

    [Fact]
    public void AuthorizedDedicatedPageRecordsViewAfterAuthorization()
    {
        var file = CreateFile("alice");
        var tracker = new Mock<IFileViewTrackingService>();
        var updated = Clone(file);
        updated.ViewCount = 8;
        updated.LastViewedAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        updated.DailyViewCounts["2026-08-21"] = 8;
        tracker.Setup(service => service.Record(file.Id, "alice"))
            .Returns(new FileViewRecordResult(updated, true, false, false));
        var controller = Controller(file, tracker.Object, "alice", UserRoles.Contributor);

        var result = Assert.IsType<ViewResult>(controller.Details(file.Id));

        var model = Assert.IsType<FileDetailsViewModel>(result.Model);
        Assert.Equal(8, model.ViewCount);
        Assert.Equal(updated.LastViewedAt, model.LastViewedAt);
        tracker.Verify(service => service.Record(file.Id, "alice"), Times.Once);
    }

    [Fact]
    public void CrossOwnerRequestReturnsNotFoundWithoutRecording()
    {
        var file = CreateFile("alice");
        var tracker = new Mock<IFileViewTrackingService>();
        var controller = Controller(file, tracker.Object, "mallory", UserRoles.Contributor);

        Assert.IsType<NotFoundResult>(controller.Details(file.Id));

        tracker.Verify(service => service.Record(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void MissingFileReturnsNotFoundWithoutRecording()
    {
        var file = CreateFile("alice");
        File.Delete(file.FilePath);
        var tracker = new Mock<IFileViewTrackingService>();
        var controller = Controller(file, tracker.Object, "alice", UserRoles.Contributor);

        Assert.IsType<NotFoundResult>(controller.Details(file.Id));

        tracker.Verify(service => service.Record(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void AdministratorCanViewOwnedFileAndRecordsOwnView()
    {
        var file = CreateFile("alice");
        var tracker = new Mock<IFileViewTrackingService>();
        tracker.Setup(service => service.Record(file.Id, "admin"))
            .Returns(new FileViewRecordResult(file, true, false, false));
        var controller = Controller(file, tracker.Object, "admin", UserRoles.Admin);

        Assert.IsType<ViewResult>(controller.Details(file.Id));
        tracker.Verify(service => service.Record(file.Id, "admin"), Times.Once);
    }

    [Fact]
    public void TrackingFailureDoesNotPreventAuthorizedPageRendering()
    {
        var file = CreateFile("alice");
        var tracker = new Mock<IFileViewTrackingService>();
        tracker.Setup(service => service.Record(file.Id, "alice"))
            .Returns(new FileViewRecordResult(null, false, false, true));
        var controller = Controller(file, tracker.Object, "alice", UserRoles.Contributor);

        var result = Assert.IsType<ViewResult>(controller.Details(file.Id));

        Assert.Equal(0, Assert.IsType<FileDetailsViewModel>(result.Model).ViewCount);
    }

    [Fact]
    public void MetadataApiExposesReadOnlyViewMetricsWithoutRecordingView()
    {
        var file = CreateFile("alice");
        file.ViewCount = 12;
        file.LastViewedAt = new DateTime(2026, 8, 21, 10, 0, 0, DateTimeKind.Utc);
        file.DailyViewCounts["2026-08-21"] = 12;
        var tracker = new Mock<IFileViewTrackingService>();
        var controller = Controller(file, tracker.Object, "alice", UserRoles.Contributor);

        var result = Assert.IsType<OkObjectResult>(controller.FileDetailsApi(file.Id));

        var response = Assert.IsType<FileDetailsResponse>(result.Value);
        Assert.Equal(12, response.ViewCount);
        Assert.Equal(file.LastViewedAt, response.LastViewedAt);
        tracker.Verify(service => service.Record(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    private StoredFileInfo CreateFile(string owner)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "report.txt");
        File.WriteAllText(path, "hello");
        return new StoredFileInfo
        {
            Id = Guid.NewGuid(), FileName = "report.txt", FilePath = path, FileSize = 5,
            Owner = owner, UploadTime = DateTime.UtcNow
        };
    }

    private FileController Controller(StoredFileInfo file, IFileViewTrackingService tracker,
        string viewer, string role)
    {
        var metadata = new Mock<IFileMetadataStore>();
        metadata.Setup(store => store.GetAll()).Returns([Clone(file)]);
        var users = new Mock<IUserService>();
        var controller = new FileController(new CleanupJob(_root, users.Object),
            Mock.Of<IBackgroundJobClient>(), new ConfigurationBuilder().Build(),
            Mock.Of<IApiMetricsService>(), users.Object, Mock.Of<IMimeValidationService>(),
            NullLogger<FileController>.Instance, metadata.Object,
            fileViewTrackingService: tracker);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, viewer), new Claim(ClaimTypes.Role, role)], "Test"))
            }
        };
        return controller;
    }

    private static StoredFileInfo Clone(StoredFileInfo file) => new()
    {
        Id = file.Id, FileName = file.FileName, FilePath = file.FilePath, FileSize = file.FileSize,
        UploadTime = file.UploadTime, ExpiryTime = file.ExpiryTime, Owner = file.Owner,
        DirectoryId = file.DirectoryId, ViewCount = file.ViewCount, LastViewedAt = file.LastViewedAt,
        DailyViewCounts = new Dictionary<string, long>(file.DailyViewCounts)
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
