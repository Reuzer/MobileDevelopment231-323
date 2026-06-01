using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WindowsApp;

public partial class MainWindow : Window
{
    private BitmapSource? _sourceImage;
    private ImageAnalysisResult? _analysis;
    private string? _currentPath;
    private bool _syncingControls;
    private double _rotationDegrees;

    public MainWindow()
    {
        InitializeComponent();
        RefreshCommandState();
    }

    public async Task LoadImageAsync(string path)
    {
        if (!File.Exists(path))
        {
            SetStatus($"Файл не найден: {path}");
            return;
        }

        try
        {
            SetStatus("Загрузка изображения...");
            _sourceImage = ImageProcessingService.LoadBitmap(path);
            _currentPath = path;
            _analysis = null;
            SetRotation(0);
            ImageInfoTextBlock.Text = $"{Path.GetFileName(path)} | {_sourceImage.PixelWidth} x {_sourceImage.PixelHeight}px";
            AnalysisInfoTextBlock.Text = string.Empty;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            Title = $"Scan Align - {Path.GetFileName(path)}";
            RefreshCommandState();
            RefreshPreview();
            await AnalyzeCurrentImageAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось открыть изображение: {ex.Message}");
        }
    }

    private async Task AnalyzeCurrentImageAsync()
    {
        if (_sourceImage is null)
        {
            return;
        }

        try
        {
            SetBusy(true, "Поиск контура...");
            var sensitivity = (int)Math.Round(SensitivitySlider.Value);
            var source = _sourceImage;
            var analysis = await Task.Run(() => ImageProcessingService.Analyze(source, sensitivity));
            _analysis = analysis;

            if (analysis.Found)
            {
                AnalysisInfoTextBlock.Text =
                    $"Контур: {analysis.BoundingBox.Width:0} x {analysis.BoundingBox.Height:0}px | " +
                    $"угол {analysis.EdgeAngleDegrees:0.###}° | площадь {analysis.CoveragePercent:0.##}%";
                SetStatus("Контур найден. Можно выровнять изображение или сохранить результат.");
            }
            else
            {
                AnalysisInfoTextBlock.Text = "Контур не найден. Попробуйте изменить чувствительность.";
                SetStatus("Контур не найден.");
            }

            RefreshCommandState();
            RefreshPreview();
        }
        catch (Exception ex)
        {
            SetStatus($"Ошибка анализа: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshPreview()
    {
        if (_sourceImage is null)
        {
            PreviewImage.Source = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            PreviewImage.Source = ImageProcessingService.RenderPreview(_sourceImage, _analysis, CreateRenderSettings());
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось построить предпросмотр: {ex.Message}");
        }
    }

    private ImageRenderSettings CreateRenderSettings()
    {
        return new ImageRenderSettings
        {
            RotationDegrees = _rotationDegrees,
            CenterSelection = CenterSelectionCheckBox.IsChecked == true,
            AutoCrop = AutoCropCheckBox.IsChecked == true,
            CropMarginPixels = GetCropMargin(),
            ShowContour = ShowContourCheckBox.IsChecked == true,
            ShowGuideLines = ShowGuidesCheckBox.IsChecked == true,
            ShowDiagonals = ShowDiagonalsCheckBox.IsChecked == true,
            ShowCropFrame = ShowCropFrameCheckBox.IsChecked == true,
            MaxPreviewDimension = 1800
        };
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть изображение",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadImageAsync(dialog.FileName);
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeCurrentImageAsync();
    }

    private void AlignButton_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis?.Found != true)
        {
            SetStatus("Сначала нужно найти контур.");
            return;
        }

        SetRotation(NormalizeRotation(_rotationDegrees - _analysis.EdgeAngleDegrees));
        SetStatus($"Поворот скорректирован на {-_analysis.EdgeAngleDegrees:0.###}°.");
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        SetRotation(NormalizeRotation(_rotationDegrees - 90));
    }

    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        SetRotation(NormalizeRotation(_rotationDegrees + 90));
    }

