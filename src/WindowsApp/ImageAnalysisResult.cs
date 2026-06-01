using System.Windows;

namespace WindowsApp;

public sealed class ImageAnalysisResult
{
    public required bool Found { get; init; }

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    public required Rect BoundingBox { get; init; }

    public required Point Center { get; init; }

    public required double EdgeAngleDegrees { get; init; }

    public required double CoveragePercent { get; init; }

    public required IReadOnlyList<Point> BoundaryPoints { get; init; }

    public required IReadOnlyList<Point> EdgeBox { get; init; }
}
