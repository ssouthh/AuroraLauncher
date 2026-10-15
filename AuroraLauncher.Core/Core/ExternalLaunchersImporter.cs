using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AuroraLauncher.Core;

public sealed record DetectedAccount(string Username, string Uuid, string AccessToken, string RefreshToken, string LauncherSource);

/// <summary>
/// Автоматический поиск и импорт лицензионных аккаунтов Minecraft из других установленных лаунчеров:
/// Modrinth App, BetaCraft, Официальный Minecraft Launcher, Prism Launcher, CurseForge, Feather, Lunar.
/// </summary>
public static class ExternalLaunchersImporter
{
    public static List<DetectedAccount> DetectAllAccounts()
    {
        var list = new List<DetectedAccount>();

        // 1. Modrinth App (app.db)
        try
        {
            var modrinthAcc = TryReadModrinth();
            if (modrinthAcc != null) list.Add(modrinthAcc);
        }
        catch { }

        // 2. BetaCraft (.betacraft\launcher\accounts.json)
        try
        {
            var betaAcc = TryReadBetaCraft();
            if (betaAcc != null) list.Add(betaAcc);
        }
        catch { }

        // 3. Официальный лаунчер (.minecraft\launcher_accounts.json)
        try
        {
            var mojangAcc = TryReadOfficialMojang();
            if (mojangAcc != null) list.Add(mojangAcc);
        }
        catch { }

        // 4. Prism Launcher (PrismLauncher\accounts.json)
        try
        {
            var prismAcc = TryReadPrismLauncher();
            if (prismAcc != null) list.Add(prismAcc);
        }
        catch { }

        // 5. Feather Launcher (.feather\accounts.json)
        try
        {
            var featherAcc = TryReadFeather();
            if (featherAcc != null) list.Add(featherAcc);
        }
        catch { }

        // 6. Lunar Client (.lunarclient\settings\game\accounts.json)
        try
        {
            var lunarAcc = TryReadLunar();
            if (lunarAcc != null) list.Add(lunarAcc);
        }
        catch { }

        return list.GroupBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.First())
                   .ToList();
    }

    public static MinecraftAccount? TryImportBest()
    {
        var accounts = DetectAllAccounts();
        var best = accounts.FirstOrDefault();
        if (best == null) return null;

        var acc = new MinecraftAccount
        {
            Username = best.Username,
            Uuid = best.Uuid,
            McAccessToken = best.AccessToken,
            RefreshToken = best.RefreshToken,
            McTokenTime = DateTimeOffset.UtcNow,
            IsOffline = false,
            AuthType = "imported_" + best.LauncherSource.ToLowerInvariant()
        };
        Accounts.Save(acc);
        Ctx.Log($"[AccountImport] Импортирован аккаунт {acc.Username} из {best.LauncherSource}!");
        return acc;
    }

    // ---------- 1. Modrinth App ----------

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, ExecCallback cb, IntPtr arg, out IntPtr errmsg);

    private delegate int ExecCallback(IntPtr arg, int colCount, IntPtr colValuesPtr, IntPtr colNamesPtr);

    private static DetectedAccount? TryReadModrinth()
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "app.db");
        if (!File.Exists(dbPath)) return null;

        IntPtr db = IntPtr.Zero;
        int rc = sqlite3_open_v2(dbPath, out db, 1, IntPtr.Zero);
        if (rc != 0 || db == IntPtr.Zero) return null;

        string? uuid = null, username = null, accessToken = null, refreshToken = null;
        try
        {
            ExecCallback cb = (arg, colCount, colValuesPtr, colNamesPtr) =>
            {
                for (int i = 0; i < colCount; i++)
                {
                    var namePtr = Marshal.ReadIntPtr(colNamesPtr, i * IntPtr.Size);
                    var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(namePtr) : null;
                    var valPtr = Marshal.ReadIntPtr(colValuesPtr, i * IntPtr.Size);
                    var val = valPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(valPtr) : null;

                    if (name == "uuid") uuid = val;
                    else if (name == "username") username = val;
                    else if (name == "access_token") accessToken = val;
                    else if (name == "refresh_token") refreshToken = val;
                }
                return 0;
            };

            IntPtr errmsg;
            sqlite3_exec(db, "SELECT uuid, username, access_token, refresh_token FROM minecraft_users WHERE active = 1 LIMIT 1;", cb, IntPtr.Zero, out errmsg);
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(accessToken))
            {
                sqlite3_exec(db, "SELECT uuid, username, access_token, refresh_token FROM minecraft_users LIMIT 1;", cb, IntPtr.Zero, out errmsg);
            }
        }
        catch { }
        finally { sqlite3_close(db); }

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(accessToken))
        {
            return new DetectedAccount(username, uuid ?? Guid.NewGuid().ToString("N"), accessToken, refreshToken ?? "", "Modrinth App");
        }
        return null;
    }

    // ---------- 2. BetaCraft ----------

    private static DetectedAccount? TryReadBetaCraft()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".betacraft", "launcher", "accounts.json");
        if (!File.Exists(file)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in accs.EnumerateArray())
            {
                var type = a.TryGetProperty("account_type", out var at) ? at.GetString() : "";
                if (type == "MICROSOFT")
                {
                    var user = a.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var token = a.TryGetProperty("access_token", out var t) ? t.GetString() : null;
                    var refresh = a.TryGetProperty("refresh_token", out var r) ? r.GetString() : "";
                    var uuid = a.TryGetProperty("local_uuid", out var id) ? id.GetString() : "";
                    if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
                    {
                        return new DetectedAccount(user, uuid ?? Guid.NewGuid().ToString("N"), token, refresh ?? "", "BetaCraft");
                    }
                }
            }
        }
        return null;
    }

    // ---------- 3. Официальный лаунчер Minecraft ----------

    private static DetectedAccount? TryReadOfficialMojang()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, ".minecraft", "launcher_accounts.json"),
            Path.Combine(appData, ".minecraft", "launcher_accounts_microsoft_store.json")
        };

        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in accs.EnumerateObject())
                    {
                        var a = prop.Value;
                        var token = a.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
                        if (a.TryGetProperty("minecraftProfile", out var prof) && prof.ValueKind == JsonValueKind.Object)
                        {
                            var user = prof.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var uuid = prof.TryGetProperty("id", out var id) ? id.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
                            {
                                return new DetectedAccount(user, uuid ?? Guid.NewGuid().ToString("N"), token, "", "Minecraft Launcher");
                            }
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    // ---------- 4. Prism Launcher ----------

    private static DetectedAccount? TryReadPrismLauncher()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, "PrismLauncher", "accounts.json"),
            Path.Combine(appData, "PolyMC", "accounts.json")
        };

        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in accs.EnumerateArray())
                    {
                        var user = a.TryGetProperty("profile", out var prof) && prof.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var uuid = a.TryGetProperty("profile", out var prof2) && prof2.TryGetProperty("id", out var id) ? id.GetString() : null;
                        var token = a.TryGetProperty("ygg", out var ygg) && ygg.TryGetProperty("token", out var tok) ? tok.GetString() : null;
                        var refresh = "";
                        if (a.TryGetProperty("msa", out var msa) && msa.TryGetProperty("refreshToken", out var rt))
                            refresh = rt.GetString() ?? "";

                        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
                        {
                            return new DetectedAccount(user, uuid ?? Guid.NewGuid().ToString("N"), token, refresh, "Prism Launcher");
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    // ---------- 5. Feather Launcher ----------

    private static DetectedAccount? TryReadFeather()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".feather", "accounts.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("selected", out var sel) && doc.RootElement.TryGetProperty("accounts", out var accs))
            {
                var selKey = sel.GetString();
                if (!string.IsNullOrEmpty(selKey) && accs.TryGetProperty(selKey, out var a))
                {
                    var user = a.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var uuid = a.TryGetProperty("uuid", out var id) ? id.GetString() : null;
                    var token = a.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
                        return new DetectedAccount(user, uuid ?? Guid.NewGuid().ToString("N"), token, "", "Feather Launcher");
                }
            }
        }
        catch { }
        return null;
    }

    // ---------- 6. Lunar Client ----------

    private static DetectedAccount? TryReadLunar()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lunarclient", "settings", "game", "accounts.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("accounts", out var accs) && accs.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in accs.EnumerateObject())
                {
                    var a = prop.Value;
                    var user = a.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var uuid = a.TryGetProperty("uuid", out var id) ? id.GetString() : null;
                    var token = a.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(token))
                        return new DetectedAccount(user, uuid ?? Guid.NewGuid().ToString("N"), token, "", "Lunar Client");
                }
            }
        }
        catch { }
        return null;
    }
}
