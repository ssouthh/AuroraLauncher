using System.Windows;
using AuroraLauncher.Core;

namespace AuroraLauncher.Views;

public partial class LoginWindow : Window
{
    private readonly CancellationTokenSource _cts = new();
    private DeviceCodeLogin? _dcl;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
    }

    /// <summary>true — вход выполнен успешно.</summary>
    public bool Success { get; private set; }

    private async Task RunAsync()
    {
        try
        {
            OpenBtn.IsEnabled = true;
            StatusText.Text = "Получение кода…";
            _dcl = await Auth.BeginDeviceLoginAsync(Token);
            CodeText.Text = string.Join(" ", _dcl.UserCode.Chunk(5).Select(c => new string(c)));
            StatusText.Text = "Ожидание входа…";
            Ctx.Log("Начат вход через Microsoft (device code)");
            var ms = await Auth.PollDeviceTokenAsync(_dcl, Token);
            StatusText.Text = "Получение профиля Minecraft…";
            var account = await Auth.LoginAsync(ms, Token);
            Ctx.Account = account;
            Ctx.Log($"Вход выполнен: {account.Username} ({account.Uuid})");
            Success = true;
            MessageBox.Show(this, $"Привет, {account.Username}!", "Вход выполнен",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (OperationCanceledException) { Close(); }
        catch (AuthException ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Ошибка входа", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(this, "Ошибка входа: " + ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private CancellationToken Token => _cts.Token;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_dcl == null) return;
        Proc.OpenUrl(_dcl.VerificationUriComplete ?? _dcl.VerificationUri);
        OpenBtn.IsEnabled = false;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_dcl != null) Clipboard.SetText(_dcl.UserCode);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { _cts.Cancel(); Close(); }

    protected override void OnClosed(EventArgs e) { _cts.Cancel(); base.OnClosed(e); }
}
