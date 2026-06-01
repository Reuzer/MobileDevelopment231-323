namespace WindowsApp;

/// <summary>
/// Настройки построения предпросмотра и итогового изображения.
/// </summary>
public sealed class ImageRenderSettings
{
    /// <summary>
    /// Пользовательский угол поворота изображения в градусах.
    /// </summary>
    public double RotationDegrees { get; init; }

    /// <summary>
    /// Нужно ли смещать найденную область в центр холста после поворота.
    /// </summary>
    public bool CenterSelection { get; init; }

    /// <summary>
    /// Нужно ли автоматически обрезать результат вокруг найденного контура.
    /// </summary>
    public bool AutoCrop { get; init; }

    /// <summary>
    /// Дополнительный отступ вокруг найденного контура при автообрезке.
    /// </summary>
    public int CropMarginPixels { get; init; }

    /// <summary>
    /// Нужно ли показывать точки найденного контура на предпросмотре.
    /// </summary>
    public bool ShowContour { get; init; }

    /// <summary>
    /// Нужно ли показывать направляющие линии и ориентированный прямоугольник.
    /// </summary>
    public bool ShowGuideLines { get; init; }

    /// <summary>
    /// Нужно ли показывать диагонали ориентированного прямоугольника.
    /// </summary>
    public bool ShowDiagonals { get; init; }

    /// <summary>
    /// Нужно ли показывать рамку будущей обрезки на предпросмотре.
    /// </summary>
    public bool ShowCropFrame { get; init; }

    /// <summary>
    /// Максимальный размер стороны изображения предпросмотра, чтобы интерфейс не зависал на больших файлах.
    /// </summary>
    public int MaxPreviewDimension { get; init; } = 1800;
}
