using System.Text;
using AuroraLauncher.Core;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace AuroraLauncher;

public sealed partial class DebugConsoleWindow : Window
{
    private static DebugConsoleWindow? _instance;
    public static DebugConsoleWindow Instance => _instance ??= new DebugConsoleWindow();

    private readonly List<string> _allLines = new();
    private string _filter = "";

    public DebugConsoleWindow()
    {
        InitializeComponent();
        _instance = this;

        Title = I18n.Get("Debug.ConsoleTitle");
        ConsoleHeaderTitle.Text = I18n.Get("Debug.ConsoleTitle");
        SearchBox.PlaceholderText = I18n.Get("Common.Search");
        AutoScrollToggle.Content = I18n.Get("Debug.AutoScroll");
        CopyLogsBtn.Content = I18n.Get("Debug.CopyAll");
        ClearLogsBtn.Content = I18n.Get("Common.Clear");
        StatusText.Text = I18n.Get("Debug.Connected");

        Closed += (_, _) =>
        {
            _instance = null;
            Ctx.LogEmitted -= OnLogEmitted;
        };

        // Заполняем историей
        var history = Ctx.GetRecentLogs(400);
        _allLines.AddRange(history);
        ApplyFilter();

        Ctx.LogEmitted += OnLogEmitted;
    }

    public static void ShowWindow()
    {
        var win = Instance;
        win.Activate();
    }

    private void OnLogEmitted(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _allLines.Add(line);
            if (string.IsNullOrEmpty(_filter) || line.Contains(_filter, StringComparison.OrdinalIgnoreCase))
            {
                if (ConsoleTextBox.Text.Length > 0)
                    ConsoleTextBox.Text += Environment.NewLine + line;
                else
                    ConsoleTextBox.Text = line;

                LineCountText.Text = $"{_allLines.Count} {I18n.Get("Debug.Lines")}";

                if (AutoScrollToggle.IsChecked == true)
                {
                    LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
                }
            }
        });
    }

    private void SearchBox_TextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
    {
        _filter = SearchBox.Text?.Trim() ?? "";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrEmpty(_filter)
            ? _allLines
            : _allLines.Where(l => l.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        var sb = new StringBuilder();
        foreach (var l in filtered)
            sb.AppendLine(l);

        ConsoleTextBox.Text = sb.ToString();
        LineCountText.Text = $"{filtered.Count} / {_allLines.Count} {I18n.Get("Debug.Lines")}";

        if (AutoScrollToggle.IsChecked == true)
        {
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(ConsoleTextBox.Text);
        Clipboard.SetContent(dp);
        StatusText.Text = I18n.Get("Common.Copied");
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _allLines.Clear();
        ConsoleTextBox.Text = "";
        LineCountText.Text = $"0 {I18n.Get("Debug.Lines")}";
    }
}
