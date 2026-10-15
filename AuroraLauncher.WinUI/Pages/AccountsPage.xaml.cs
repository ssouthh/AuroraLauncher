using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AuroraLauncher.Pages;

public sealed partial class AccountsPage : Page
{
    private CancellationTokenSource? _loginCts;
    private DeviceCodeLogin? _currentCode;

    public AccountsPage()
    {
        InitializeComponent();
        Loaded += AccountsPage_Loaded;
        Unloaded += AccountsPage_Unloaded;
    }

    private void AccountsPage_Loaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged += OnLanguageChanged;
        ApplyLocalization();

        RefreshProfileDisplay();
        CheckExternalAccountsAvailability();
        CheckClientIdStatus();
    }

    private void AccountsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        I18n.LanguageChanged -= OnLanguageChanged;
        CancelLogin();
    }

    private void OnLanguageChanged()
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            ApplyLocalization();
            RefreshProfileDisplay();
            CheckExternalAccountsAvailability();
            CheckClientIdStatus();
        });
    }

    public void ApplyLocalization()
    {
        AccountsPageTitle.Text = I18n.Get("Accounts.Title");
        AccountsPageSubtitle.Text = I18n.Get("Accounts.Subtitle");
        CurrentProfileHeaderTitle.Text = I18n.Get("Accounts.CurrentProfile");
        RefreshAccountBtn.Content = I18n.Get("Accounts.RefreshToken");
        LogoutAccountBtn.Content = I18n.Get("Accounts.Logout");
        ModrinthImportTitle.Text = I18n.Get("Accounts.ModrinthDetectedTitle");
        ImportFromModrinthBtnText.Text = I18n.Get("Accounts.ImportProfile");
        MicrosoftAuthTitle.Text = I18n.Get("Accounts.OfficialTitle");
        MicrosoftAuthDesc.Text = I18n.Get("Accounts.OfficialDesc");
        MissingClientIdBar.Title = I18n.Get("Accounts.MissingClientIdTitle");
        MissingClientIdBar.Message = I18n.Get("Accounts.MissingClientIdMessage");
        StartDeviceLoginBtnText.Text = I18n.Get("Accounts.SignInMicrosoft");
        DeviceCodeStepsTitle.Text = I18n.Get("Accounts.DeviceCodeSteps");
        DeviceStep1Text.Text = I18n.Get("Accounts.Step1");
        CopyCodeBtnText.Text = I18n.Get("Accounts.CopyCode");
        DeviceStep2Text.Text = I18n.Get("Accounts.Step2");
        OpenBrowserLinkBtnText.Text = I18n.Get("Accounts.OpenBrowser");
        CancelMsLoginBtn.Content = I18n.Get("Accounts.CancelLogin");
    }

    private void CheckClientIdStatus()
    {
        MissingClientIdBar.IsOpen = !Auth.HasValidClientId;
    }

    private void CheckExternalAccountsAvailability()
    {
        try
        {
            var detected = ExternalLaunchersImporter.DetectAllAccounts();
            if (detected.Count > 0)
            {
                ModrinthImportCard.Visibility = Visibility.Visible;
                var names = string.Join(", ", detected.Select(d => $"«{d.Username}» ({d.LauncherSource})"));
                ModrinthImportDescText.Text = $"{I18n.Get("Accounts.ModrinthDetectedDesc")} ({names})";
            }
            else
            {
                ModrinthImportCard.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            ModrinthImportCard.Visibility = Visibility.Collapsed;
        }
    }

    private void ImportFromModrinthBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var account = ExternalLaunchersImporter.TryImportBest();
            if (account != null)
            {
                Ctx.Account = account;
                LauncherState.NotifyAccountChanged();
                RefreshProfileDisplay();
                ShowInfo(I18n.Get("Accounts.LoginSuccess", account.Username), InfoBarSeverity.Success);
            }
            else
            {
                ShowInfo(I18n.Get("Accounts.LoginError", "External account not found"), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowInfo(I18n.Get("Common.Error") + ": " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RefreshProfileDisplay()
    {
        var acc = Ctx.Account;
        if (acc != null)
        {
            ProfileUsernameText.Text = acc.Username;
            ProfileUuidText.Text = $"UUID: {acc.Uuid}";
            ProfileBadgeText.Text = acc.IsOffline ? I18n.Get("Accounts.NoAccount") : I18n.Get("Home.AccountType");
            ProfileBadgeBorder.Background = (Brush)Application.Current.Resources[acc.IsOffline ? "LayerFillColorDefaultBrush" : "AccentFillColorDefaultBrush"];
            AvatarGlyph.Glyph = acc.IsOffline ? "\uE77B" : "\uE73E";

            RefreshAccountBtn.Visibility = acc.IsOffline ? Visibility.Collapsed : Visibility.Visible;
            LogoutAccountBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ProfileUsernameText.Text = I18n.Get("Accounts.NotLoggedIn");
            ProfileUuidText.Text = I18n.Get("Accounts.SignInHint");
            ProfileBadgeText.Text = I18n.Get("Accounts.NoAccount");
            ProfileBadgeBorder.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
            AvatarGlyph.Glyph = "\uE77B";

            RefreshAccountBtn.Visibility = Visibility.Collapsed;
            LogoutAccountBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void StartDeviceLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelLogin();
        _loginCts = new CancellationTokenSource();

        StartDeviceLoginBtn.IsEnabled = false;
        ShowInfo(I18n.Get("Common.Loading"), InfoBarSeverity.Informational);

        try
        {
            _currentCode = await Auth.BeginDeviceLoginAsync(_loginCts.Token);
            UserCodeDisplay.Text = _currentCode.UserCode;
            CopyCodeBtnText.Text = I18n.Get("Accounts.CopyCode");

            MicrosoftStartSection.Visibility = Visibility.Collapsed;
            MicrosoftFlowSection.Visibility = Visibility.Visible;
            MsPollingRing.IsActive = true;
            MsPollingStatusText.Text = I18n.Get("Accounts.WaitingApproval");

            var uri = _currentCode.VerificationUriComplete ?? _currentCode.VerificationUri;
            _ = Launcher.LaunchUriAsync(new Uri(uri));

            ShowInfo(I18n.Get("Accounts.WaitingApproval"), InfoBarSeverity.Informational);

            _ = PollAndCompleteLoginAsync(_currentCode, _loginCts.Token);
        }
        catch (OperationCanceledException)
        {
            ResetLoginFlow();
        }
        catch (Exception ex)
        {
            ResetLoginFlow();
            ShowInfo(I18n.Get("Accounts.LoginError", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async Task PollAndCompleteLoginAsync(DeviceCodeLogin code, CancellationToken ct)
    {
        try
        {
            var ms = await Auth.PollDeviceTokenAsync(code, ct);

            DispatcherQueue.TryEnqueue(() =>
            {
                MsPollingStatusText.Text = I18n.Get("Common.Loading");
            });

            var account = await Auth.LoginAsync(ms, ct);
            Ctx.Account = account;
            LauncherState.NotifyAccountChanged();

            DispatcherQueue.TryEnqueue(() =>
            {
                ResetLoginFlow();
                RefreshProfileDisplay();
                ShowInfo(I18n.Get("Accounts.LoginSuccess", account.Username), InfoBarSeverity.Success);
            });
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(ResetLoginFlow);
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ResetLoginFlow();
                ShowInfo(I18n.Get("Accounts.LoginError", ex.Message), InfoBarSeverity.Error);
            });
        }
    }

    private void CopyCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_currentCode != null)
        {
            var dp = new DataPackage();
            dp.SetText(_currentCode.UserCode);
            Clipboard.SetContent(dp);
            CopyCodeBtnText.Text = I18n.Get("Common.Copied");
        }
    }

    private void OpenBrowserLinkBtn_Click(object sender, RoutedEventArgs e)
    {
        var uri = _currentCode?.VerificationUriComplete ?? _currentCode?.VerificationUri ?? "https://www.microsoft.com/link";
        _ = Launcher.LaunchUriAsync(new Uri(uri));
    }

    private void CancelMsLoginBtn_Click(object sender, RoutedEventArgs e)
    {
        CancelLogin();
        ResetLoginFlow();
        ShowInfo(I18n.Get("Common.Cancel"), InfoBarSeverity.Informational);
    }

    private void CancelLogin()
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = null;
    }

    private void ResetLoginFlow()
    {
        MicrosoftFlowSection.Visibility = Visibility.Collapsed;
        MicrosoftStartSection.Visibility = Visibility.Visible;
        StartDeviceLoginBtn.IsEnabled = true;
        MsPollingRing.IsActive = false;
        _currentCode = null;
    }

    private async void RefreshAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        if (Ctx.Account == null || Ctx.Account.IsOffline) return;

        RefreshAccountBtn.IsEnabled = false;
        ShowInfo(I18n.Get("Common.Loading"), InfoBarSeverity.Informational);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Auth.EnsureFreshMcTokenAsync(cts.Token);
            LauncherState.NotifyAccountChanged();
            RefreshProfileDisplay();
            ShowInfo(I18n.Get("Common.Success"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfo(I18n.Get("Common.Error") + ": " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            RefreshAccountBtn.IsEnabled = true;
        }
    }

    private void LogoutAccountBtn_Click(object sender, RoutedEventArgs e)
    {
        Accounts.Delete();
        Ctx.Account = null;
        LauncherState.NotifyAccountChanged();
        RefreshProfileDisplay();
        ShowInfo(I18n.Get("Accounts.NotLoggedIn"), InfoBarSeverity.Informational);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AccountInfoBar.Message = message;
        AccountInfoBar.Severity = severity;
        AccountInfoBar.IsOpen = true;
    }
}
