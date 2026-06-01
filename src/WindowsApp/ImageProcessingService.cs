using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace WindowsApp;

/// <summary>
/// Содержит всю работу с изображениями: загрузку, поиск контура, построение предпросмотра,
/// поворот, обрезку и сохранение результата.
/// </summary>
public static class ImageProcessingService
{
    /// <summary>
    /// Максимальная сторона уменьшенной копии, на которой выполняется анализ.
    /// Это ускоряет обработку больших сканов.
    /// </summary>
    private const int AnalysisMaxDimension = 1400;

    /// <summary>
    /// Ограничение количества точек контура, чтобы подсветка не перегружала интерфейс.
    /// </summary>
    private const int MaxBoundaryPoints = 14000;

    /// <summary>
    /// Качество сохранения WebP-изображений.
    /// </summary>
    private const int WebpQuality = 90;

    /// <summary>
    /// Загружает изображение из файла и приводит его к формату BGRA32,
    /// с которым дальше работает анализатор.
    /// </summary>
    public static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        if (IsWebpPath(path))
        {
            return LoadWebpBitmap(stream);
        }

        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);

        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        source.Freeze();
        return source;
    }

    /// <summary>
    /// Анализирует изображение: строит маску переднего плана, находит основной объект
    /// и вычисляет его границы, центр, угол наклона и точки контура.
    /// </summary>
    public static ImageAnalysisResult Analyze(BitmapSource source, int sensitivity)
    {
        var analysisScale = Math.Min(1.0, (double)AnalysisMaxDimension / Math.Max(source.PixelWidth, source.PixelHeight));
        var analysisBitmap = CreateScaledBitmap(source, analysisScale);
        var buffer = PixelBuffer.FromBitmap(analysisBitmap);
        var background = EstimateBackground(buffer);
        var mask = BuildForegroundMask(buffer, background, sensitivity);

        Dilate(mask, buffer.Width, buffer.Height);
        Dilate(mask, buffer.Width, buffer.Height);
        FillSmallGaps(mask, buffer.Width, buffer.Height);

        var component = FindMainComponent(mask, buffer.Width, buffer.Height);
        if (component is null)
        {
            return EmptyResult(source);
        }

        var labelMap = component.Labels;
        var stats = component.Stats;
        var centerAnalysis = new Point(
            (stats.MinX + stats.MaxX) / 2.0,
            (stats.MinY + stats.MaxY) / 2.0);
        var edgeAngle = NormalizeEdgeAngle(stats.PrincipalAngleDegrees);
        var edgeBox = BuildOrientedBox(labelMap, stats, buffer.Width, edgeAngle, analysisScale);
        var boundaryPoints = BuildBoundaryPoints(labelMap, stats.Label, buffer.Width, buffer.Height, analysisScale);
        var sourceScale = 1.0 / analysisScale;

        return new ImageAnalysisResult
        {
            Found = true,
            SourceWidth = source.PixelWidth,
            SourceHeight = source.PixelHeight,
            BoundingBox = new Rect(
                stats.MinX * sourceScale,
                stats.MinY * sourceScale,
                Math.Max(1, (stats.MaxX - stats.MinX + 1) * sourceScale),
                Math.Max(1, (stats.MaxY - stats.MinY + 1) * sourceScale)),
            Center = new Point(centerAnalysis.X * sourceScale, centerAnalysis.Y * sourceScale),
            EdgeAngleDegrees = edgeAngle,
            CoveragePercent = stats.Area * 100.0 / (buffer.Width * buffer.Height),
            BoundaryPoints = boundaryPoints,
            EdgeBox = edgeBox
        };
    }

    /// <summary>
    /// Строит уменьшенное изображение для предпросмотра с наложением контура,
    /// направляющих линий, центра и рамки будущей обрезки.
    /// </summary>
    public static BitmapSource RenderPreview(BitmapSource source, ImageAnalysisResult? analysis, ImageRenderSettings settings)
    {
        var layout = BuildLayout(source, analysis, settings);
        var scale = Math.Min(1.0, (double)settings.MaxPreviewDimension / Math.Max(layout.Width, layout.Height));
        var width = Math.Max(1, (int)Math.Ceiling(layout.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(layout.Height * scale));
        var map = layout.ImageMap.Scale(scale);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(242, 245, 247)), null, new Rect(0, 0, width, height));
            context.PushTransform(new MatrixTransform(map.ToMatrix()));
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.Pop();

            if (analysis?.Found == true)
            {
                DrawAnalysisOverlay(context, analysis, layout, scale, settings);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Строит итоговое изображение для сохранения: применяет поворот, центровку и автообрезку.
    /// В отличие от предпросмотра, служебные линии в результат не рисуются.
    /// </summary>
    public static BitmapSource RenderResult(BitmapSource source, ImageAnalysisResult? analysis, ImageRenderSettings settings)
    {
        var layout = BuildLayout(source, analysis, settings);
        var cropRect = settings.AutoCrop && analysis?.Found == true
            ? GetCropRect(analysis, layout, settings.CropMarginPixels)
            : new Rect(0, 0, layout.Width, layout.Height);

        cropRect.Intersect(new Rect(0, 0, layout.Width, layout.Height));
        if (cropRect.IsEmpty || cropRect.Width < 1 || cropRect.Height < 1)
        {
            cropRect = new Rect(0, 0, layout.Width, layout.Height);
        }

        var width = Math.Max(1, (int)Math.Ceiling(cropRect.Width));
        var height = Math.Max(1, (int)Math.Ceiling(cropRect.Height));
        var map = layout.ImageMap.Translate(-cropRect.X, -cropRect.Y);
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            context.PushTransform(new MatrixTransform(map.ToMatrix()));
            context.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Сохраняет BitmapSource в файл. Формат выбирается по расширению имени файла.
    /// </summary>
    public static void SaveBitmap(BitmapSource bitmap, string path)
    {
        if (IsWebpPath(path))
        {
            SaveWebpBitmap(bitmap, path);
            return;
        }

        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Загружает WebP через SkiaSharp, потому что стандартные WPF-кодеки не гарантируют поддержку WebP.
    /// </summary>
    private static BitmapSource LoadWebpBitmap(Stream stream)
    {
        using var image = SKBitmap.Decode(stream);
        if (image is null)
        {
            throw new InvalidDataException("Invalid WebP image.");
        }

        var stride = image.Width * 4;
        var pixels = new byte[stride * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * stride;
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                var offset = row + x * 4;
                pixels[offset] = color.Blue;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Red;
                pixels[offset + 3] = color.Alpha;
            }
        }

        var bitmap = BitmapSource.Create(
            image.Width,
            image.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Кодирует изображение в WebP через SkiaSharp.
    /// </summary>
    private static void SaveWebpBitmap(BitmapSource bitmap, string path)
    {
        BitmapSource source = bitmap.Format == PixelFormats.Bgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        var info = new SKImageInfo(source.PixelWidth, source.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmapImage = new SKBitmap(info);
        Marshal.Copy(pixels, 0, bitmapImage.GetPixels(), pixels.Length);

        using var image = SKImage.FromBitmap(bitmapImage);
        using var data = image.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        if (data is null)
        {
            throw new InvalidOperationException("Could not encode WebP image.");
        }

        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Проверяет, нужно ли использовать отдельную ветку обработки WebP.
    /// </summary>
    private static bool IsWebpPath(string path)
    {
        return string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Форматирует угол без лишних нулей, чтобы одинаково показывать его в интерфейсе.
    /// </summary>
    public static string FormatAngle(double angle)
    {
        return angle.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Создает уменьшенную копию изображения для быстрого анализа.
    /// </summary>
    private static BitmapSource CreateScaledBitmap(BitmapSource source, double scale)
    {
        if (scale >= 0.999)
        {
            return source;
        }

        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>
    /// Оценивает цвет и шум фона по краям изображения.
    /// Это позволяет отделить скан или текст от однотонного фона планшета/сканера.
    /// </summary>
    private static BackgroundColor EstimateBackground(PixelBuffer buffer)
    {
        var strip = Math.Max(2, Math.Min(buffer.Width, buffer.Height) / 35);
        var step = Math.Max(1, Math.Max(buffer.Width, buffer.Height) / 900);
        var samples = new List<ColorSample>();

        for (var y = 0; y < buffer.Height; y += step)
        {
            for (var x = 0; x < strip; x += step)
            {
                samples.Add(buffer.GetSample(x, y));
                samples.Add(buffer.GetSample(buffer.Width - 1 - x, y));
            }
        }

        for (var x = 0; x < buffer.Width; x += step)
        {
            for (var y = 0; y < strip; y += step)
            {
                samples.Add(buffer.GetSample(x, y));
                samples.Add(buffer.GetSample(x, buffer.Height - 1 - y));
            }
        }

        var red = Median(samples.Select(sample => sample.R).ToArray());
        var green = Median(samples.Select(sample => sample.G).ToArray());
        var blue = Median(samples.Select(sample => sample.B).ToArray());
        var luminance = Luminance(red, green, blue);

        var diffs = samples
            .Select(sample => Difference(sample.R, sample.G, sample.B, red, green, blue))
            .OrderBy(value => value)
            .ToArray();
        var mean = diffs.Length == 0 ? 0 : diffs.Average();
        var variance = diffs.Length == 0
            ? 0
            : diffs.Select(value => (value - mean) * (value - mean)).Average();

        return new BackgroundColor(red, green, blue, luminance, mean, Math.Sqrt(variance));
    }

    /// <summary>
    /// Строит бинарную маску: true означает пиксель, достаточно отличающийся от фона.
    /// </summary>
    private static bool[] BuildForegroundMask(PixelBuffer buffer, BackgroundColor background, int sensitivity)
    {
        sensitivity = Math.Clamp(sensitivity, 5, 95);
        var adaptiveThreshold = Math.Max(12, background.NoiseMean + background.NoiseStdDev * 2.4 + 7);
        var sensitivityThreshold = Math.Clamp(76 - sensitivity, 10, 72);
        var threshold = Math.Max(adaptiveThreshold, sensitivityThreshold);
        var mask = new bool[buffer.Width * buffer.Height];

        for (var y = 0; y < buffer.Height; y++)
        {
            var row = y * buffer.Width;
            var sourceRow = y * buffer.Stride;
            for (var x = 0; x < buffer.Width; x++)
            {
                var offset = sourceRow + x * 4;
                var b = buffer.Pixels[offset];
                var g = buffer.Pixels[offset + 1];
                var r = buffer.Pixels[offset + 2];
                var diff = Difference(r, g, b, background.R, background.G, background.B);
                var lumDiff = Math.Abs(Luminance(r, g, b) - background.Luminance);
                mask[row + x] = Math.Max(diff, lumDiff) >= threshold;
            }
        }

        return mask;
    }

    /// <summary>
    /// Расширяет области маски на один пиксель во все стороны, чтобы разорванные фрагменты лучше соединялись.
    /// </summary>
    private static void Dilate(bool[] mask, int width, int height)
    {
        var source = (bool[])mask.Clone();

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                if (source[index])
                {
                    continue;
                }

                mask[index] =
                    source[index - 1] ||
                    source[index + 1] ||
                    source[index - width] ||
                    source[index + width] ||
                    source[index - width - 1] ||
                    source[index - width + 1] ||
                    source[index + width - 1] ||
                    source[index + width + 1];
            }
        }
    }

    /// <summary>
    /// Закрывает маленькие разрывы в маске, если вокруг пикселя достаточно соседей переднего плана.
    /// </summary>
    private static void FillSmallGaps(bool[] mask, int width, int height)
    {
        var source = (bool[])mask.Clone();

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                if (source[index])
                {
                    continue;
                }

                var neighbors = 0;
                neighbors += source[index - 1] ? 1 : 0;
                neighbors += source[index + 1] ? 1 : 0;
                neighbors += source[index - width] ? 1 : 0;
                neighbors += source[index + width] ? 1 : 0;
                neighbors += source[index - width - 1] ? 1 : 0;
                neighbors += source[index - width + 1] ? 1 : 0;
                neighbors += source[index + width - 1] ? 1 : 0;
                neighbors += source[index + width + 1] ? 1 : 0;
                mask[index] = neighbors >= 5;
            }
        }
    }

    /// <summary>
    /// Находит связные компоненты маски и выбирает главный регион.
    /// Для текста регион собирается из нескольких близких компонентов, чтобы не выбирать только одну строку.
    /// </summary>
    private static ComponentResult? FindMainComponent(bool[] mask, int width, int height)
    {
        var labels = new int[mask.Length];
        var queue = new int[mask.Length];
        var components = new List<ComponentStats>();
        var label = 0;

        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0)
            {
                continue;
            }

            label++;
            var stats = new ComponentStats(label);
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            labels[start] = label;

            while (head < tail)
            {
                var index = queue[head++];
                var x = index % width;
                var y = index / width;
                stats.Add(x, y, width, height);

                TryQueue(index - 1, x > 0);
                TryQueue(index + 1, x < width - 1);
                TryQueue(index - width, y > 0);
                TryQueue(index + width, y < height - 1);
            }

            components.Add(stats);

            void TryQueue(int next, bool valid)
            {
                if (!valid || labels[next] != 0 || !mask[next])
                {
                    return;
                }

                labels[next] = label;
                queue[tail++] = next;
            }
        }

        if (components.Count == 0)
        {
            return null;
        }

        var minArea = Math.Max(30, width * height * 0.0004);
        var minSeedFallbackArea = Math.Max(8, width * height * 0.00002);
        var seedCandidates = components
            .Where(component => component.Area >= minArea)
            .ToArray();
        if (seedCandidates.Length == 0)
        {
            seedCandidates = components
                .Where(component => component.Area >= minSeedFallbackArea)
                .ToArray();
        }

        var best = seedCandidates
            .Where(component => !component.TouchesBorder)
            .OrderByDescending(component => component.Score(width, height))
            .FirstOrDefault() ?? seedCandidates
            .OrderByDescending(component => component.Score(width, height))
            .FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        var selectedLabels = SelectMainRegion(best, components, width, height);
        var regionLabels = new int[labels.Length];
        var regionStats = new ComponentStats(1);

        for (var index = 0; index < labels.Length; index++)
        {
            if (!selectedLabels.Contains(labels[index]))
            {
                continue;
            }

            regionLabels[index] = regionStats.Label;
            regionStats.Add(index % width, index / width, width, height);
        }

        regionStats.Finish();
        return new ComponentResult(regionLabels, regionStats);
    }

    /// <summary>
    /// Расширяет стартовый компонент соседними компонентами, которые выглядят как части того же объекта:
    /// строки текста, слова или соседние фрагменты скана.
    /// </summary>
    private static HashSet<int> SelectMainRegion(ComponentStats seed, IReadOnlyList<ComponentStats> components, int width, int height)
    {
        var selected = new HashSet<int> { seed.Label };
        var bounds = RegionBounds.From(seed);
        var minMemberArea = Math.Max(8, width * height * 0.00002);
        var candidates = components
            .Where(component =>
                !component.TouchesBorder &&
                component.Area >= minMemberArea &&
                component.Area <= width * height * 0.92)
            .ToArray();
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var component in candidates)
            {
                if (selected.Contains(component.Label) || !ShouldJoinRegion(bounds, component, seed, width, height))
                {
                    continue;
                }

                selected.Add(component.Label);
                bounds = bounds.Include(component);
                changed = true;
            }
        }

        return selected;
    }

    /// <summary>
    /// Решает, можно ли присоединить компонент к текущему региону по расстоянию и перекрытию проекций.
    /// </summary>
    private static bool ShouldJoinRegion(RegionBounds bounds, ComponentStats component, ComponentStats seed, int width, int height)
    {
        var horizontalGap = AxisGap(bounds.MinX, bounds.MaxX, component.MinX, component.MaxX);
        var verticalGap = AxisGap(bounds.MinY, bounds.MaxY, component.MinY, component.MaxY);
        var textHeight = Math.Max(seed.BoundingHeight, component.BoundingHeight);
        var verticalLimit = Math.Max(8, Math.Min(height * 0.06, textHeight * 2.4));
        var horizontalLimit = Math.Max(12, Math.Min(width * 0.08, textHeight * 4.0));
        var horizontalOverlap = AxisOverlap(bounds.MinX, bounds.MaxX, component.MinX, component.MaxX);
        var verticalOverlap = AxisOverlap(bounds.MinY, bounds.MaxY, component.MinY, component.MaxY);
        var minComponentWidth = Math.Max(1, Math.Min(bounds.Width, component.BoundingWidth));
        var minComponentHeight = Math.Max(1, Math.Min(bounds.Height, component.BoundingHeight));
        var overlapsTextColumn = horizontalOverlap >= minComponentWidth * 0.15;
        var overlapsTextRow = verticalOverlap >= minComponentHeight * 0.15;

        if (verticalGap <= verticalLimit && overlapsTextColumn)
        {
            return true;
        }

        if (horizontalGap <= horizontalLimit && overlapsTextRow)
        {
            return true;
        }

        return verticalGap <= verticalLimit && horizontalGap <= horizontalLimit;
    }

    /// <summary>
    /// Возвращает расстояние между двумя отрезками на одной оси.
    /// Если отрезки пересекаются, расстояние равно нулю.
    /// </summary>
    private static int AxisGap(int firstMin, int firstMax, int secondMin, int secondMax)
    {
        if (firstMax < secondMin)
        {
            return secondMin - firstMax;
        }

        if (secondMax < firstMin)
        {
            return firstMin - secondMax;
        }

        return 0;
    }

    /// <summary>
    /// Возвращает длину пересечения двух отрезков на одной оси.
    /// </summary>
    private static int AxisOverlap(int firstMin, int firstMax, int secondMin, int secondMax)
    {
        return Math.Max(0, Math.Min(firstMax, secondMax) - Math.Max(firstMin, secondMin) + 1);
    }

    /// <summary>
    /// Выбирает точки границы найденного компонента и масштабирует их обратно к исходному изображению.
    /// </summary>
    private static IReadOnlyList<Point> BuildBoundaryPoints(int[] labels, int label, int width, int height, double analysisScale)
    {
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                if (labels[index] == label && IsBoundary(labels, label, index, width))
                {
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return Array.Empty<Point>();
        }

        var step = Math.Max(1, (int)Math.Ceiling((double)count / MaxBoundaryPoints));
        var points = new List<Point>(Math.Min(count, MaxBoundaryPoints));
        var sourceScale = 1.0 / analysisScale;
        var current = 0;

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x;
                if (labels[index] != label || !IsBoundary(labels, label, index, width))
                {
                    continue;
                }

                if (current % step == 0)
                {
                    points.Add(new Point((x + 0.5) * sourceScale, (y + 0.5) * sourceScale));
                }

                current++;
            }
        }

        return points;
    }

    /// <summary>
    /// Проверяет, находится ли пиксель компонента на внешней границе.
    /// </summary>
    private static bool IsBoundary(int[] labels, int label, int index, int width)
    {
        return labels[index - 1] != label ||
               labels[index + 1] != label ||
               labels[index - width] != label ||
               labels[index + width] != label;
    }

    /// <summary>
    /// Строит ориентированный прямоугольник вокруг компонента по его главному углу.
    /// </summary>
    private static IReadOnlyList<Point> BuildOrientedBox(
        int[] labels,
        ComponentStats stats,
        int width,
        double angleDegrees,
        double analysisScale)
    {
        var angle = angleDegrees * Math.PI / 180.0;
        var ux = Math.Cos(angle);
        var uy = Math.Sin(angle);
        var vx = -uy;
        var vy = ux;
        var centerX = (stats.MinX + stats.MaxX) / 2.0;
        var centerY = (stats.MinY + stats.MaxY) / 2.0;
        var minU = double.MaxValue;
        var maxU = double.MinValue;
        var minV = double.MaxValue;
        var maxV = double.MinValue;

        for (var y = stats.MinY; y <= stats.MaxY; y++)
        {
            var row = y * width;
            for (var x = stats.MinX; x <= stats.MaxX; x++)
            {
                if (labels[row + x] != stats.Label)
                {
                    continue;
                }

                var dx = x - centerX;
                var dy = y - centerY;
                var u = dx * ux + dy * uy;
                var v = dx * vx + dy * vy;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }
        }

        if (minU == double.MaxValue)
        {
            return Array.Empty<Point>();
        }

        var sourceScale = 1.0 / analysisScale;
        return new[]
        {
            ToSource(minU, minV),
            ToSource(maxU, minV),
            ToSource(maxU, maxV),
            ToSource(minU, maxV)
        };

        Point ToSource(double u, double v)
        {
            return new Point(
                (centerX + u * ux + v * vx) * sourceScale,
                (centerY + u * uy + v * vy) * sourceScale);
        }
    }

    /// <summary>
    /// Рисует поверх предпросмотра контур, направляющие, диагонали, центр и рамку обрезки.
    /// </summary>
    private static void DrawAnalysisOverlay(
        DrawingContext context,
        ImageAnalysisResult analysis,
        TransformLayout layout,
        double scale,
        ImageRenderSettings settings)
    {
        var contourBrush = new SolidColorBrush(Color.FromArgb(185, 220, 48, 58));
        var guidePen = new Pen(new SolidColorBrush(Color.FromArgb(190, 42, 115, 211)), Math.Max(1.2, 2.0 * scale));
        var diagonalPen = new Pen(new SolidColorBrush(Color.FromArgb(130, 42, 115, 211)), Math.Max(0.9, 1.4 * scale));
        var centerPen = new Pen(new SolidColorBrush(Color.FromRgb(24, 135, 84)), Math.Max(1.2, 2.0 * scale));
        var cropPen = new Pen(new SolidColorBrush(Color.FromRgb(198, 126, 0)), Math.Max(1.2, 2.0 * scale))
        {
            DashStyle = DashStyles.Dash
        };

        if (settings.ShowContour)
        {
            var radius = Math.Max(0.8, 1.7 * scale);
            foreach (var sourcePoint in analysis.BoundaryPoints)
            {
                var point = layout.ImageMap.Transform(sourcePoint).Scale(scale);
                context.DrawRectangle(contourBrush, null, new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2));
            }
        }

        if (settings.ShowGuideLines && analysis.EdgeBox.Count == 4)
        {
            var box = analysis.EdgeBox.Select(point => layout.ImageMap.Transform(point).Scale(scale)).ToArray();
            for (var i = 0; i < box.Length; i++)
            {
                context.DrawLine(guidePen, box[i], box[(i + 1) % box.Length]);
            }

            var center = layout.ImageMap.Transform(analysis.Center).Scale(scale);
            var angle = (analysis.EdgeAngleDegrees + layout.RotationDegrees) * Math.PI / 180.0;
            var ux = Math.Cos(angle);
            var uy = Math.Sin(angle);
            var lineLength = Math.Max(layout.Width, layout.Height) * scale;
            context.DrawLine(
                guidePen,
                new Point(center.X - ux * lineLength, center.Y - uy * lineLength),
                new Point(center.X + ux * lineLength, center.Y + uy * lineLength));
            context.DrawLine(
                guidePen,
                new Point(center.X + uy * lineLength, center.Y - ux * lineLength),
                new Point(center.X - uy * lineLength, center.Y + ux * lineLength));
        }

        if (settings.ShowDiagonals && analysis.EdgeBox.Count == 4)
        {
            var box = analysis.EdgeBox.Select(point => layout.ImageMap.Transform(point).Scale(scale)).ToArray();
            context.DrawLine(diagonalPen, box[0], box[2]);
            context.DrawLine(diagonalPen, box[1], box[3]);
        }

        var centerPoint = layout.ImageMap.Transform(analysis.Center).Scale(scale);
        var markerSize = Math.Max(7, 10 * scale);
        context.DrawLine(centerPen, new Point(centerPoint.X - markerSize, centerPoint.Y), new Point(centerPoint.X + markerSize, centerPoint.Y));
        context.DrawLine(centerPen, new Point(centerPoint.X, centerPoint.Y - markerSize), new Point(centerPoint.X, centerPoint.Y + markerSize));

        if (settings.ShowCropFrame || settings.AutoCrop)
        {
            var cropRect = GetCropRect(analysis, layout, settings.CropMarginPixels).Scale(scale);
            context.DrawRectangle(null, cropPen, cropRect);
        }
    }

    /// <summary>
    /// Вычисляет холст и матрицу преобразования изображения после поворота и опциональной центровки.
    /// </summary>
    private static TransformLayout BuildLayout(BitmapSource source, ImageAnalysisResult? analysis, ImageRenderSettings settings)
    {
        var rotated = AffineMap.Rotation(settings.RotationDegrees, source.PixelWidth / 2.0, source.PixelHeight / 2.0);
        var corners = new[]
        {
            new Point(0, 0),
            new Point(source.PixelWidth, 0),
            new Point(source.PixelWidth, source.PixelHeight),
            new Point(0, source.PixelHeight)
        }.Select(rotated.Transform).ToArray();
        var minX = corners.Min(point => point.X);
        var minY = corners.Min(point => point.Y);
        var maxX = corners.Max(point => point.X);
        var maxY = corners.Max(point => point.Y);
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var map = rotated.Translate(-minX, -minY);

        if (settings.CenterSelection && analysis?.Found == true)
        {
            var center = map.Transform(analysis.Center);
            map = map.Translate(width / 2.0 - center.X, height / 2.0 - center.Y);
        }

        return new TransformLayout(width, height, map, settings.RotationDegrees);
    }

    /// <summary>
    /// Вычисляет прямоугольник автообрезки по трансформированным точкам найденного контура.
    /// </summary>
    private static Rect GetCropRect(ImageAnalysisResult analysis, TransformLayout layout, int margin)
    {
        var points = analysis.BoundaryPoints.Count > 0
            ? analysis.BoundaryPoints
            : new[]
            {
                analysis.BoundingBox.TopLeft,
                analysis.BoundingBox.TopRight,
                analysis.BoundingBox.BottomRight,
                analysis.BoundingBox.BottomLeft
            };
        var transformed = points.Select(point => layout.ImageMap.Transform(point)).ToArray();
        var minX = transformed.Min(point => point.X);
        var minY = transformed.Min(point => point.Y);
        var maxX = transformed.Max(point => point.X);
        var maxY = transformed.Max(point => point.Y);
        var rect = new Rect(new Point(minX, minY), new Point(maxX, maxY));
        rect.Inflate(Math.Max(0, margin), Math.Max(0, margin));
        return rect;
    }

    /// <summary>
    /// Возвращает пустой результат, если на изображении не удалось найти значимый контур.
    /// </summary>
    private static ImageAnalysisResult EmptyResult(BitmapSource source)
    {
        return new ImageAnalysisResult
        {
            Found = false,
            SourceWidth = source.PixelWidth,
            SourceHeight = source.PixelHeight,
            BoundingBox = new Rect(0, 0, source.PixelWidth, source.PixelHeight),
            Center = new Point(source.PixelWidth / 2.0, source.PixelHeight / 2.0),
            EdgeAngleDegrees = 0,
            CoveragePercent = 0,
            BoundaryPoints = Array.Empty<Point>(),
            EdgeBox = Array.Empty<Point>()
        };
    }

    /// <summary>
    /// Приводит угол найденной стороны к диапазону -45..45 градусов,
    /// потому что для выравнивания важен наклон ближайшей горизонтали/вертикали.
    /// </summary>
    private static double NormalizeEdgeAngle(double angle)
    {
        while (angle <= -45)
        {
            angle += 90;
        }

        while (angle > 45)
        {
            angle -= 90;
        }

        return angle;
    }

    /// <summary>
    /// Считает яркость RGB-пикселя по стандартным весам каналов.
    /// </summary>
    private static double Luminance(double r, double g, double b)
    {
        return r * 0.299 + g * 0.587 + b * 0.114;
    }

    /// <summary>
    /// Считает взвешенное цветовое отличие пикселя от оцененного цвета фона.
    /// </summary>
    private static double Difference(double r, double g, double b, double backgroundR, double backgroundG, double backgroundB)
    {
        var dr = r - backgroundR;
        var dg = g - backgroundG;
        var db = b - backgroundB;
        return Math.Sqrt(dr * dr * 0.299 + dg * dg * 0.587 + db * db * 0.114);
    }

    /// <summary>
    /// Возвращает медиану массива байтов; используется для устойчивой оценки цвета фона.
    /// </summary>
    private static byte Median(byte[] values)
    {
        if (values.Length == 0)
        {
            return 255;
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }

    /// <summary>
    /// Описание оцененного фона: цвет, яркость и уровень шума по краям изображения.
    /// </summary>
    private readonly record struct BackgroundColor(byte R, byte G, byte B, double Luminance, double NoiseMean, double NoiseStdDev);

    /// <summary>
    /// RGB-сэмпл одного пикселя, взятого из изображения.
    /// </summary>
    private readonly record struct ColorSample(byte R, byte G, byte B);

    /// <summary>
    /// Упрощенный доступ к пикселям BitmapSource в формате BGRA32.
    /// </summary>
    private sealed class PixelBuffer
    {
        /// <summary>
        /// Ширина буфера в пикселях.
        /// </summary>
        public required int Width { get; init; }

        /// <summary>
        /// Высота буфера в пикселях.
        /// </summary>
        public required int Height { get; init; }

        /// <summary>
        /// Количество байтов в одной строке изображения.
        /// </summary>
        public required int Stride { get; init; }

        /// <summary>
        /// Сырые байты изображения в порядке каналов BGRA.
        /// </summary>
        public required byte[] Pixels { get; init; }

        /// <summary>
        /// Создает пиксельный буфер из BitmapSource и при необходимости конвертирует формат.
        /// </summary>
        public static PixelBuffer FromBitmap(BitmapSource bitmap)
        {
            BitmapSource source = bitmap.Format == PixelFormats.Bgra32
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            var stride = source.PixelWidth * 4;
            var pixels = new byte[stride * source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            return new PixelBuffer
            {
                Width = source.PixelWidth,
                Height = source.PixelHeight,
                Stride = stride,
                Pixels = pixels
            };
        }

        /// <summary>
        /// Возвращает RGB-сэмпл пикселя с защитой координат от выхода за границы.
        /// </summary>
        public ColorSample GetSample(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            var offset = y * Stride + x * 4;
            return new ColorSample(Pixels[offset + 2], Pixels[offset + 1], Pixels[offset]);
        }
    }

    /// <summary>
    /// Связка карты меток и статистики выбранного компонента.
    /// </summary>
    private sealed record ComponentResult(int[] Labels, ComponentStats Stats);

    /// <summary>
    /// Накопленная статистика связного компонента: площадь, границы, центр масс и главный угол.
    /// </summary>
    private sealed class ComponentStats
    {
        /// <summary>
        /// Создает статистику для компонента с указанной числовой меткой.
        /// </summary>
        public ComponentStats(int label)
        {
            Label = label;
        }

        /// <summary>
        /// Числовая метка компонента в карте связных областей.
        /// </summary>
        public int Label { get; }

        /// <summary>
        /// Количество пикселей в компоненте.
        /// </summary>
        public int Area { get; private set; }

        /// <summary>
        /// Минимальная координата X компонента.
        /// </summary>
        public int MinX { get; private set; } = int.MaxValue;

        /// <summary>
        /// Максимальная координата X компонента.
        /// </summary>
        public int MaxX { get; private set; } = int.MinValue;

        /// <summary>
        /// Минимальная координата Y компонента.
        /// </summary>
        public int MinY { get; private set; } = int.MaxValue;

        /// <summary>
        /// Максимальная координата Y компонента.
        /// </summary>
        public int MaxY { get; private set; } = int.MinValue;

        /// <summary>
        /// Главный угол компонента, вычисленный по ковариации пикселей.
        /// </summary>
        public double PrincipalAngleDegrees { get; private set; }

        /// <summary>
        /// Ширина ограничивающего прямоугольника компонента.
        /// </summary>
        public int BoundingWidth => MaxX - MinX + 1;

        /// <summary>
        /// Высота ограничивающего прямоугольника компонента.
        /// </summary>
        public int BoundingHeight => MaxY - MinY + 1;

        private double SumX { get; set; }

        private double SumY { get; set; }

        private double SumXX { get; set; }

        private double SumYY { get; set; }

        private double SumXY { get; set; }

        /// <summary>
        /// Показывает, касается ли компонент края изображения; такие компоненты часто являются рамками или фоном.
        /// </summary>
        public bool TouchesBorder { get; private set; }

        /// <summary>
        /// Добавляет пиксель в статистику компонента.
        /// </summary>
        public void Add(int x, int y, int width, int height)
        {
            Area++;
            MinX = Math.Min(MinX, x);
            MaxX = Math.Max(MaxX, x);
            MinY = Math.Min(MinY, y);
            MaxY = Math.Max(MaxY, y);
            SumX += x;
            SumY += y;
            SumXX += x * x;
            SumYY += y * y;
            SumXY += x * y;
            TouchesBorder |= x <= 1 || y <= 1 || x >= width - 2 || y >= height - 2;
        }

        /// <summary>
        /// Завершает расчет статистики и вычисляет главный угол компонента.
        /// </summary>
        public void Finish()
        {
            if (Area <= 1)
            {
                PrincipalAngleDegrees = 0;
                return;
            }

            var meanX = SumX / Area;
            var meanY = SumY / Area;
            var covXX = SumXX / Area - meanX * meanX;
            var covYY = SumYY / Area - meanY * meanY;
            var covXY = SumXY / Area - meanX * meanY;
            PrincipalAngleDegrees = 0.5 * Math.Atan2(2 * covXY, covXX - covYY) * 180.0 / Math.PI;
        }

        /// <summary>
        /// Возвращает эвристическую оценку компонента для выбора главной области изображения.
        /// </summary>
        public double Score(int width, int height)
        {
            var boundingArea = Math.Max(1, (MaxX - MinX + 1) * (MaxY - MinY + 1));
            var fillRatio = Math.Clamp((double)Area / boundingArea, 0.05, 1.0);
            var score = Area * (0.7 + fillRatio * 0.3);
            var imageArea = width * height;
            if (Area > imageArea * 0.92)
            {
                score *= 0.1;
            }

            if (TouchesBorder)
            {
                score *= 0.35;
            }

            return score;
        }
    }

    /// <summary>
    /// Границы текущей группы компонентов, которые считаются одним найденным объектом.
    /// </summary>
    private readonly record struct RegionBounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        /// <summary>
        /// Ширина региона.
        /// </summary>
        public int Width => MaxX - MinX + 1;

        /// <summary>
        /// Высота региона.
        /// </summary>
        public int Height => MaxY - MinY + 1;

        /// <summary>
        /// Создает границы региона из одного компонента.
        /// </summary>
        public static RegionBounds From(ComponentStats stats)
        {
            return new RegionBounds(stats.MinX, stats.MinY, stats.MaxX, stats.MaxY);
        }

        /// <summary>
        /// Возвращает новые границы, расширенные еще одним компонентом.
        /// </summary>
        public RegionBounds Include(ComponentStats stats)
        {
            return new RegionBounds(
                Math.Min(MinX, stats.MinX),
                Math.Min(MinY, stats.MinY),
                Math.Max(MaxX, stats.MaxX),
                Math.Max(MaxY, stats.MaxY));
        }
    }

    /// <summary>
    /// Описание холста после преобразований и матрицы, которая переводит исходные координаты в координаты холста.
    /// </summary>
    private readonly record struct TransformLayout(double Width, double Height, AffineMap ImageMap, double RotationDegrees);

    /// <summary>
    /// Компактное представление аффинного преобразования: поворот, перенос и масштабирование.
    /// </summary>
    private readonly record struct AffineMap(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
    {
        /// <summary>
        /// Создает преобразование поворота вокруг заданного центра.
        /// </summary>
        public static AffineMap Rotation(double angleDegrees, double centerX, double centerY)
        {
            var angle = angleDegrees * Math.PI / 180.0;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            return new AffineMap(
                cos,
                sin,
                -sin,
                cos,
                centerX - cos * centerX + sin * centerY,
                centerY - sin * centerX - cos * centerY);
        }

        /// <summary>
        /// Применяет преобразование к точке.
        /// </summary>
        public Point Transform(Point point)
        {
            return new Point(
                point.X * M11 + point.Y * M21 + OffsetX,
                point.X * M12 + point.Y * M22 + OffsetY);
        }

        /// <summary>
        /// Добавляет перенос к текущему преобразованию.
        /// </summary>
        public AffineMap Translate(double x, double y)
        {
            return this with
            {
                OffsetX = OffsetX + x,
                OffsetY = OffsetY + y
            };
        }

        /// <summary>
        /// Масштабирует все коэффициенты преобразования.
        /// </summary>
        public AffineMap Scale(double scale)
        {
            return new AffineMap(
                M11 * scale,
                M12 * scale,
                M21 * scale,
                M22 * scale,
                OffsetX * scale,
                OffsetY * scale);
        }

        /// <summary>
        /// Преобразует собственный формат матрицы в WPF Matrix.
        /// </summary>
        public Matrix ToMatrix()
        {
            return new Matrix(M11, M12, M21, M22, OffsetX, OffsetY);
        }
    }
}

/// <summary>
/// Небольшие расширения геометрии для масштабирования точек и прямоугольников.
/// </summary>
internal static class GeometryExtensions
{
    /// <summary>
    /// Умножает координаты точки на коэффициент масштаба.
    /// </summary>
    public static Point Scale(this Point point, double scale)
    {
        return new Point(point.X * scale, point.Y * scale);
    }

    /// <summary>
    /// Умножает положение и размер прямоугольника на коэффициент масштаба.
    /// </summary>
    public static Rect Scale(this Rect rect, double scale)
    {
        return new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }
}
