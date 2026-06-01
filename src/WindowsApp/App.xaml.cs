using System.Windows;

namespace WindowsApp;

/// <summary>
/// WPF-приложение: создает главное окно и передает ему аргументы командной строки.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Запускает главное окно. Если первым аргументом передан путь к файлу,
    /// сразу открывает это изображение для анализа.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            _ = mainWindow.LoadImageAsync(e.Args[0]);
        }
    }
}
