using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Глобальный контекст приложения: настройки, аккаунт, лог.</summary>
public static class Ctx
{
    public static Settings Settings { get; private set; } = new();
    public static MinecraftAccount? Account { get; set; }

    public static event Action<string>? LogEmitted;

    public static void Init()
    {
        Paths.EnsureDirs();
        Settings = Settings.Load();
        Account = Accounts.Load();
    }

    public static void SaveSettings() => Settings.Save();

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            Directory.CreateDirectory(Paths.LogsDir);
            File.AppendAllText(Path.Combine(Paths.LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine);
        }
        catch { /* лог не критичен */ }
        LogEmitted?.Invoke(line);
    }
}
