using Lebiru.FileService.Controllers;
using Lebiru.FileService.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Lebiru.FileService.Tests.Services;

public class TelemetryServiceTests
{
    [Fact]
    public void RecordsRequestsErrorsAndLatency()
    {
        using var telemetry = new TelemetryService();
        telemetry.RequestStarted();
        telemetry.RequestCompleted("GET", "/File/Home", 200, 20);
        telemetry.RequestStarted();
        telemetry.RequestCompleted("POST", "/File/Upload", 500, 40);

        var snapshot = telemetry.GetSnapshot();

        Assert.Equal(2, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.TotalErrors);
        Assert.Equal(50, snapshot.ErrorRate);
        Assert.Equal(30, snapshot.AverageDurationMs);
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(2, snapshot.Series.Sum(point => point.Requests));
    }

    [Fact]
    public void ControllerReturnsDashboardAndJsonSnapshot()
    {
        using var telemetry = new TelemetryService();
        var controller = new TelemetryController(telemetry);

        var page = Assert.IsType<ViewResult>(controller.Index());
        Assert.IsType<TelemetrySnapshot>(page.Model);
        var json = Assert.IsType<JsonResult>(controller.Snapshot(15));
        Assert.IsType<TelemetrySnapshot>(json.Value);
    }
}
