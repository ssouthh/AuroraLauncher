using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Отчёт о прогрессе длительной операции. Percent: 0..100 или -1 (неопределён).</summary>
public class ProgressReport
{
    public string Phase = "";
    public string Detail = "";
    public int Percent = -1;
}

/// <summary>HTTP-слой лаунчера: единый HttpClient, повторные попытки, проверка SHA-1/SHA-512.</summary>
public static class Net
{
    public const string UserAgent = "AuroraLauncher/1.0 (personal Minecraft launcher)";

    private static readonly HttpClient Http;

    static Net()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        };
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static HttpClient Client => Http;

    /// <summary>Выполняет запрос и возвращает статус + распарсенный JSON (default при не-JSON).</summary>
    public static async Task<(int status, JsonElement json)> SendRawAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        using (var resp = await Http.SendAsync(req, ct))
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            JsonElement j = default;
            try { using var doc = JsonDocument.Parse(body); j = doc.RootElement.Clone(); } catch { /* не JSON */ }
            return ((int)resp.StatusCode, j);
        }
    }

    public static Task<(int, JsonElement)> PostFormRawAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        return SendRawAsync(req, ct);
    }

    public static Task<(int, JsonElement)> PostJsonRawAsync(string url, string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return SendRawAsync(req, ct);
    }

    public static async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct = default)
    {
        var (st, j) = await SendRawAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (st != 200) throw new HttpRequestException($"HTTP {st}: {url}");
        return j;
    }

    public static async Task<JsonElement> PostJsonAsync(string url, string json, CancellationToken ct = default)
    {
        var (st, j) = await PostJsonRawAsync(url, json, ct);
        if (st < 200 || st >= 300) throw new HttpRequestException($"HTTP {st} ({url}): {Net.Trunc(j.ToString(), 300)}");
        return j;
    }

    public static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// Скачивает файл с повторными попытками и проверкой контрольных сумм.
    /// Пишет во временный .part и перемещает на место только после успешной сверки.
    /// </summary>
    public static async Task DownloadFileAsync(string url, string destPath, string? sha1 = null, string? sha512 = null,
        long expectedSize = -1, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tmp = destPath + ".part";
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (File.Exists(tmp)) TryDelete(tmp);
                using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                    {
                        await src.CopyToAsync(fs, 81920, ct);
                    }
                }
                if (expectedSize > 0)
                {
                    var got = new FileInfo(tmp).Length;
                    if (got != expectedSize) throw new InvalidDataException($"размер файла {got} байт, ожидался {expectedSize}");
                }
                if (sha1 != null)
                {
                    var actual = await HashFileAsync(tmp, SHA1.Create(), ct);
                    if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"несовпадение SHA-1: {Path.GetFileName(destPath)}");
                }
                if (sha512 != null)
                {
                    var actual = await HashFileAsync(tmp, SHA512.Create(), ct);
                    if (!string.Equals(actual, sha512, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"несовпадение SHA-512: {Path.GetFileName(destPath)}");
                }
                File.Move(tmp, destPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       && attempt < 4 && !ct.IsCancellationRequested)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), CancellationToken.None);
            }
        }
        throw last ?? new IOException("не удалось скачать " + url);
    }

    public static async Task<string> HashFileAsync(string path, System.Security.Cryptography.HashAlgorithm alg, CancellationToken ct)
    {
        string hex;
        using (alg)
        await using (var fs = File.OpenRead(path))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0) alg.TransformBlock(buf, 0, n, null, 0);
            alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            hex = Convert.ToHexString(alg.Hash!).ToLowerInvariant();
        }
        return hex;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* занят — попробуем в следующий раз */ }
    }

    /// <summary>Прогресс-обёртка: переназначает процент из диапазона 0..100 в поддиапазон from..to.</summary>
    public static IProgress<ProgressReport> Slice(this IProgress<ProgressReport> inner, int from, int to, string? phaseOverride = null)
        => new Progress<ProgressReport>(r => inner.Report(new ProgressReport
        {
            Phase = phaseOverride ?? r.Phase,
            Detail = r.Detail,
            Percent = r.Percent < 0 ? -1 : from + (to - from) * r.Percent / 100,
        }));
}
