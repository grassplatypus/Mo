using Mo.Core.DisplayConfiguration;

namespace Mo.Core.Tests;

public class RotationGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void HalfTurnsKeepTheSourceDimensions(int degrees)
    {
        Assert.Equal((2560, 1440), RotationGeometry.ToDesktop(2560, 1440, degrees));
        Assert.Equal((2560, 1440), RotationGeometry.ToSource(2560, 1440, degrees));
    }

    // Measured on the probe machine: CCD/NVAPI report source 2560x1440 at 270°,
    // while the GDI desktop rectangle is 1440x2560.
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void QuarterTurnsSwapALandscapePanel(int degrees)
    {
        Assert.Equal((1440, 2560), RotationGeometry.ToDesktop(2560, 1440, degrees));
        Assert.Equal((2560, 1440), RotationGeometry.ToSource(1440, 2560, degrees));
    }

    // The regression this class exists for: an aspect-ratio guard ("only swap when
    // width > height") is a no-op on a natively portrait panel, so the desktop extent
    // stayed portrait and the source mode was written back as landscape — a mode the
    // panel does not have.
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void QuarterTurnsSwapAPortraitPanelToo(int degrees)
    {
        Assert.Equal((1920, 1200), RotationGeometry.ToDesktop(1200, 1920, degrees));
        Assert.Equal((1200, 1920), RotationGeometry.ToSource(1920, 1200, degrees));
    }

    [Theory]
    [InlineData(0, 1200, 1920)]
    [InlineData(90, 1200, 1920)]
    [InlineData(180, 2560, 1440)]
    [InlineData(270, 2560, 1440)]
    public void ToSourceIsTheInverseOfToDesktop(int degrees, int sourceWidth, int sourceHeight)
    {
        var (dw, dh) = RotationGeometry.ToDesktop(sourceWidth, sourceHeight, degrees);
        Assert.Equal((sourceWidth, sourceHeight), RotationGeometry.ToSource(dw, dh, degrees));
    }

    [Fact]
    public void OnlyQuarterTurnsAreQuarterTurns()
    {
        Assert.False(RotationGeometry.IsQuarterTurn(0));
        Assert.True(RotationGeometry.IsQuarterTurn(90));
        Assert.False(RotationGeometry.IsQuarterTurn(180));
        Assert.True(RotationGeometry.IsQuarterTurn(270));
    }
}
