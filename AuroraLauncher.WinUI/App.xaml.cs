using AuroraLauncher.Core;
using Microsoft.UI.Xaml;

namespace AuroraLauncher;

public partial class App : Application
{
    private Window? _window;
    public static Window? CurrentWindow => (Current as App)?._window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Ctx.Log("WinUI exception: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Ctx.Init();
        Ctx.Log("Aurora Launcher WinUI запущен");
        _window = new MainWindow();
        Ctx.Log("Главное окно создано");
        _window.Activate();
        Ctx.Log("Главное окно активировано");
    }
}
