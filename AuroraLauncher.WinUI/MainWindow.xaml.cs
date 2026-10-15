using AuroraLauncher.Pages;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AuroraLauncher;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        ApplyLocalization();
        AuroraLauncher.Core.I18n.LanguageChanged += () => DispatcherQueue.TryEnqueue(ApplyLocalization);
    }

    public void ApplyLocalization()
    {
        Title = AuroraLauncher.Core.I18n.T("Nav.AppTitle");
        NavHome.Content = AuroraLauncher.Core.I18n.T("Nav.Home");
        ToolTipService.SetToolTip(NavHome, AuroraLauncher.Core.I18n.T("Nav.Home"));
        NavInstances.Content = AuroraLauncher.Core.I18n.T("Nav.Instances");
        ToolTipService.SetToolTip(NavInstances, AuroraLauncher.Core.I18n.T("Nav.Instances"));
        NavMods.Content = AuroraLauncher.Core.I18n.T("Nav.Mods");
        ToolTipService.SetToolTip(NavMods, AuroraLauncher.Core.I18n.T("Nav.Mods"));
        NavJava.Content = AuroraLauncher.Core.I18n.T("Nav.Java");
        ToolTipService.SetToolTip(NavJava, AuroraLauncher.Core.I18n.T("Nav.Java"));
        NavAccounts.Content = AuroraLauncher.Core.I18n.T("Nav.Accounts");
        ToolTipService.SetToolTip(NavAccounts, AuroraLauncher.Core.I18n.T("Nav.Accounts"));

        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = AuroraLauncher.Core.I18n.T("Nav.Settings");
            ToolTipService.SetToolTip(settingsItem, AuroraLauncher.Core.I18n.T("Nav.Settings"));
        }
    }

    private void ConfigureWindow()
    {
        Title = "Aurora Launcher";

        // Задаём оптимальный начальный размер окна
        var appWindow = AppWindow;
        if (appWindow != null)
        {
            appWindow.Resize(new SizeInt32(1160, 780));

            // Центрируем окно на основном мониторе
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var centeredPosition = new PointInt32(
                    (displayArea.WorkArea.Width - 1160) / 2,
                    (displayArea.WorkArea.Height - 780) / 2
                );
                appWindow.Move(centeredPosition);
            }
        }

        // Современная кастомизация TitleBar в стиле WinUI 3
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyTheme();
    }

    public void ApplyTheme(string? theme = null)
    {
        theme ??= AuroraLauncher.Core.Ctx.Settings.AppTheme;
        if (Content is FrameworkElement rootElement)
        {
            rootElement.RequestedTheme = theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
        }
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        NavView.SelectedItem = NavHome;
        ContentFrame.Navigate(typeof(HomePage));

        // Подписка на краш игры: автоматическое открытие интеллектуального окна диагностики с логами
        AuroraLauncher.Core.GameLauncher.GameCrashed += (crashInfo, inst) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var crashWindow = new CrashWindow(crashInfo, inst);
                crashWindow.Activate();
            });
        };

        // Если включен режим отладки — открываем живую консоль
        if (AuroraLauncher.Core.Ctx.Settings.DebugMode)
        {
            DebugConsoleWindow.ShowWindow();
        }

        // Фоновое автоматическое обновление токена и подтягивание лицензии при запуске лаунчера
        _ = Task.Run(async () =>
        {
            try
            {
                if (AuroraLauncher.Core.Ctx.Account != null && AuroraLauncher.Core.Ctx.Account.IsOffline)
                {
                    AuroraLauncher.Core.Ctx.Account = null;
                    AuroraLauncher.Core.Accounts.Delete();
                }

                if (AuroraLauncher.Core.Ctx.Account == null)
                {
                    var imported = AuroraLauncher.Core.ExternalLaunchersImporter.TryImportBest();
                    if (imported != null)
                    {
                        AuroraLauncher.Core.Ctx.Account = imported;
                        DispatcherQueue.TryEnqueue(() => LauncherState.NotifyAccountChanged());
                    }
                }
                else
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    await AuroraLauncher.Core.Auth.EnsureFreshMcTokenAsync(cts.Token);
                    AuroraLauncher.Core.Ctx.Log("[Startup] Токен активного аккаунта успешно обновлен.");
                    DispatcherQueue.TryEnqueue(() => LauncherState.NotifyAccountChanged());
                }
            }
            catch (Exception ex)
            {
                AuroraLauncher.Core.Ctx.Log($"[Startup] Фоновое обновление токена: {ex.Message}");
            }
        });
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            NavigateByTag(tag);
        }
    }

    private void NavigateByTag(string? tag)
    {
        switch (tag)
        {
            case "home":
                ContentFrame.Navigate(typeof(HomePage));
                break;
            case "instances":
                ContentFrame.Navigate(typeof(InstancesPage));
                break;
            case "mods":
                ContentFrame.Navigate(typeof(ModsPage));
                break;
            case "java":
                ContentFrame.Navigate(typeof(JavaPage));
                break;
            case "accounts":
                ContentFrame.Navigate(typeof(AccountsPage));
                break;
            case "settings":
                ContentFrame.Navigate(typeof(SettingsPage));
                break;
        }
    }

    public void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "home":
                NavView.SelectedItem = NavHome;
                break;
            case "instances":
                NavView.SelectedItem = NavInstances;
                break;
            case "mods":
                NavView.SelectedItem = NavMods;
                break;
            case "java":
                NavView.SelectedItem = NavJava;
                break;
            case "accounts":
                NavView.SelectedItem = NavAccounts;
                break;
            case "settings":
                NavView.SelectedItem = NavView.SettingsItem;
                break;
        }
        NavigateByTag(tag);
    }
}
