using System.Windows;

namespace WindowsApp;

/// <summary>
/// Результат анализа изображения: найденная область, ее геометрия и точки контура.
/// </summary>
public sealed class ImageAnalysisResult
{
    /// <summary>
    /// Показывает, удалось ли найти полезный контур на изображении.
    /// </summary>
    public required bool Found { get; init; }

    /// <summary>
    /// Ширина исходного изображения в пикселях.
    /// </summary>
    public required int SourceWidth { get; init; }

    /// <summary>
    /// Высота исходного изображения в пикселях.
    /// </summary>
    public required int SourceHeight { get; init; }

    /// <summary>
    /// Прямоугольник, который ограничивает найденную область в координатах исходного изображения.
    /// </summary>
    public required Rect BoundingBox { get; init; }

    /// <summary>
    /// Центр найденной области в координатах исходного изображения.
    /// </summary>
    public required Point Center { get; init; }

    /// <summary>
    /// Угол наклона основной стороны найденной области в градусах.
    /// </summary>
    public required double EdgeAngleDegrees { get; init; }

    /// <summary>
    /// Доля найденной области относительно уменьшенной копии изображения, в процентах.
    /// </summary>
    public required double CoveragePercent { get; init; }

    /// <summary>
    /// Точки внешней границы найденной области, используемые для подсветки и обрезки.
    /// </summary>
    public required IReadOnlyList<Point> BoundaryPoints { get; init; }

    /// <summary>
    /// Четыре угла ориентированного прямоугольника, построенного по найденной области.
    /// </summary>
    public required IReadOnlyList<Point> EdgeBox { get; init; }
}
