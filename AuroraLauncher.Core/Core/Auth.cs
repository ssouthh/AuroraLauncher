using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AuroraLauncher.Core;

public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

public sealed record DeviceCodeLogin(string UserCode, string DeviceCode, string VerificationUri,
    string? VerificationUriComplete, int ExpiresIn, int Interval);

public sealed record MsToken(string AccessToken, string RefreshToken);

public class MinecraftAccount
{
    public string Uuid { get; set; } = "";
    public string Username { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string McAccessToken { get; set; } = "";
    public DateTimeOffset McTokenTime { get; set; } = DateTimeOffset.MinValue;
    public bool IsOffline { get; set; } = false;
    public string AuthType { get; set; } = "official"; // "official", "device_code", "offline"
}

/// <summary>
/// Официальная авторизация Microsoft (device code flow) → Xbox Live → XSTS → Minecraft Services.
/// Пароль пользователя никогда не попадает в лаунчер: подтверждение происходит на странице Microsoft.
/// </summary>
public static class Auth
{
    private const string DeviceEp = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEp = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    /// <summary>
    /// Активный Client ID Microsoft Entra (Azure App).
    /// Приоритет: Настройки -> Переменная окружения AURORA_MS_CLIENT_ID -> Пусто.
    /// Чужие идентификаторы сторонних проектов не используются.
    /// </summary>
    public static string ClientId
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Ctx.Settings.MsClientId))
                return Ctx.Settings.MsClientId.Trim();

            var env = Environment.GetEnvironmentVariable("AURORA_MS_CLIENT_ID");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();

            return Settings.DefaultMsClientId;
        }
    }

    /// <summary>Указывает, настроен ли действительный собственный Client ID.</summary>
    public static bool HasValidClientId => !string.IsNullOrWhiteSpace(ClientId);

    // ---------- Шаг 1. Microsoft OAuth 2.0 (Device Code Flow) ----------

    public static async Task<DeviceCodeLogin> BeginDeviceLoginAsync(CancellationToken ct)
    {
        if (!HasValidClientId)
        {
            throw new AuthException(
                "Microsoft Entra (Azure) Client ID не настроен.\n\n" +
                "Для авторизации по Device Code требуется собственный Client ID приложения.\n" +
                "1. Зарегистрируйте бесплатное приложение в Azure Portal (портал Microsoft Entra).\n" +
                "2. Укажите полученный Application (Client) ID в разделе «Настройки -> Аккаунты и API»\n" +
                "   или задайте системную переменную окружения AURORA_MS_CLIENT_ID.\n\n" +
                "Подробная пошаговая инструкция доступна в README.md и разделе «О лаунчере».");
        }

        var (st, j) = await Net.PostFormRawAsync(DeviceEp, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = "XboxLive.signin offline_access",
        }, ct);
        if (st != 200)
            throw new AuthException("Не удалось начать вход в Microsoft: " +
                (j.Str("error_description") ?? j.Str("error") ?? $"HTTP {st}"));
        return new DeviceCodeLogin(
            j.Str("user_code") ?? "?",
            j.Str("device_code") ?? "",
            j.Str("verification_uri") ?? "https://www.microsoft.com/link",
            j.Str("verification_uri_complete"),
            j.GetInt("expires_in", 900),
            Math.Max(3, j.GetInt("interval", 5)));
    }

    public static async Task<MsToken> PollDeviceTokenAsync(DeviceCodeLogin dcl, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(dcl.ExpiresIn);
        var interval = dcl.Interval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (st, j) = await Net.PostFormRawAsync(TokenEp, new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = ClientId,
                ["device_code"] = dcl.DeviceCode,
            }, ct);
            if (st == 200)
                return new MsToken(j.Str("access_token")!, j.Str("refresh_token") ?? "");
            var err = j.Str("error") ?? "";
            if (err == "authorization_pending") { await Task.Delay(interval * 1000, ct); continue; }
            if (err == "slow_down") { interval += 5; await Task.Delay(interval * 1000, ct); continue; }
            if (err == "expired_token") throw new AuthException("Время ввода кода истекло. Попробуйте ещё раз.");
            if (err == "authorization_declined") throw new AuthException("Вход был отклонён на странице Microsoft.");
            throw new AuthException("Ошибка входа Microsoft: " + (j.Str("error_description") ?? err ?? $"HTTP {st}"));
        }
        throw new AuthException("Время входа истекло.");
    }

    public static async Task<MsToken> RefreshMsAsync(string refreshToken, CancellationToken ct)
    {
        var (st, j) = await Net.PostFormRawAsync(TokenEp, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = "XboxLive.signin offline_access",
        }, ct);
        if (st == 200)
            return new MsToken(j.Str("access_token")!, j.Str("refresh_token") ?? "");
        var err = j.Str("error") ?? "";
        if (err is "invalid_grant" or "invalid_client")
            throw new AuthException("Сессия Microsoft истекла — войдите заново.");
        throw new AuthException("Не удалось обновить токен Microsoft: " + (j.Str("error_description") ?? err));
    }

    // ---------- Шаги 2–4. Xbox → XSTS → Minecraft ----------

    /// <summary>Полная цепочка аутентификации по access-токену Microsoft. Возвращает токен Minecraft.</summary>
    public static async Task<string> GetMcTokenAsync(string msAccessToken, CancellationToken ct)
    {
        Ctx.Log("[Auth] Запрос токена Xbox Live...");
        var xblBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, object>
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msAccessToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        });
        var (xblSt, xbl) = await Net.PostJsonRawAsync("https://user.auth.xboxlive.com/user/authenticate", xblBody, ct);
        if (xblSt != 200)
            throw new AuthException("Ошибка Xbox Live: " + Net.Trunc(xbl.ToString(), 200));
        var xblToken = xbl.Str("Token") ?? throw new AuthException("Xbox Live не вернул токен");
        var uhs = xbl.El("DisplayClaims")?.Arr("xui").FirstOrDefault().Str("uhs")
                  ?? throw new AuthException("Xbox Live не вернул UHS");

        Ctx.Log("[Auth] Запрос токена XSTS для Minecraft Services...");
        var xstsBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, object>
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new[] { xblToken },
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        });
        var (xstsSt, xsts) = await Net.PostJsonRawAsync("https://xsts.auth.xboxlive.com/xsts/authorize", xstsBody, ct);
        if (xstsSt != 200)
            throw new AuthException(XstsErrorText(xsts.GetLong("XErr", 0), Net.Trunc(xsts.ToString(), 200)));
        var xstsToken = xsts.Str("Token") ?? throw new AuthException("XSTS не вернул токен");
        var xstsUhs = xsts.El("DisplayClaims")?.Arr("xui").FirstOrDefault().Str("uhs") ?? uhs;

        Ctx.Log("[Auth] Запрос токена Minecraft Services (login_with_xsts)...");
        var mcBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["identityToken"] = $"XBL3.0 x={xstsUhs};{xstsToken}",
        });
        var (mcSt, mc) = await Net.PostJsonRawAsync(
            "https://api.minecraftservices.com/authentication/login_with_xsts", mcBody, ct);
        if (mcSt != 200)
        {
            var kind = mc.Str("error") ?? mc.Str("title") ?? mc.Str("errorMessage") ?? "";
            Ctx.Log($"[Auth] Ошибка login_with_xsts: HTTP {mcSt}, ответ: {kind}");
            if (mcSt is 401 or 403 ||
                kind.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
                kind.Contains("OOB", StringComparison.OrdinalIgnoreCase))
            {
                throw new AuthException("Minecraft Services сообщил: у этой учетной записи не найдена лицензия Minecraft Java Edition или требуется вход через официальный браузер.");
            }
            throw new AuthException("Minecraft Services отклонил вход: " + Net.Trunc(kind, 200));
        }
        Ctx.Log("[Auth] Токен Minecraft успешно получен!");
        return mc.Str("access_token") ?? throw new AuthException("Minecraft Services не вернул токен");
    }

    private static string XstsErrorText(long xerr, string raw) => xerr switch
    {
        2148916233 => "У аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com и повторите вход.",
        2148916234 => "Сервис Xbox Live недоступен в вашей стране.",
        2148916235 => "Аккаунт ребёнка: попросите взрослого из семейной группы Microsoft подтвердить вход.",
        2148916236 => "Аккаунт ребёнка: необходим взрослый в семейной группе Microsoft.",
        2148916237 => "Аккаунт ребёнка: необходим взрослый в семейной группе Microsoft.",
        2148916238 => "Требуется подтверждение даты рождения в аккаунте Microsoft.",
        _ => "Ошибка XSTS: " + (xerr != 0 ? xerr.ToString() : raw),
    };

    /// <summary>
    /// Проверяет наличие подлинного игрового профиля Minecraft Java Edition.
    /// Если у аккаунта Microsoft нет лицензии Minecraft, возвращается HTTP 404.
    /// При этом проверяется владение в Microsoft Store, чтобы различить аккаунты без купленной игры
    /// и аккаунты, где игра куплена (или есть Game Pass), но имя персонажа ещё не создано.
    /// </summary>
    public static async Task<(string name, string uuid)> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        var (st, j) = await Net.SendRawAsync(req, ct);
        if (st == 404)
        {
            var ownsStore = await CheckStoreOwnershipAsync(mcToken, ct);
            if (ownsStore)
            {
                throw new AuthException("У вашей учетной записи есть лицензия Minecraft в Microsoft Store / Game Pass, но еще не создан профиль (никнейм) Java Edition. Пожалуйста, откройте minecraft.net, войдите в профиль и установите игровое имя.");
            }
            throw new AuthException("У аккаунта нет лицензии Minecraft Java Edition (профиль не найден).");
        }
        if (st != 200)
            throw new AuthException("Не удалось получить профиль Minecraft (HTTP " + st + ").");

        var name = j.Str("name");
        var id = j.Str("id");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
            throw new AuthException("Не удалось подтвердить лицензию Minecraft: профиль повреждён или отсутствует.");

        return (name, id);
    }

    /// <summary>
    /// Дополнительная проверка владения игрой через API прав владения Minecraft Store (entitlements).
    /// </summary>
    public static async Task<bool> CheckStoreOwnershipAsync(string mcToken, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/entitlements/mcstore");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            var (st, j) = await Net.SendRawAsync(req, ct);
            if (st == 200 && j.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.Str("name");
                    if (name is "product_minecraft" or "game_minecraft")
                        return true;
                }
            }
        }
        catch
        {
            // Fallback: если проверка entitlements вернула временную ошибку,
            // валидность лицензии гарантируется успешным ответом /minecraft/profile
        }
        return false;
    }

    /// <summary>Вход по завершённому device-code опросу: обновляет и сохраняет аккаунт.</summary>
    public static async Task<MinecraftAccount> LoginAsync(MsToken ms, CancellationToken ct)
    {
        var mcToken = await GetMcTokenAsync(ms.AccessToken, ct);
        var (name, uuid) = await GetProfileAsync(mcToken, ct);
        var acct = new MinecraftAccount
        {
            Username = name,
            Uuid = uuid,
            RefreshToken = ms.RefreshToken,
            McAccessToken = mcToken,
            McTokenTime = DateTimeOffset.UtcNow,
            IsOffline = false,
            AuthType = "device_code",
        };
        Accounts.Save(acct);
        return acct;
    }

    /// <summary>
    /// Офлайн/cracked режим отключен в целях соблюдения условий использования Minecraft (Mojang EULA).
    /// Лаунчер поддерживает исключительно официальную авторизацию владельцев лицензии.
    /// </summary>
    public static MinecraftAccount LoginOffline(string username)
    {
        throw new NotSupportedException(
            "Офлайн-режим обхода авторизации удалён в соответствии с требованиями Mojang Studios и Microsoft. " +
            "Для запуска игры требуется официальная лицензия Minecraft Java Edition и вход через Microsoft.");
    }

    /// <summary>Возвращает действующий токен Minecraft, при необходимости обновляя всю цепочку.</summary>
    public static async Task<string> EnsureFreshMcTokenAsync(CancellationToken ct)
    {
        var a = Ctx.Account ?? throw new AuthException("Нет сохранённого аккаунта — войдите через Microsoft.");
        if (a.IsOffline)
            throw new AuthException("Неподтверждённые/офлайн профили не поддерживаются. Войдите через Microsoft с подлинной лицензией.");
        if (a.McAccessToken.Length > 0 && a.McTokenTime > DateTimeOffset.UtcNow.AddHours(-12))
            return a.McAccessToken;

        if (a.AuthType.StartsWith("imported_") || a.AuthType == "modrinth_imported")
        {
            var reimported = ExternalLaunchersImporter.TryImportBest();
            if (reimported != null && !string.IsNullOrEmpty(reimported.McAccessToken))
            {
                a.McAccessToken = reimported.McAccessToken;
                a.RefreshToken = reimported.RefreshToken;
                a.McTokenTime = DateTimeOffset.UtcNow;
                Accounts.Save(a);
                return a.McAccessToken;
            }
            if (a.McAccessToken.Length > 0)
                return a.McAccessToken;
        }

        var ms = await RefreshMsAsync(a.RefreshToken, ct);
        var mcToken = await GetMcTokenAsync(ms.AccessToken, ct);
        try
        {
            var (name, uuid) = await GetProfileAsync(mcToken, ct);
            a.Username = name;
            a.Uuid = uuid;
        }
        catch { /* профиль мог не измениться — не критично */ }
        a.RefreshToken = ms.RefreshToken;
        a.McAccessToken = mcToken;
        a.McTokenTime = DateTimeOffset.UtcNow;
        Accounts.Save(a);
        return mcToken;
    }
}

/// <summary>Хранилище аккаунта: JSON, зашифрованный DPAPI, с правами только для текущего пользователя.</summary>
public static class Accounts
{
    public static MinecraftAccount? Load()
    {
        try
        {
            if (!File.Exists(Paths.AccountsFile)) return null;
            var plain = Secure.Unprotect(File.ReadAllBytes(Paths.AccountsFile));
            return JsonSerializer.Deserialize<MinecraftAccount>(plain);
        }
        catch { return null; }
    }

    public static void Save(MinecraftAccount a)
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllBytes(Paths.AccountsFile, Secure.Protect(JsonSerializer.SerializeToUtf8Bytes(a)));
        Secure.RestrictToCurrentUser(Paths.AccountsFile);
    }

    public static void Delete()
    {
        Net.TryDelete(Paths.AccountsFile);
    }
}
