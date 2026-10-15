using System.Diagnostics;
using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AuroraLauncher.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isInitializing = false;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
        Unloaded += SettingsPage_Unloaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged += OnLanguageChanged;
        LoadCurrentSettings();
        ApplyLocalization();
    }

    private void SettingsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue?.TryEnqueue(ApplyLocalization);
    }

    private void LoadCurrentSettings()
    {
        _isInitializing = true;
        try
        {
            var s = Ctx.Settings;
            RamSlider.Value = s.RamMb;
            UpdateRamText((int)s.RamMb);

            CustomResolutionSwitch.IsOn = s.CustomResolution;
            WindowWidthBox.Value = s.WindowWidth;
            WindowHeightBox.Value = s.WindowHeight;

            ShowSnapshotsSwitch.IsOn = s.ShowSnapshots;
            ShowOldVersionsSwitch.IsOn = s.ShowOldVersions;

            var themeIndex = s.AppTheme switch
            {
                "Dark" => 1,
                "Light" => 2,
                _ => 0
            };
            AppThemeComboBox.SelectedIndex = themeIndex;

            PopulateLanguageSelector();

            ExtraJvmArgsBox.Text = s.ExtraJvmArgs ?? "";
            CurseApiKeyBox.Password = s.CurseApiKey ?? "";
            MsClientIdBox.Text = s.MsClientId ?? "";
            DebugModeSwitch.IsOn = s.DebugMode;

            DataPathText.Text = Paths.Root;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void PopulateLanguageSelector()
    {
        LanguageSelectorComboBox.Items.Clear();

        var systemItem = new ComboBoxItem
        {
            Content = $"⚙️ {I18n.Get("Settings.LanguageDefault")}",
            Tag = "system"
        };
        LanguageSelectorComboBox.Items.Add(systemItem);

        int selectedIndex = 0;
        int i = 1;
        var currentCode = Ctx.Settings.Language ?? "system";

        foreach (var lang in I18n.SupportedLanguages)
        {
            var item = new ComboBoxItem
            {
                Content = $"{lang.NativeName} ({lang.EnglishName})",
                Tag = lang.Code
            };
            LanguageSelectorComboBox.Items.Add(item);

            if (string.Equals(lang.Code, currentCode, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
            i++;
        }

        if (string.Equals(currentCode, "system", StringComparison.OrdinalIgnoreCase))
        {
            selectedIndex = 0;
        }

        LanguageSelectorComboBox.SelectedIndex = selectedIndex;
    }

    public void ApplyLocalization()
    {
        SettingsPageTitle.Text = I18n.Get("Settings.Title");
        SettingsPageSubtitle.Text = I18n.Get("Settings.Subtitle");
        SaveSettingsTopBtnText.Text = I18n.Get("Settings.SaveAll");
        SaveSettingsBtn.Content = I18n.Get("Settings.SaveAll");
        SaveSettingsBottomHint.Text = I18n.Get("Common.Saving");

        // Subtabs
        TabGameJvmText.Text = I18n.Get("Settings.TabGameJvm");
        TabAppearanceText.Text = I18n.Get("Settings.TabAppearance");
        TabLanguageText.Text = I18n.Get("Settings.TabLanguage");
        TabAccountsApiText.Text = I18n.Get("Settings.TabAccountsApi");
        TabDebugStorageText.Text = I18n.Get("Settings.TabDebug");
        TabAboutText.Text = I18n.Get("Settings.TabAbout");

        ToolTipService.SetToolTip(TabGameJvmBtn, I18n.Get("Settings.TabGameJvm"));
        ToolTipService.SetToolTip(TabAppearanceBtn, I18n.Get("Settings.TabAppearance"));
        ToolTipService.SetToolTip(TabLanguageBtn, I18n.Get("Settings.TabLanguage"));
        ToolTipService.SetToolTip(TabAccountsApiBtn, I18n.Get("Settings.TabAccountsApi"));
        ToolTipService.SetToolTip(TabDebugStorageBtn, I18n.Get("Settings.TabDebug"));
        ToolTipService.SetToolTip(TabAboutBtn, I18n.Get("Settings.TabAbout"));

        // Game & JVM Panel
        RamHeaderTitle.Text = I18n.Get("Settings.RamTitle");
        RamHeaderSubtitle.Text = I18n.Get("Settings.RamSubtitle");
        UpdateRamText((int)RamSlider.Value);

        CustomResolutionTitle.Text = I18n.Get("Settings.CustomResolution");
        CustomResolutionSubtitle.Text = I18n.Get("Settings.ResolutionSubtitle");
        WindowWidthBox.Header = I18n.Get("Settings.Width");
        WindowHeightBox.Header = I18n.Get("Settings.Height");

        ExtraJvmArgsTitle.Text = I18n.Get("Settings.ExtraJvmArgs");
        ExtraJvmArgsSubtitle.Text = I18n.Get("Settings.ExtraJvmArgsSubtitle");

        // Appearance Panel
        AppThemeTitle.Text = I18n.Get("Settings.ThemeTitle");
        AppThemeSubtitle.Text = I18n.Get("Settings.ThemeSubtitle");
        AppThemeItemDefault.Content = I18n.Get("Settings.ThemeDefault");
        AppThemeItemDark.Content = I18n.Get("Settings.ThemeDark");
        AppThemeItemLight.Content = I18n.Get("Settings.ThemeLight");

        FilterTitle.Text = I18n.Get("Settings.FilterTitle");
        FilterSubtitle.Text = I18n.Get("Settings.FilterSubtitle");
        ShowSnapshotsTitle.Text = I18n.Get("Settings.Snapshots");
        ShowSnapshotsSubtitle.Text = I18n.Get("Settings.SnapshotsSubtitle");
        ShowOldVersionsTitle.Text = I18n.Get("Settings.OldVersions");
        ShowOldVersionsSubtitle.Text = I18n.Get("Settings.OldVersionsSubtitle");

        // Language Panel
        LanguageHeaderTitle.Text = I18n.Get("Settings.LanguageTitle");
        LanguageHeaderSubtitle.Text = I18n.Get("Settings.LanguageSubtitle");
        ResetLanguageBtnText.Text = I18n.Get("Settings.LanguageReset");

        // Accounts & API Panel
        MsClientIdTitle.Text = I18n.Get("Settings.MsClientIdTitle");
        MsClientIdSubtitle.Text = I18n.Get("Settings.MsClientIdSubtitle");
        CurseApiKeyTitle.Text = I18n.Get("Settings.CurseApiKeyTitle");
        CurseApiKeySubtitle.Text = I18n.Get("Settings.CurseApiKeySubtitle");

        // Debug Panel
        DebugModeTitle.Text = I18n.Get("Settings.DebugModeTitle");
        DebugModeSubtitle.Text = I18n.Get("Settings.DebugModeSubtitle");
        OpenConsoleBtn.Content = I18n.Get("Debug.ConsoleTitle");
        OpenDataFolderBtn.Content = I18n.Get("Common.OpenFolder");
    }

    private void LanguageSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (LanguageSelectorComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (Ctx.Settings.Language != tag)
            {
                Ctx.Settings.Language = tag;
                Ctx.SaveSettings();
                I18n.SetLanguage(tag);

                LanguageInfoBar.Message = I18n.Get("Settings.Saved");
                LanguageInfoBar.Severity = InfoBarSeverity.Success;
                LanguageInfoBar.IsOpen = true;
            }
        }
    }

    private void ResetLanguageBtn_Click(object sender, RoutedEventArgs e)
    {
        Ctx.Settings.Language = "system";
        Ctx.SaveSettings();
        I18n.SetLanguage("system");

        _isInitializing = true;
        try
        {
            LanguageSelectorComboBox.SelectedIndex = 0;
        }
        finally
        {
            _isInitializing = false;
        }

        LanguageInfoBar.Message = I18n.Get("Settings.Saved");
        LanguageInfoBar.Severity = InfoBarSeverity.Success;
        LanguageInfoBar.IsOpen = true;
    }

    private void DebugModeSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        Ctx.Settings.DebugMode = DebugModeSwitch.IsOn;
        Ctx.SaveSettings();
        if (DebugModeSwitch.IsOn)
        {
            DebugConsoleWindow.ShowWindow();
        }
    }

    private void OpenConsoleBtn_Click(object sender, RoutedEventArgs e)
    {
        DebugConsoleWindow.ShowWindow();
    }

    private void TabGameJvmBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(0);
    private void TabAppearanceBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(1);
    private void TabLanguageBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(2);
    private void TabAccountsApiBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(3);
    private void TabDebugStorageBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(4);
    private void TabAboutBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(5);
    private void HowToGetClientIdBtn_Click(object sender, RoutedEventArgs e) => SwitchSubTab(5);

    private void SwitchSubTab(int index)
    {
        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        var def = (Style)Application.Current.Resources["DefaultButtonStyle"];

        TabGameJvmBtn.Style = index == 0 ? accent : def;
        TabAppearanceBtn.Style = index == 1 ? accent : def;
        TabLanguageBtn.Style = index == 2 ? accent : def;
        TabAccountsApiBtn.Style = index == 3 ? accent : def;
        TabDebugStorageBtn.Style = index == 4 ? accent : def;
        TabAboutBtn.Style = index == 5 ? accent : def;

        GameJvmPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        LanguagePanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        AccountsApiPanel.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        DebugStoragePanel.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        AboutLegalPanel.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (AppThemeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            Ctx.Settings.AppTheme = tag;
            Ctx.SaveSettings();
            (App.CurrentWindow as MainWindow)?.ApplyTheme(tag);
        }
    }

    private void RamSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateRamText((int)e.NewValue);
    }

    private void UpdateRamText(int mb)
    {
        var gb = mb / 1024.0;
        RamValueText.Text = $"{mb} MB ({gb:0.0} GB)";
    }

    private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = Ctx.Settings;
        s.RamMb = (int)RamSlider.Value;
        s.CustomResolution = CustomResolutionSwitch.IsOn;
        s.WindowWidth = (int)WindowWidthBox.Value;
        s.WindowHeight = (int)WindowHeightBox.Value;
        s.ShowSnapshots = ShowSnapshotsSwitch.IsOn;
        s.ShowOldVersions = ShowOldVersionsSwitch.IsOn;
        s.ExtraJvmArgs = ExtraJvmArgsBox.Text?.Trim() ?? "";
        s.CurseApiKey = CurseApiKeyBox.Password?.Trim() ?? "";
        s.MsClientId = MsClientIdBox.Text?.Trim() ?? "";

        Ctx.SaveSettings();
        ShowInfo(I18n.Get("Settings.Saved"), InfoBarSeverity.Success);
    }

    private void OpenDataFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Paths.EnsureDirs();
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Paths.Root) { UseShellExecute = true });
        }
        catch { }
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        SettingsInfoBar.Message = message;
        SettingsInfoBar.Severity = severity;
        SettingsInfoBar.IsOpen = true;
    }
}
