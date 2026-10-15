using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AuroraLauncher.Pages;

public sealed partial class HomePage : Page
{
    private CancellationTokenSource? _launchCts;

    public HomePage()
    {
        InitializeComponent();
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        LauncherState.SelectedInstanceChanged += RefreshState;
        LauncherState.InstancesChanged += RefreshInstancesList;
        LauncherState.AccountChanged += RefreshAccount;
        LauncherState.ProgressUpdated += OnProgressUpdated;
        I18n.LanguageChanged += OnLanguageChanged;

        ApplyLocalization();
        RefreshAccount();
        RefreshState();
        RefreshInstancesList();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        LauncherState.SelectedInstanceChanged -= RefreshState;
        LauncherState.InstancesChanged -= RefreshInstancesList;
        LauncherState.AccountChanged -= RefreshAccount;
        LauncherState.ProgressUpdated -= OnProgressUpdated;
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshAccount();
            RefreshState();
            RefreshInstancesList();
        });
    }

    public void ApplyLocalization()
    {
        HomeHeaderSubtitle.Text = I18n.Get("Home.Subtitle");
        SelectedInstanceBadgeText.Text = I18n.Get("Home.SelectedInstance");
        ReadyBadgeText.Text = I18n.Get("Home.ReadyToLaunch");
        PlayButtonText.Text = I18n.Get("Home.Play");
        OpenFolderButtonText.Text = I18n.Get("Home.GameFolder");
        ManageModsButtonText.Text = I18n.Get("Home.Mods");
        NewInstanceButtonText.Text = I18n.Get("Home.CreateInstance");
        AllInstancesHeaderText.Text = I18n.Get("Home.AllInstances");
        EmptyInstancesTitle.Text = I18n.Get("Home.NoInstancesTitle");
        EmptyInstancesDesc.Text = I18n.Get("Home.NoInstancesDesc");
        EmptyCreateInstanceBtn.Content = I18n.Get("Home.CreateFirstInstance");
    }

    private void RefreshAccount()
    {
        var acc = Ctx.Account;
        if (acc != null)
        {
            AccountNameText.Text = acc.Username;
            AccountTypeText.Text = acc.IsOffline ? I18n.Get("Accounts.NoAccount") : I18n.Get("Home.AccountType");
            AccountActionBtn.Content = I18n.Get("Home.ManageAccount");
            AccountIcon.Glyph = acc.IsOffline ? "\uE77B" : "\uE73E";
        }
        else
        {
            AccountNameText.Text = I18n.Get("Home.NotLoggedIn");
            AccountTypeText.Text = I18n.Get("Accounts.SignInHint");
            AccountActionBtn.Content = I18n.Get("Home.Login");
            AccountIcon.Glyph = "\uE77B";
        }
    }

    private void RefreshState()
    {
        var inst = LauncherState.SelectedInstance;
        if (inst != null)
        {
            SelectedInstanceNameText.Text = inst.Name;
            SelectedInstanceIconText.Text = string.IsNullOrWhiteSpace(inst.Icon) ? "🎮" : inst.Icon;
            SelectedInstanceVersionBadge.Text = $"{I18n.Get("Home.Version")}: {inst.McVersion}";
            var loaderName = string.IsNullOrEmpty(inst.Loader) ? "Vanilla" : char.ToUpperInvariant(inst.Loader[0]) + inst.Loader.Substring(1);
            SelectedInstanceLoaderBadge.Text = $"{I18n.Get("Home.Loader")}: {loaderName}";
            var ramGb = Ctx.Settings.RamMb / 1024.0;
            SelectedInstanceRamBadge.Text = $"{I18n.Get("Home.Ram")}: {ramGb:0.0} GB";
            ReadyBadge.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
            OpenFolderButton.IsEnabled = true;
            ManageModsButton.IsEnabled = true;
        }
        else
        {
            SelectedInstanceNameText.Text = I18n.Get("Home.NoInstanceSelected");
            SelectedInstanceIconText.Text = "🎮";
            SelectedInstanceVersionBadge.Text = $"{I18n.Get("Home.Version")}: —";
            SelectedInstanceLoaderBadge.Text = $"{I18n.Get("Home.Loader")}: —";
            SelectedInstanceRamBadge.Text = $"{I18n.Get("Home.Ram")}: {Ctx.Settings.RamMb / 1024.0:0.0} GB";
            ReadyBadge.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = false;
            OpenFolderButton.IsEnabled = false;
            ManageModsButton.IsEnabled = false;
        }
    }

    private void RefreshInstancesList()
    {
        InstancesListPanel.Children.Clear();
        var instances = LauncherState.InstancesList;
        InstancesCountText.Text = I18n.Get("Home.InstancesCount", instances.Count);

        if (instances.Count == 0)
        {
            EmptyInstancesBorder.Visibility = Visibility.Visible;
            return;
        }

        EmptyInstancesBorder.Visibility = Visibility.Collapsed;
        foreach (var inst in instances)
        {
            var isCurrent = LauncherState.SelectedInstance?.Name == inst.Name;

            var card = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources[isCurrent ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(isCurrent ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Иконка
            var iconBorder = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
                Margin = new Thickness(0, 0, 16, 0)
            };
            var iconText = new TextBlock
            {
                Text = inst.Icon,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = iconText;
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            // Тексты
            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            textPanel.Children.Add(new TextBlock
            {
                Text = inst.Name,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = inst.Subtitle,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(textPanel);

            // Кнопки действий
            var actionsPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };

            var selectBtn = new Button
            {
                Content = isCurrent ? I18n.Get("Common.Installed") : I18n.Get("Instances.NameLabel"),
                IsEnabled = !isCurrent,
                Style = isCurrent ? (Style)Application.Current.Resources["AccentButtonStyle"] : (Style)Application.Current.Resources["DefaultButtonStyle"]
            };
            selectBtn.Click += (_, _) =>
            {
                LauncherState.SelectInstance(inst);
                RefreshInstancesList();
            };
            actionsPanel.Children.Add(selectBtn);

            var folderBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uED25", FontSize = 14 }
            };
            ToolTipService.SetToolTip(folderBtn, I18n.Get("Home.GameFolder"));
            folderBtn.Click += (_, _) => Instances.OpenFolder(inst);
            actionsPanel.Children.Add(folderBtn);

            var deleteBtn = new Button
            {
                Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }
            };
            ToolTipService.SetToolTip(deleteBtn, I18n.Get("Common.Delete"));
            deleteBtn.Click += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = I18n.Get("Instances.DeleteConfirmTitle"),
                    Content = I18n.Get("Instances.DeleteConfirmMessage", inst.Name),
                    PrimaryButtonText = I18n.Get("Common.Delete"),
                    CloseButtonText = I18n.Get("Common.Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    Instances.Delete(inst);
                    var all = LauncherState.InstancesList;
                    all.RemoveAll(i => i.Name == inst.Name);
                    Instances.SaveAll(all);
                    LauncherState.RefreshInstances();
                }
            };
            actionsPanel.Children.Add(deleteBtn);

            Grid.SetColumn(actionsPanel, 2);
            grid.Children.Add(actionsPanel);

            card.Child = grid;
            InstancesListPanel.Children.Add(card);
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        var inst = LauncherState.SelectedInstance;
        if (inst == null)
        {
            ShowInfo(I18n.Get("Mods.SelectInstanceFirst"), InfoBarSeverity.Warning);
            return;
        }

        if (Ctx.Account == null)
        {
            ShowInfo(I18n.Get("Accounts.SignInHint"), InfoBarSeverity.Warning);
            (App.CurrentWindow as MainWindow)?.NavigateTo("accounts");
            return;
        }

        _launchCts?.Cancel();
        _launchCts = new CancellationTokenSource();

        PlayButton.IsEnabled = false;
        ProgressSection.Visibility = Visibility.Visible;
        LaunchProgressBar.IsIndeterminate = true;
        StatusPhaseText.Text = I18n.Get("Home.PreparingLaunch");
        StatusPercentText.Text = "";

        try
        {
            var proc = await LauncherState.LaunchAsync(inst, _launchCts.Token);
            ShowInfo($"Minecraft launched! PID: {proc.Id}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowInfo(I18n.Get("Common.Cancel"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowInfo(I18n.Get("Common.Error") + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            PlayButton.IsEnabled = true;
            ProgressSection.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProgressUpdated(ProgressReport report)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StatusPhaseText.Text = string.IsNullOrWhiteSpace(report.Detail) ? report.Phase : $"{report.Phase} · {report.Detail}";
            if (report.Percent >= 0)
            {
                LaunchProgressBar.IsIndeterminate = false;
                LaunchProgressBar.Value = report.Percent;
                StatusPercentText.Text = $"{report.Percent}%";
            }
            else
            {
                LaunchProgressBar.IsIndeterminate = true;
                StatusPercentText.Text = "";
            }
        });
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (LauncherState.SelectedInstance != null)
            Instances.OpenFolder(LauncherState.SelectedInstance);
    }

    private void ManageModsButton_Click(object sender, RoutedEventArgs e)
    {
        (App.CurrentWindow as MainWindow)?.NavigateTo("mods");
    }

    private void NewInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        (App.CurrentWindow as MainWindow)?.NavigateTo("instances");
    }

    private void AccountActionBtn_Click(object sender, RoutedEventArgs e)
    {
        (App.CurrentWindow as MainWindow)?.NavigateTo("accounts");
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        MessageInfoBar.Message = message;
        MessageInfoBar.Severity = severity;
        MessageInfoBar.IsOpen = true;
    }
}
