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
}

/// <summary>
/// Официальная авторизация Microsoft (device code flow) → Xbox Live → XSTS → Minecraft Services.
/// Пароль пользователя никогда не попадает в лаунчер: подтверждение происходит на странице Microsoft.
/// </summary>
public static class Auth
{
    private const string DeviceEp = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEp = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

    public static string ClientId => string.IsNullOrWhiteSpace(Ctx.Settings.MsClientId)
        ? Settings.DefaultMsClientId
        : Ctx.Settings.MsClientId.Trim();

    // ---------- Шаг 1. Microsoft (device code) ----------

    public static async Task<DeviceCodeLogin> BeginDeviceLoginAsync(CancellationToken ct)
    {
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

        var mcBody = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}",
        });
        var (mcSt, mc) = await Net.PostJsonRawAsync(
            "https://api.minecraftservices.com/authentication/login_with_xsts", mcBody, ct);
        if (mcSt != 200)
        {
            var kind = mc.Str("error") ?? mc.Str("title") ?? "";
            if (kind.Contains("OOB", StringComparison.OrdinalIgnoreCase) ||
                (mcSt == 403 && kind.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
                throw new AuthException("У аккаунта Microsoft нет лицензии Minecraft Java Edition.");
            throw new AuthException("Minecraft Services отклонил вход: " + Net.Trunc(mc.ToString(), 200));
        }
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

    public static async Task<(string name, string uuid)> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
        var (st, j) = await Net.SendRawAsync(req, ct);
        if (st == 404)
            throw new AuthException("У аккаунта нет профиля Minecraft (нужна лицензия Java Edition).");
        if (st != 200)
            throw new AuthException("Не удалось получить профиль Minecraft (HTTP " + st + ").");
        return (j.Str("name") ?? "Player", j.Str("id") ?? "");
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
        };
        Accounts.Save(acct);
        return acct;
    }

    /// <summary>Возвращает действующий токен Minecraft, при необходимости обновляя всю цепочку.</summary>
    public static async Task<string> EnsureFreshMcTokenAsync(CancellationToken ct)
    {
        var a = Ctx.Account ?? throw new AuthException("Нет сохранённого аккаунта — войдите через Microsoft.");
        if (a.McAccessToken.Length > 0 && a.McTokenTime > DateTimeOffset.UtcNow.AddHours(-8))
            return a.McAccessToken;
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
