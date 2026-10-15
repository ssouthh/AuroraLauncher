using System.IO;
using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace AuroraLauncher;

public sealed partial class CrashWindow : Window
{
    private readonly CrashReportInfo _info;
    private readonly Instance? _instance;

    public CrashWindow(CrashReportInfo info, Instance? inst)
    {
        InitializeComponent();
        _info = info;
        _instance = inst;

        Title = I18n.Get("Crash.WindowTitle");
        CrashTitleText.Text = info.Title;
        ExitCodeText.Text = $"{I18n.Get("Common.Error")}: {info.ExitCode}";
        CulpritLabel.Text = I18n.Get("Crash.Culprit");
        CulpritText.Text = info.Culprit;
        ExplanationText.Text = info.Explanation;
        AdviceLabel.Text = I18n.Get("Crash.Recommendation");
        RecommendationText.Text = info.Recommendation;
        LogHeaderTitle.Text = I18n.Get("Crash.FullLog");
        CopyLogBtnText.Text = I18n.Get("Common.Copy");
        CrashReportsFolderBtn.Content = I18n.Get("Crash.OpenCrashFolder");
        CloseCrashWindowBtn.Content = I18n.Get("Common.Close");
        CrashLogTextBox.Text = info.FullLog;

        if (!string.IsNullOrEmpty(info.CulpritModName) && inst != null)
        {
            var matchFile = FindModFile(inst, info.CulpritModName);
            if (matchFile != null)
            {
                DeleteCulpritModBtn.Visibility = Visibility.Visible;
                DeleteCulpritModText.Text = I18n.Get("Crash.DeleteMod", matchFile);
            }
        }
    }

    private static string? FindModFile(Instance inst, string name)
    {
        var installed = Mods.Installed(inst);
        var clean = name.ToLowerInvariant();
        return installed.FirstOrDefault(f => f.ToLowerInvariant().Contains(clean));
    }

    private void DeleteCulpritModBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_instance == null || string.IsNullOrEmpty(_info.CulpritModName)) return;
        var file = FindModFile(_instance, _info.CulpritModName);
        if (file != null)
        {
            Mods.Delete(_instance, file);
            DeleteCulpritModBtn.IsEnabled = false;
            DeleteCulpritModText.Text = I18n.Get("Mods.ModRemoved", file);
            Ctx.Log($"[CrashHandler] User removed conflicting mod {file}");
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(_info.FullLog);
        Clipboard.SetContent(dp);
        CopyLogBtnText.Text = I18n.Get("Common.Copied");
    }

    private void OpenCrashReportsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_instance != null)
        {
            var dir = Path.Combine(_instance.GameDir, "crash-reports");
            Directory.CreateDirectory(dir);
            _ = Launcher.LaunchFolderPathAsync(dir);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
