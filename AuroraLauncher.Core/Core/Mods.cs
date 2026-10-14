using System.IO;
using System.Text.Json;

namespace AuroraLauncher.Core;

public sealed record ModHit(string Id, string Title, string Author, string Description, string Downloads,
    string IconUrl, string Source, string VersionsInfo);

/// <summary>
/// Поиск и установка модов. Основной источник — Modrinth (официальный API, без ключей).
/// Поддержан и CurseForge (API-ключ вводится в настройках лаунчера).
/// Файлы модов проверяются по SHA-1 из ответа API и кладутся в mods/ инстанса.
/// </summary>
public static class Mods
{
    // ---------- Поиск ----------

    public static async Task<List<ModHit>> SearchModrinthAsync(string query, string mcVersion, string? loader, CancellationToken ct)
    {
        var facets = $"[[\"versions:{mcVersion}\"],[\"project_type:mod\"]]";
        if (!string.IsNullOrEmpty(loader))
            facets = $"[[\"versions:{mcVersion}\"],[\"categories:{loader.ToLower()}\"],[\"project_type:mod\"]]";
        var url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query)}&limit=40&index=relevance&facets={Uri.EscapeDataString(facets)}";
        var j = await Net.GetJsonAsync(url, ct);
        var res = new List<ModHit>();
        foreach (var h in j.Arr("hits"))
        {
            var author = h.Str("author") ?? "";
            res.Add(new ModHit(
                "modrinth:" + (h.Str("project_id") ?? ""),
                h.Str("title") ?? "?",
                author,
                Net.Trunc(h.Str("description") ?? "", 160),
                FormatDownloads(h.GetLong("downloads")),
                h.Str("icon_url") ?? "",
                "Modrinth",
                h.Str("versions")?.Length > 0 ? "" : ""));
        }
        return res;
    }

    public static async Task<List<ModHit>> SearchCurseForgeAsync(string apiKey, string query, string mcVersion, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&searchFilter=" + Uri.EscapeDataString(query) +
            "&gameVersion=" + Uri.EscapeDataString(mcVersion) + "&sort=1&pageSize=40");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Accept.ParseAdd("application/json");
        var (st, j) = await Net.SendRawAsync(req, ct);
        if (st != 200) throw new HttpRequestException($"CurseForge: HTTP {st} (проверьте API-ключ в настройках)");
        var res = new List<ModHit>();
        foreach (var d in j.Arr("data"))
        {
            var authors = d.Arr("authors").Select(a => a.Str("name") ?? "").FirstOrDefault() ?? "";
            res.Add(new ModHit(
                "curseforge:" + d.GetLong("id").ToString(),
                d.Str("name") ?? "?",
                authors,
                Net.Trunc(d.Str("summary") ?? "", 160),
                FormatDownloads(d.GetLong("downloadCount")),
                d.El("logo")?.Str("thumbnailUrl") ?? "",
                "CurseForge",
                ""));
        }
        return res;
    }

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.#") + "K",
        _ => n.ToString(),
    };

    // ---------- Установка ----------

    /// <summary>Устанавливает последнюю совместимую версию мода в папку mods инстанса.</summary>
    public static async Task<string> InstallAsync(string hitId, Instance inst, CancellationToken ct)
    {
        var modsDir = Path.Combine(inst.GameDir, "mods");
        Directory.CreateDirectory(modsDir);
        var parts = hitId.Split(':', 2);
        var source = parts[0];
        var id = parts.Length > 1 ? parts[1] : hitId;

        var loaderFolder = inst.Loader switch
        {
            "forge" => "forge",
            "neoforge" => "neoforge",
            "fabric" => "fabric",
            _ => "",
        };

        if (source == "modrinth")
        {
            // Версии проекта, отфильтрованные по игре и загрузчику
            var url = $"https://api.modrinth.com/v2/project/{id}/version?loaders=[\"{loaderFolder}\"]&game_versions=[\"{inst.McVersion}\"]";
            var j = await Net.GetJsonAsync(url, ct);
            var v = j.EnumerateArray().FirstOrDefault();
            if (v.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Нет версии мода для этой игры/загрузчика");
            var file = v.Arr("files").FirstOrDefault(f => f.GetBool("primary")) is { } pf ? pf : v.Arr("files").FirstOrDefault();
            if (file.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Файл мода не найден");
            var fname = file.Str("filename")!;
            var dest = Path.Combine(modsDir, fname);
            if (!File.Exists(dest))
                await Net.DownloadFileAsync(file.Str("url")!, dest, file.El("hashes")?.Str("sha1"), ct: ct);
            return fname;
        }

        if (source == "curseforge")
        {
            var apiKey = Ctx.Settings.CurseApiKey;
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Для CurseForge укажите API-ключ в настройках");
            // Файлы проекта, отсортированные по дате; берём первый под нашу версию игры
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.curseforge.com/v1/mods/{id}/files?gameVersion={Uri.EscapeDataString(inst.McVersion)}&modLoaderFilter={LoaderFilter(inst.Loader)}");
            req.Headers.Add("x-api-key", apiKey);
            var (st, j) = await Net.SendRawAsync(req, ct);
            if (st != 200) throw new HttpRequestException($"CurseForge: HTTP {st}");
            var files = j.Arr("data")
                .Where(f => f.Arr("gameVersions").Any(g => g.GetString() == inst.McVersion))
                .ToList();
            var f = files.FirstOrDefault();
            if (f.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Нет файлов мода для этой версии игры");
            var fname = f.Str("fileName")!;
            var dest = Path.Combine(modsDir, fname);
            if (!File.Exists(dest))
                await Net.DownloadFileAsync(f.Str("downloadUrl")!, dest, null, ct: ct);
            return fname;
        }

        throw new InvalidOperationException("Неизвестный источник мода: " + source);
    }

    private static int LoaderFilter(string loader) => loader switch
    {
        "forge" => 1,
        "neoforge" => 6,
        "fabric" => 4,
        _ => 0,
    };

    /// <summary>Список уже установленных модов инстанса.</summary>
    public static List<string> Installed(Instance inst)
    {
        var dir = Path.Combine(inst.GameDir, "mods");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).Where(n => n != null).Select(n => n!).ToList();
    }

    public static void Delete(Instance inst, string fileName)
    {
        var p = Path.Combine(inst.GameDir, "mods", fileName);
        if (File.Exists(p)) Net.TryDelete(p);
    }
}