    private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _syncingControls)
        {
            return;
        }

        SetRotation(e.NewValue, updateSlider: false);
    }

    private void RotationTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyRotationTextBox();
    }

    private void RotationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyRotationTextBox();
            e.Handled = true;
        }
    }

    private void CropMarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _syncingControls)
        {
            return;
        }

        _syncingControls = true;
        CropMarginTextBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        _syncingControls = false;
        RefreshPreview();
    }

    private void CropMarginTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyCropMarginTextBox();
    }

    private void CropMarginTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCropMarginTextBox();
            e.Handled = true;
        }
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _sourceImage is null)
        {
            return;
        }

        SetStatus("Чувствительность изменена. Нажмите Ctrl+R или кнопку поиска контура для повторного анализа.");
    }

    private void OverlayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshPreview();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceImage is null)
        {
            return;
        }

        var initialName = _currentPath is null
            ? "aligned.png"
            : $"{Path.GetFileNameWithoutExtension(_currentPath)}_aligned.png";
        var dialog = new SaveFileDialog
        {
            Title = "Сохранить результат",
            FileName = initialName,
            Filter = "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp|TIFF image|*.tif"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetBusy(true, "Формирование результата...");
            var bitmap = ImageProcessingService.RenderResult(_sourceImage, _analysis, CreateRenderSettings());
            ImageProcessingService.SaveBitmap(bitmap, dialog.FileName);
            SetStatus($"Сохранено: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Не удалось сохранить результат: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.O)
        {
            OpenButton_Click(OpenButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.S && SaveButton.IsEnabled)
        {
            SaveButton_Click(SaveButton, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.R && AnalyzeButton.IsEnabled)
        {
            AnalyzeButton_Click(AnalyzeButton, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ApplyRotationTextBox()
    {
        if (double.TryParse(
                RotationTextBox.Text.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            SetRotation(NormalizeRotation(value));
        }
        else
        {
            RotationTextBox.Text = ImageProcessingService.FormatAngle(_rotationDegrees);
        }
    }

    private void ApplyCropMarginTextBox()
    {
        if (int.TryParse(CropMarginTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            value = Math.Clamp(value, (int)CropMarginSlider.Minimum, (int)CropMarginSlider.Maximum);
            _syncingControls = true;
            CropMarginSlider.Value = value;
            CropMarginTextBox.Text = value.ToString(CultureInfo.InvariantCulture);
            _syncingControls = false;
            RefreshPreview();
        }
        else
        {
            CropMarginTextBox.Text = GetCropMargin().ToString(CultureInfo.InvariantCulture);
        }
    }

    private int GetCropMargin()
    {
        return (int)Math.Round(CropMarginSlider.Value);
    }

    private void SetRotation(double value, bool updateSlider = true)
    {
        _rotationDegrees = value;
        _syncingControls = true;
        RotationTextBox.Text = ImageProcessingService.FormatAngle(value);
        if (updateSlider)
        {
            RotationSlider.Value = Math.Clamp(value, RotationSlider.Minimum, RotationSlider.Maximum);
        }
        _syncingControls = false;
        RefreshPreview();
    }

    private void RefreshCommandState()
    {
        var hasImage = _sourceImage is not null;
        var hasContour = _analysis?.Found == true;
        SaveButton.IsEnabled = hasImage;
        AnalyzeButton.IsEnabled = hasImage;
        AlignButton.IsEnabled = hasContour;
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        OpenButton.IsEnabled = !isBusy;
        SaveButton.IsEnabled = !isBusy && _sourceImage is not null;
        AnalyzeButton.IsEnabled = !isBusy && _sourceImage is not null;
        AlignButton.IsEnabled = !isBusy && _analysis?.Found == true;
        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    private static double NormalizeRotation(double angle)
    {
        while (angle <= -180)
        {
            angle += 360;
        }

        while (angle > 180)
        {
            angle -= 360;
        }

        return angle;
    }
}
