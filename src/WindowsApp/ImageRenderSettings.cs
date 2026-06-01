namespace WindowsApp;

public sealed class ImageRenderSettings
{
    public double RotationDegrees { get; init; }

    public bool CenterSelection { get; init; }

    public bool AutoCrop { get; init; }

    public int CropMarginPixels { get; init; }

    public bool ShowContour { get; init; }

    public bool ShowGuideLines { get; init; }

    public bool ShowDiagonals { get; init; }

    public bool ShowCropFrame { get; init; }

    public int MaxPreviewDimension { get; init; } = 1800;
}
