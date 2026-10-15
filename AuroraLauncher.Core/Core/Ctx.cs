using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Глобальный контекст приложения: настройки, аккаунт, дебаг-лог.</summary>
public static class Ctx
{
    public static Settings Settings { get; private set; } = new();
    public static MinecraftAccount? Account { get; set; }

    public static event Action<string>? LogEmitted;

    private static readonly object LogLock = new();
    private static readonly ConcurrentQueue<string> LogQueue = new();

    static Ctx()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAllLogs();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"[CRITICAL UNHANDLED] {e.ExceptionObject}");
            FlushAllLogs();
        };
    }

    public static void Init()
    {
        Paths.EnsureDirs();
        Settings = Settings.Load();
        I18n.Init(Settings.Language);
        Account = Accounts.Load();
        Log($"=== AuroraLauncher запущен ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        Log($"Рабочая папка: {Environment.CurrentDirectory}");
        Log($"Папка лаунчера: {AppDomain.CurrentDomain.BaseDirectory}");
    }

    public static void SaveSettings() => Settings.Save();

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogQueue.Enqueue(line);

        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                File.AppendAllText(Path.Combine(Paths.LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), line + Environment.NewLine);
            }
            catch { }
        }

        LogEmitted?.Invoke(line);
    }

    public static List<string> GetRecentLogs(int maxCount = 300)
    {
        return LogQueue.TakeLast(maxCount).ToList();
    }

    public static void FlushAllLogs()
    {
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogsDir);
                var finalLine = $"[{DateTime.Now:HH:mm:ss.fff}] === Завершение работы лаунчера ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===";
                File.AppendAllText(Path.Combine(Paths.LogsDir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), finalLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
