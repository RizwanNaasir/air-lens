using AirLens.Core.VirtualCamera;
using Xunit;

namespace AirLens.Core.Tests;

public class VirtualCameraGeometryTests
{
    [Fact]
    public void FourKToTenEightyFillsTheFrame()
    {
        var (src, dst) = VirtualCameraGeometry.Compute(3840, 2160, 1920, 1080, 0, 1.0, fill: false);
        Assert.Equal(new RectI(0, 0, 3840, 2160), src);
        Assert.Equal(new RectI(0, 0, 1920, 1080), dst);
    }

    [Fact]
    public void ZoomCropsTheCentre()
    {
        var (src, dst) = VirtualCameraGeometry.Compute(3840, 2160, 1920, 1080, 0, 2.0, fill: false);
        Assert.Equal(new RectI(960, 540, 2880, 1620), src); // 1920x1080 from the middle: native pixels
        Assert.Equal(new RectI(0, 0, 1920, 1080), dst);
    }

    [Fact]
    public void PortraitInALandscapeFrameGetsSideBars()
    {
        // Direct mode with the phone upright: the receiver turns a 1920x1080 frame a quarter turn.
        var (src, dst) = VirtualCameraGeometry.Compute(1920, 1080, 1920, 1080, 90, 1.0, fill: false);
        Assert.Equal(new RectI(0, 0, 1920, 1080), src);
        Assert.Equal(1080, dst.Height);
        Assert.Equal(608, dst.Width); // 1080 * 9/16, rounded to even
        Assert.Equal((1920 - 608) / 2, dst.Left);
    }

    [Fact]
    public void FillCropsThePortraitToTheFrameShape()
    {
        var (src, dst) = VirtualCameraGeometry.Compute(1920, 1080, 1920, 1080, 90, 1.0, fill: true);
        Assert.Equal(new RectI(0, 0, 1920, 1080), dst);
        // Shown as 1080 wide x 1920 tall; a 16:9 band of it is 1080 x 608, i.e. 608 x 1080 in input pixels.
        Assert.Equal(608, src.Width);
        Assert.Equal(1080, src.Height);
        Assert.Equal((1920 - 608) / 2, src.Left);
    }

    [Fact]
    public void EveryEdgeIsEvenAndZoomIsClamped()
    {
        var (src, dst) = VirtualCameraGeometry.Compute(2560, 1440, 1280, 720, 270, 99, fill: false);
        foreach (int v in new[] { src.Left, src.Top, src.Right, src.Bottom, dst.Left, dst.Top, dst.Right, dst.Bottom })
            Assert.Equal(0, v % 2);
        Assert.True(src.Width >= 2560 / VirtualCameraLook.MaxZoom - 2 || src.Height >= 1440 / VirtualCameraLook.MaxZoom - 2);
        Assert.Equal(90, VirtualCameraGeometry.Normalize(450));
        Assert.Equal(270, VirtualCameraGeometry.Normalize(-90));
    }
}
