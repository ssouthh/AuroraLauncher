using System.Windows;
using AuroraLauncher.Core;

namespace AuroraLauncher.Views;

public partial class ProgressWindow : Window
{
    private readonly CancellationTokenSource _cts = new();

    public ProgressWindow(string title)
    {
        InitializeComponent();
        Title = title;
        PhaseText.Text = title;
    }

    public CancellationToken Token => _cts.Token;

    public IProgress<ProgressReport> Progress => new Progress<ProgressReport>(r =>
    {
        PhaseText.Text = r.Phase;
        DetailText.Text = r.Detail;
        Bar.IsIndeterminate = r.Percent < 0;
        if (r.Percent >= 0) Bar.Value = r.Percent;
    });

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DetailText.Text = "Отмена…";
        _cts.Cancel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }
}
