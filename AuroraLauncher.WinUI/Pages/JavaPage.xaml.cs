using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AuroraLauncher.Pages;

public sealed partial class JavaPage : Page
{
    private readonly List<JavaInfo> _foundJava = new();

    public JavaPage()
    {
        InitializeComponent();
        Loaded += JavaPage_Loaded;
        Unloaded += JavaPage_Unloaded;
    }

    private void JavaPage_Loaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged += OnLanguageChanged;
        ApplyLocalization();
        RefreshActiveJavaDisplay();

        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            DispatcherQueue.TryEnqueue(async () =>
            {
                await RunScanAsync(false);
            });
        });
    }

    private void JavaPage_Unloaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshActiveJavaDisplay();
            RenderJavaList();
        });
    }

    public void ApplyLocalization()
    {
        JavaPageTitle.Text = I18n.Get("Java.Title");
        JavaPageSubtitle.Text = I18n.Get("Java.Subtitle");
        CurrentJavaHeaderTitle.Text = I18n.Get("Java.CurrentTitle");
        ResetJavaBtn.Content = I18n.Get("Java.ResetAuto");
        ScanHeaderTitle.Text = I18n.Get("Java.ScanTitle");
        ScanHeaderSubtitle.Text = I18n.Get("Java.ScanSubtitle");
        QuickScanBtnText.Text = I18n.Get("Java.QuickScan");
        DeepScanBtnText.Text = I18n.Get("Java.DeepScan");
        MojangRuntimesTitle.Text = I18n.Get("Java.MojangRuntimesTitle");
        MojangRuntimesSubtitle.Text = I18n.Get("Java.MojangRuntimesSubtitle");
        DownloadJava25BtnText.Text = I18n.Get("Java.DownloadJava25");
        DownloadJava21BtnText.Text = I18n.Get("Java.DownloadJava21");
        DownloadJava17BtnText.Text = I18n.Get("Java.DownloadJava17");
        FoundJavaHeaderTitle.Text = I18n.Get("Java.FoundRuntimes", _foundJava.Count);
    }

    private void RefreshActiveJavaDisplay()
    {
        var configured = Ctx.Settings.JavaPath;
        if (!string.IsNullOrEmpty(configured) && System.IO.File.Exists(configured))
        {
            ActiveJavaText.Text = configured;
            ActiveJavaPathText.Text = I18n.Get("Java.ActiveBadge");
            ResetJavaBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ActiveJavaText.Text = I18n.Get("Java.AutoSelect");
            ActiveJavaPathText.Text = I18n.Get("Common.Default");
            ResetJavaBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void QuickScanBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunScanAsync(false);
    }

    private async void DeepScanBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunScanAsync(true);
    }

    private async Task RunScanAsync(bool deep)
    {
        QuickScanBtn.IsEnabled = false;
        DeepScanBtn.IsEnabled = false;
        ScanStatusSection.Visibility = Visibility.Visible;
        ScanStatusText.Text = I18n.Get("Java.Scanning");

        var progress = new Progress<string>(s =>
        {
            DispatcherQueue.TryEnqueue(() => ScanStatusText.Text = s);
        });

        try
        {
            var found = await JavaScan.ScanAsync(deep, progress, CancellationToken.None);
            _foundJava.Clear();
            _foundJava.AddRange(found);
            FoundJavaHeaderTitle.Text = I18n.Get("Java.FoundRuntimes", _foundJava.Count);
            RenderJavaList();
            ShowInfo(I18n.Get("Java.FoundRuntimes", found.Count), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo(I18n.Get("Common.Error") + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            QuickScanBtn.IsEnabled = true;
            DeepScanBtn.IsEnabled = true;
            ScanStatusSection.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderJavaList()
    {
        JavaListPanel.Children.Clear();
        if (_foundJava.Count == 0)
        {
            JavaListPanel.Children.Add(new TextBlock
            {
                Text = I18n.Get("Java.AutoSelect"),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            return;
        }

        foreach (var java in _foundJava)
        {
            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon { Glyph = "\uE90F", FontSize = 20, Margin = new Thickness(0, 0, 16, 0), Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = java.Display + (string.IsNullOrEmpty(java.Vendor) ? "" : $"  ({java.Vendor})"),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = java.ExePath,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(textPanel);

            var isSelected = Ctx.Settings.JavaPath == java.ExePath;
            var selectBtn = new Button
            {
                Content = isSelected ? I18n.Get("Java.ActiveBadge") : I18n.Get("Java.SetActive"),
                IsEnabled = !isSelected,
                Style = isSelected ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"]
            };
            selectBtn.Click += (_, _) =>
            {
                Ctx.Settings.JavaPath = java.ExePath;
                Ctx.SaveSettings();
                RefreshActiveJavaDisplay();
                RenderJavaList();
                ShowInfo($"{I18n.Get("Java.ActiveBadge")}: {java.Display}", InfoBarSeverity.Success);
            };
            Grid.SetColumn(selectBtn, 2);
            grid.Children.Add(selectBtn);

            card.Child = grid;
            JavaListPanel.Children.Add(card);
        }
    }

    private void ResetJavaBtn_Click(object sender, RoutedEventArgs e)
    {
        Ctx.Settings.JavaPath = "";
        Ctx.SaveSettings();
        RefreshActiveJavaDisplay();
        RenderJavaList();
        ShowInfo(I18n.Get("Java.AutoSelect"), InfoBarSeverity.Informational);
    }

    private async void DownloadJava25Btn_Click(object sender, RoutedEventArgs e)
    {
        await DownloadMojangRuntimeAsync("java-runtime-epsilon", "Java 25");
    }

    private async void DownloadJava21Btn_Click(object sender, RoutedEventArgs e)
    {
        await DownloadMojangRuntimeAsync("java-runtime-delta", "Java 21");
    }

    private async void DownloadJava17Btn_Click(object sender, RoutedEventArgs e)
    {
        await DownloadMojangRuntimeAsync("java-runtime-gamma", "Java 17");
    }

    private async Task DownloadMojangRuntimeAsync(string component, string label)
    {
        DownloadJava25Btn.IsEnabled = false;
        DownloadJava21Btn.IsEnabled = false;
        DownloadJava17Btn.IsEnabled = false;
        DownloadJavaProgressSection.Visibility = Visibility.Visible;
        DownloadJavaProgressBar.IsIndeterminate = true;
        DownloadJavaStatusText.Text = I18n.Get("Java.DownloadingJava", label);

        try
        {
            var progress = new Progress<ProgressReport>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadJavaStatusText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Phase : $"{p.Phase} · {p.Detail}";
                    if (p.Percent >= 0)
                    {
                        DownloadJavaProgressBar.IsIndeterminate = false;
                        DownloadJavaProgressBar.Value = p.Percent;
                        DownloadJavaPercentText.Text = $"{p.Percent}%";
                    }
                    else
                    {
                        DownloadJavaProgressBar.IsIndeterminate = true;
                        DownloadJavaPercentText.Text = "";
                    }
                });
            });

            var javaPath = await Runtimes.EnsureRuntimeAsync(component, progress, CancellationToken.None);
            Ctx.Settings.JavaPath = javaPath;
            Ctx.SaveSettings();
            RefreshActiveJavaDisplay();
            await RunScanAsync(false);
            ShowInfo(I18n.Get("Java.DownloadJavaSuccess", label), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo(I18n.Get("Common.Error") + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            DownloadJava25Btn.IsEnabled = true;
            DownloadJava21Btn.IsEnabled = true;
            DownloadJava17Btn.IsEnabled = true;
            DownloadJavaProgressSection.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        JavaInfoBar.Message = message;
        JavaInfoBar.Severity = severity;
        JavaInfoBar.IsOpen = true;
    }
}
