using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AuroraLauncher.Core;

/// <summary>
/// Автоматический импорт официального лицензионного аккаунта Minecraft из Modrinth App.
/// Использует встроенный в Windows winsqlite3.dll для прямого безопасного чтения SQLite базы app.db.
/// </summary>
public static class ModrinthAccountHelper
{
    private static readonly string ModrinthDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ModrinthApp", "app.db");

    public static bool HasModrinthApp() => File.Exists(ModrinthDbPath);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPStr)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_exec", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, ExecCallback cb, IntPtr arg, out IntPtr errmsg);

    private delegate int ExecCallback(IntPtr arg, int colCount, IntPtr colValuesPtr, IntPtr colNamesPtr);

    public static string? GetModrinthUsername()
    {
        if (!File.Exists(ModrinthDbPath)) return null;

        IntPtr db = IntPtr.Zero;
        int rc = sqlite3_open_v2(ModrinthDbPath, out db, 1, IntPtr.Zero); // 1 = SQLITE_OPEN_READONLY
        if (rc != 0 || db == IntPtr.Zero) return null;

        string? username = null;
        try
        {
            ExecCallback cb = (arg, colCount, colValuesPtr, colNamesPtr) =>
            {
                for (int i = 0; i < colCount; i++)
                {
                    var namePtr = Marshal.ReadIntPtr(colNamesPtr, i * IntPtr.Size);
                    var name = namePtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(namePtr) : null;
                    if (name == "username")
                    {
                        var valPtr = Marshal.ReadIntPtr(colValuesPtr, i * IntPtr.Size);
                        username = valPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(valPtr) : null;
                    }
                }
                return 0;
            };

            IntPtr errmsg;
            sqlite3_exec(db, "SELECT username FROM minecraft_users WHERE active = 1 LIMIT 1;", cb, IntPtr.Zero, out errmsg);
            if (string.IsNullOrEmpty(username))
            {
                sqlite3_exec(db, "SELECT username FROM minecraft_users LIMIT 1;", cb, IntPtr.Zero, out errmsg);
            }
        }
        catch { }
        finally
        {
            sqlite3_close(db);
        }

        return username;
    }

    public static MinecraftAccount? TryImport()
    {
        if (!File.Exists(ModrinthDbPath)) return null;

        IntPtr db = IntPtr.Zero;
        int rc = sqlite3_open_v2(ModrinthDbPath, out db, 1, IntPtr.Zero);
        if (rc != 0 || db == IntPtr.Zero)
        {
            Ctx.Log($"[ModrinthImport] Не удалось открыть app.db (rc={rc})");
            return null;
        }

        string? uuid = null;
        string? username = null;
        string? accessToken = null;
        string? refreshToken = null;

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
        catch (Exception ex)
        {
            Ctx.Log($"[ModrinthImport] Ошибка чтения app.db: {ex.Message}");
            return null;
        }
        finally
        {
            sqlite3_close(db);
        }

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(accessToken))
        {
            var acc = new MinecraftAccount
            {
                Username = username,
                Uuid = uuid ?? Guid.NewGuid().ToString("N"),
                McAccessToken = accessToken,
                RefreshToken = refreshToken ?? "",
                McTokenTime = DateTimeOffset.UtcNow,
                IsOffline = false,
                AuthType = "modrinth_imported"
            };
            Accounts.Save(acc);
            Ctx.Log($"[ModrinthImport] Успешно импортирован официальный аккаунт {username} ({acc.Uuid})!");
            return acc;
        }

        return null;
    }
}
