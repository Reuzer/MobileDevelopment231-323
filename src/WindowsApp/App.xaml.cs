using System.Windows;

namespace WindowsApp;

public partial class App : Application
{
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
