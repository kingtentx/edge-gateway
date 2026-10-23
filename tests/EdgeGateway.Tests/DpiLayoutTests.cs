using System.Drawing;
using EdgeGateway.WinForms;

namespace EdgeGateway.Tests;

public sealed class DpiLayoutTests
{
    [Theory]
    [InlineData(96, 48)]
    [InlineData(120, 60)]
    [InlineData(144, 72)]
    [InlineData(168, 84)]
    [InlineData(192, 96)]
    public void FixedLogicalTargetKeepsItsPhysicalProportion(int dpi, int expected)
    {
        // The same baseline must be used after each transition, never the previous result.
        Assert.Equal(expected, DpiLayoutMath.Scale(48, dpi));
        Assert.Equal(48, DpiLayoutMath.Scale(48, 96));
    }

    [Fact]
    public void HighDpiMinimumCannotMakeWindowLargerThanSmallMonitor()
    {
        var workArea = new Rectangle(0, 0, 1366, 720);
        var minimum = DpiLayoutMath.MinimumWindow(new Size(960, 620), new Size(32, 78), 192, workArea);
        Assert.Equal(workArea.Size, minimum);
        Assert.Equal(new Rectangle(0, 0, 1366, 720), DpiLayoutMath.Fit(new Rectangle(100, 60, 2832, 1798), workArea));
    }

    [Fact]
    public void WorkingAreaCanStartOnNegativeMonitorCoordinatesAndReserveTaskbar()
    {
        var workArea = new Rectangle(-1920, 40, 1920, 1040);
        var fitted = DpiLayoutMath.Fit(new Rectangle(-100, -50, 800, 650), workArea);
        Assert.Equal(new Rectangle(-800, 40, 800, 650), fitted);
        Assert.True(workArea.Contains(fitted));
    }

    [Fact]
    public void WindowAlreadyInsideWorkingAreaIsNotResizedOrRecentered()
    {
        var window = new Rectangle(2150, 180, 900, 620);
        Assert.Equal(window, DpiLayoutMath.Fit(window, new Rectangle(1920, 0, 2560, 1390)));
    }
}
