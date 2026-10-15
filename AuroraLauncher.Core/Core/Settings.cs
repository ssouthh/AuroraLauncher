using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuroraLauncher.Core;

/// <summary>Пути к данным лаунчера (по умолчанию — %APPDATA%\AuroraLauncher).</summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraLauncher");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string AccountsFile => Path.Combine(Root, "accounts.dat");
    public static string InstancesDir => Path.Combine(Root, "instances");
    public static string AssetsDir => Path.Combine(Root, "assets");
    public static string RuntimesDir => Path.Combine(Root, "runtimes");
    public static string CacheDir => Path.Combine(Root, "cache");
    public static string LogsDir => Path.Combine(Root, "logs");

    public static void EnsureDirs()
    {
        foreach (var d in new[] { Root, InstancesDir, AssetsDir, RuntimesDir, CacheDir, LogsDir })
            Directory.CreateDirectory(d);
    }
}

public class Settings
{
    /// <summary>
    /// По умолчанию пустой. Требуется зарегистрировать собственный Application (Client) ID
    /// в Microsoft Entra ID (Azure Portal) и указать его в настройках или через AURORA_MS_CLIENT_ID.
    /// Использование чужих Client ID сторонних лаунчеров без разрешения запрещено.
    /// </summary>
    public const string DefaultMsClientId = "";

    public int RamMb { get; set; } = 4096;
    public bool CustomResolution { get; set; } = false;
    public int WindowWidth { get; set; } = 854;
    public int WindowHeight { get; set; } = 480;
    public string ExtraJvmArgs { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public bool ShowSnapshots { get; set; } = false;
    public bool ShowOldVersions { get; set; } = false;
    public bool DebugMode { get; set; } = false;
    public string AppTheme { get; set; } = "Default";
    public string Language { get; set; } = "system";
    [JsonIgnore]
    public string CurseApiKey { get; set; } = "";

    [JsonPropertyName("CurseApiKeyProtected")]
    public string? ProtectedCurseApiKey
    {
        get => string.IsNullOrEmpty(CurseApiKey) ? null : Convert.ToBase64String(
            Secure.Protect(Encoding.UTF8.GetBytes(CurseApiKey), "Settings.CurseApiKey.v1"));
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            try { CurseApiKey = Encoding.UTF8.GetString(Secure.Unprotect(Convert.FromBase64String(value), "Settings.CurseApiKey.v1")); }
            catch { CurseApiKey = ""; }
        }
    }

    // Accept settings files written by previous builds, then migrate to DPAPI on the next save.
    [JsonPropertyName("CurseApiKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCurseApiKey
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value)) CurseApiKey = value; }
    }
    public string MsClientId { get; set; } = "";
    /// <summary>Стабильный идентификатор клиента, передаётся игре как --clientId.</summary>
    public string ClientToken { get; set; } = Guid.NewGuid().ToString("N");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new Settings();
        }
        catch { /* повреждённый файл — используем значения по умолчанию */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, Opts));
    }
}
