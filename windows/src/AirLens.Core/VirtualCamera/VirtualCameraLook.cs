namespace AirLens.Core.VirtualCamera;

/// <summary>
/// How the virtual camera's picture is framed for the apps that use it. Applied on the GPU while
/// scaling to the size the app asked for, so none of it costs a CPU pass.
/// </summary>
public sealed record VirtualCameraLook(
    /// <summary>Flip left-right, as seen (after rotation).</summary>
    bool Mirror = false,
    /// <summary>Extra clockwise rotation, on top of what the phone asks for in direct mode.</summary>
    int RotationDegrees = 0,
    /// <summary>Center crop, 1 = the whole picture. From a 4K stream, 2× still feeds 1080p natively.</summary>
    double Zoom = 1.0,
    /// <summary>Crop to fill the frame instead of fitting the whole picture with bars.</summary>
    bool Fill = false,
    /// <summary>Apps get a black picture (privacy) while the stream keeps running.</summary>
    bool Hidden = false)
{
    public const double MaxZoom = 4.0;
    public static readonly VirtualCameraLook Default = new();
}

/// <summary>Integer rectangle, right/bottom exclusive.</summary>
public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Where a frame goes when the virtual camera applies a <see cref="VirtualCameraLook"/>. Pure math,
/// unit-tested; the D3D11 video processor does the pixel work with these rectangles.
/// </summary>
public static class VirtualCameraGeometry
{
    /// <summary>
    /// <paramref name="rotationDegrees"/> is the total clockwise rotation. Returns the source crop in
    /// input pixels (before rotation) and the target rectangle in the output frame. NV12 needs even
    /// coordinates, so every edge is rounded to an even number.
    /// </summary>
    public static (RectI Source, RectI Dest) Compute(int srcW, int srcH, int dstW, int dstH, int rotationDegrees, double zoom, bool fill)
    {
        bool quarter = Normalize(rotationDegrees) % 180 != 0;
        double shownW = quarter ? srcH : srcW, shownH = quarter ? srcW : srcH; // the picture as it ends up
        double z = Math.Clamp(zoom, 1.0, VirtualCameraLook.MaxZoom);
        double cropW = shownW / z, cropH = shownH / z;
        double dstAspect = (double)dstW / dstH;
        if (fill)
        {
            if (cropW / cropH > dstAspect) cropW = cropH * dstAspect;
            else cropH = cropW / dstAspect;
        }

        double scale = Math.Min(dstW / cropW, dstH / cropH);
        int outW = Math.Min(dstW, Even(cropW * scale));
        int outH = Math.Min(dstH, Even(cropH * scale));
        int outX = Even((dstW - outW) / 2.0);
        int outY = Even((dstH - outH) / 2.0);

        // The crop is centred, so rotating it back to input orientation only swaps its sides.
        int inW = Math.Min(srcW, Math.Max(2, Even(quarter ? cropH : cropW)));
        int inH = Math.Min(srcH, Math.Max(2, Even(quarter ? cropW : cropH)));
        int inX = Even((srcW - inW) / 2.0);
        int inY = Even((srcH - inH) / 2.0);
        return (new RectI(inX, inY, inX + inW, inY + inH), new RectI(outX, outY, outX + outW, outY + outH));
    }

    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    private static int Even(double v) => (int)Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2;
}
