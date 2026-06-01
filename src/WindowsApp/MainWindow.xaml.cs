using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WindowsApp;

/// <summary>
/// Главное окно приложения: связывает элементы интерфейса с загрузкой, анализом,
/// поворотом, предпросмотром и сохранением изображения.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Исходное изображение, загруженное пользователем.
    /// </summary>
    private BitmapSource? _sourceImage;

    /// <summary>
    /// Последний результат поиска контура для текущего изображения.
    /// </summary>
    private ImageAnalysisResult? _analysis;

    /// <summary>
    /// Путь к текущему файлу, нужен для заголовка окна и имени файла при сохранении.
    /// </summary>
    private string? _currentPath;

    /// <summary>
    /// Защищает связанные Slider/TextBox от рекурсивного обновления друг друга.
    /// </summary>
    private bool _syncingControls;

    /// <summary>
    /// Текущий пользовательский угол поворота в градусах.
    /// </summary>
    private double _rotationDegrees;

    /// <summary>
    /// Инициализирует окно и выставляет начальное состояние кнопок.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        RefreshCommandState();
    }

    /// <summary>
    /// Загружает изображение из файла, сбрасывает предыдущий анализ и сразу запускает новый поиск контура.
    /// </summary>
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

    /// <summary>
    /// Выполняет анализ текущего изображения в фоновом потоке, чтобы не блокировать интерфейс.
    /// </summary>
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

    /// <summary>
    /// Перестраивает картинку предпросмотра с учетом текущего угла, обрезки и выбранных подсветок.
    /// </summary>
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

    /// <summary>
    /// Собирает настройки отрисовки из текущего состояния элементов управления.
    /// </summary>
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

    /// <summary>
    /// Обрабатывает кнопку открытия файла и передает выбранный путь в загрузчик изображения.
    /// </summary>
    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Открыть изображение",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.webp|All files|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadImageAsync(dialog.FileName);
        }
    }

    /// <summary>
    /// Повторно запускает поиск контура для уже загруженного изображения.
    /// </summary>
    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        await AnalyzeCurrentImageAsync();
    }

    /// <summary>
    /// Корректирует ручной поворот на угол найденной грани, чтобы выровнять изображение.
    /// </summary>
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

    /// <summary>
    /// Поворачивает изображение на 90 градусов против часовой стрелки.
    /// </summary>
    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        SetRotation(NormalizeRotation(_rotationDegrees - 90));
    }

    /// <summary>
    /// Поворачивает изображение на 90 градусов по часовой стрелке.
    /// </summary>
    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        SetRotation(NormalizeRotation(_rotationDegrees + 90));
    }

    /// <summary>
    /// Применяет новый угол поворота при перемещении ползунка.
    /// </summary>
    private void RotationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _syncingControls)
        {
            return;
        }

        SetRotation(e.NewValue, updateSlider: false);
    }

    /// <summary>
    /// Применяет угол из текстового поля после потери фокуса.
    /// </summary>
    private void RotationTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyRotationTextBox();
    }

    /// <summary>
    /// Применяет угол из текстового поля по клавише Enter.
    /// </summary>
    private void RotationTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyRotationTextBox();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Синхронизирует текстовое поле отступа обрезки с ползунком и обновляет предпросмотр.
    /// </summary>
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

    /// <summary>
    /// Применяет отступ обрезки из текстового поля после потери фокуса.
    /// </summary>
    private void CropMarginTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyCropMarginTextBox();
    }

    /// <summary>
    /// Применяет отступ обрезки из текстового поля по клавише Enter.
    /// </summary>
    private void CropMarginTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyCropMarginTextBox();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Сообщает пользователю, что после изменения чувствительности нужно повторить анализ.
    /// </summary>
    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _sourceImage is null)
        {
            return;
        }

        SetStatus("Чувствительность изменена. Нажмите Ctrl+R или кнопку поиска контура для повторного анализа.");
    }

    /// <summary>
    /// Обновляет предпросмотр при переключении подсветки, центровки или автообрезки.
    /// </summary>
    private void OverlayOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        RefreshPreview();
    }

    /// <summary>
    /// Формирует итоговое изображение по текущим настройкам и сохраняет его в выбранный файл.
    /// </summary>
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
            Filter = "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp|TIFF image|*.tif|WebP image|*.webp"
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

    /// <summary>
    /// Обрабатывает горячие клавиши: Ctrl+O для открытия, Ctrl+S для сохранения, Ctrl+R для анализа.
    /// </summary>
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

    /// <summary>
    /// Проверяет и применяет число из поля ручного поворота.
    /// </summary>
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

    /// <summary>
    /// Проверяет и применяет число из поля отступа автообрезки.
    /// </summary>
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

    /// <summary>
    /// Возвращает текущий отступ автообрезки в пикселях.
    /// </summary>
    private int GetCropMargin()
    {
        return (int)Math.Round(CropMarginSlider.Value);
    }

    /// <summary>
    /// Устанавливает угол поворота, синхронизирует элементы управления и обновляет предпросмотр.
    /// </summary>
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

    /// <summary>
    /// Включает и отключает кнопки в зависимости от того, есть ли изображение и найденный контур.
    /// </summary>
    private void RefreshCommandState()
    {
        var hasImage = _sourceImage is not null;
        var hasContour = _analysis?.Found == true;
        SaveButton.IsEnabled = hasImage;
        AnalyzeButton.IsEnabled = hasImage;
        AlignButton.IsEnabled = hasContour;
    }

    /// <summary>
    /// Переводит интерфейс в режим занятости во время анализа или сохранения.
    /// </summary>
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

    /// <summary>
    /// Выводит сообщение в нижнюю строку состояния.
    /// </summary>
    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    /// <summary>
    /// Приводит угол к диапазону от -180 до 180 градусов.
    /// </summary>
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
