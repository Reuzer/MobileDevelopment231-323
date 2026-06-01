using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace WindowsApp;

public static class ImageProcessingService
{
    private const int AnalysisMaxDimension = 1400;
    private const int MaxBoundaryPoints = 14000;
    private const int WebpQuality = 90;

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

    private static bool IsWebpPath(string path)
    {
        return string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatAngle(double angle)
    {
        return angle.ToString("0.###", CultureInfo.InvariantCulture);
    }

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
        var best = components
            .Where(component => component.Area >= minArea)
            .OrderByDescending(component => component.Score(width, height))
            .FirstOrDefault();

        if (best is null)
        {
            return null;
        }

        best.Finish();
        return new ComponentResult(labels, best);
    }

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

    private static bool IsBoundary(int[] labels, int label, int index, int width)
    {
        return labels[index - 1] != label ||
               labels[index + 1] != label ||
               labels[index - width] != label ||
               labels[index + width] != label;
    }

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

    private static double Luminance(double r, double g, double b)
    {
        return r * 0.299 + g * 0.587 + b * 0.114;
    }

    private static double Difference(double r, double g, double b, double backgroundR, double backgroundG, double backgroundB)
    {
        var dr = r - backgroundR;
        var dg = g - backgroundG;
        var db = b - backgroundB;
        return Math.Sqrt(dr * dr * 0.299 + dg * dg * 0.587 + db * db * 0.114);
    }

    private static byte Median(byte[] values)
    {
        if (values.Length == 0)
        {
            return 255;
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }

    private readonly record struct BackgroundColor(byte R, byte G, byte B, double Luminance, double NoiseMean, double NoiseStdDev);

    private readonly record struct ColorSample(byte R, byte G, byte B);

    private sealed class PixelBuffer
    {
        public required int Width { get; init; }

        public required int Height { get; init; }

        public required int Stride { get; init; }

        public required byte[] Pixels { get; init; }

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

        public ColorSample GetSample(int x, int y)
        {
            x = Math.Clamp(x, 0, Width - 1);
            y = Math.Clamp(y, 0, Height - 1);
            var offset = y * Stride + x * 4;
            return new ColorSample(Pixels[offset + 2], Pixels[offset + 1], Pixels[offset]);
        }
    }

    private sealed record ComponentResult(int[] Labels, ComponentStats Stats);

    private sealed class ComponentStats
    {
        public ComponentStats(int label)
        {
            Label = label;
        }

        public int Label { get; }

        public int Area { get; private set; }

        public int MinX { get; private set; } = int.MaxValue;

        public int MaxX { get; private set; } = int.MinValue;

        public int MinY { get; private set; } = int.MaxValue;

        public int MaxY { get; private set; } = int.MinValue;

        public double PrincipalAngleDegrees { get; private set; }

        private double SumX { get; set; }

        private double SumY { get; set; }

        private double SumXX { get; set; }

        private double SumYY { get; set; }

        private double SumXY { get; set; }

        private bool TouchesBorder { get; set; }

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

    private readonly record struct TransformLayout(double Width, double Height, AffineMap ImageMap, double RotationDegrees);

    private readonly record struct AffineMap(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
    {
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

        public Point Transform(Point point)
        {
            return new Point(
                point.X * M11 + point.Y * M21 + OffsetX,
                point.X * M12 + point.Y * M22 + OffsetY);
        }

        public AffineMap Translate(double x, double y)
        {
            return this with
            {
                OffsetX = OffsetX + x,
                OffsetY = OffsetY + y
            };
        }

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

        public Matrix ToMatrix()
        {
            return new Matrix(M11, M12, M21, M22, OffsetX, OffsetY);
        }
    }
}

internal static class GeometryExtensions
{
    public static Point Scale(this Point point, double scale)
    {
        return new Point(point.X * scale, point.Y * scale);
    }

    public static Rect Scale(this Rect rect, double scale)
    {
        return new Rect(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }
}
