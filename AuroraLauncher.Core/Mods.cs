using System.IO;
using System.Text.Json;

namespace AuroraLauncher.Core;

public sealed record ModHit(string Id, string Title, string Author, string Description, string Downloads,
    string IconUrl, string Source, string Slug = "")
{
    public string SourceShort => Source;
    public string DisplayAuthor => string.IsNullOrEmpty(Author) ? Source : $"{Source} · {Author}";
}

/// <summary>
/// Поиск и установка модов. Основной источник — Modrinth (официальный API, без ключей);
/// CurseForge — по API-ключу из настроек. Файлы проверяются по SHA-1 и кладутся в mods/ инстанса.
/// </summary>
public static class Mods
{
    // ================= Поиск =================

    public static async Task<List<ModHit>> SearchModrinthAsync(string query, string mcVersion, string? loader, CancellationToken ct, int offset = 0, int limit = 20)
    {
        if (loader == "vanilla") return new List<ModHit>();

        // facets — JSON-массив массивов; собираем вручную для гарантированной корректности
        var facets = "[[\"versions:" + mcVersion + "\"],[\"project_type:mod\"]";
        if (!string.IsNullOrEmpty(loader) && loader != "vanilla")
            facets += ",[\"categories:" + loader.ToLowerInvariant() + "\"]";
        facets += "]";

        var indexParam = string.IsNullOrWhiteSpace(query) ? "index=downloads" : "index=relevance";
        var queryParam = string.IsNullOrWhiteSpace(query) ? "" : "&query=" + Uri.EscapeDataString(query.Trim());

        var url = $"https://api.modrinth.com/v2/search?limit={limit}&offset={offset}&{indexParam}{queryParam}&facets={Uri.EscapeDataString(facets)}";

        var j = await Net.GetJsonAsync(url, ct);
        var res = new List<ModHit>();
        foreach (var h in j.Arr("hits"))
        {
            var slug = h.Str("slug") ?? "";
            var projId = h.Str("project_id") ?? slug;
            var id = !string.IsNullOrEmpty(slug) ? slug : projId;
            if (id.Length == 0) continue;
            res.Add(new ModHit(
                "modrinth:" + id,
                h.Str("title") ?? "?",
                h.Str("author") ?? "",
                Net.Trunc(h.Str("description") ?? "", 150),
                FormatDownloads(h.GetLong("downloads")),
                h.Str("icon_url") ?? "",
                "Modrinth",
                slug));
        }
        return res;
    }

    public static async Task<List<ModHit>> SearchCurseForgeAsync(string apiKey, string query, string mcVersion, string? loader, CancellationToken ct, int offset = 0, int limit = 20)
    {
        if (loader == "vanilla") return new List<ModHit>();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Для CurseForge укажите API-ключ в настройках (бесплатный: console.curseforge.com).");

        var loaderParam = loader switch
        {
            "forge" => "&modLoaderType=1",
            "fabric" => "&modLoaderType=4",
            "quilt" => "&modLoaderType=5",
            "neoforge" => "&modLoaderType=6",
            _ => ""
        };

        var url = "https://api.curseforge.com/v1/mods/search" +
                  $"?gameId=432&classId=6&sortField=2&sortOrder=desc&index={offset}&pageSize={limit}" +
                  loaderParam +
                  (string.IsNullOrWhiteSpace(query) ? "" : "&searchFilter=" + Uri.EscapeDataString(query.Trim())) +
                  "&gameVersion=" + Uri.EscapeDataString(mcVersion);
        var (st, j) = await SendCurseForgeAsync(HttpMethod.Get, url, apiKey, ct);
        if (st == 403)
            throw new InvalidOperationException("CurseForge отклонил ключ (403) — проверьте API-ключ в настройках.");
        if (st != 200)
            throw new HttpRequestException($"CurseForge: HTTP {st}");

        var res = new List<ModHit>();
        foreach (var d in j.Arr("data"))
        {
            var id = d.GetLong("id").ToString();
            var slug = d.Str("slug") ?? "";
            if (id == "0") continue;
            res.Add(new ModHit(
                "curseforge:" + id,
                d.Str("name") ?? "?",
                d.Arr("authors").Select(a => a.Str("name") ?? "").FirstOrDefault() ?? "",
                Net.Trunc(d.Str("summary") ?? "", 150),
                FormatDownloads(d.GetLong("downloadCount")),
                d.El("logo")?.Str("thumbnailUrl") ?? d.El("logo")?.Str("url") ?? "",
                "CurseForge",
                slug));
        }
        return res;
    }

    private static string FormatDownloads(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#") + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.#") + "K",
        _ => n.ToString(),
    };

    // ================= Установка =================

    /// <summary>Устанавливает последнюю совместимую версию мода в mods/ инстанса. Возвращает имя файла.</summary>
    public static async Task<string> InstallAsync(string hitId, Instance inst, CancellationToken ct)
    {
        var parts = hitId.Split(':', 2);
        return parts[0] switch
        {
            "modrinth" => await InstallModrinthAsync(parts[1], inst, ct),
            "curseforge" when parts.Length > 1 && long.TryParse(parts[1], out var modId) => await InstallCurseForgeAsync(modId, inst, ct),
            _ => throw new InvalidOperationException("Неизвестный источник мода: " + hitId),
        };
    }

    private static async Task<string> InstallModrinthAsync(string projectId, Instance inst, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version" +
            "?game_versions=%5B%22" + Uri.EscapeDataString(inst.McVersion) + "%22%5D");
        var loader = inst.Loader == "vanilla" ? null : inst.Loader.ToLowerInvariant();
        if (loader != null)
            sb.Append("&loaders=%5B%22").Append(loader).Append("%22%5D");

        var versions = await Net.GetJsonAsync(sb.ToString(), ct);
        var v = versions.EnumerateArray().FirstOrDefault();
        if (v.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Нет версии мода для Minecraft {inst.McVersion}" +
                (loader != null ? $" / {loader}" : ""));

        var file = PickFile(v);
        var fname = SanitizeName(file.Str("filename") ?? "mod.jar");
        var dest = Path.Combine(ModsDir(inst), fname);
        if (!File.Exists(dest))
        {
            var url = file.Str("url") ?? throw new InvalidOperationException("у файла мода нет ссылки");
            await Net.DownloadFileAsync(url, dest, file.El("hashes")?.Str("sha1"), ct: ct);
        }
        return fname;
    }

    private static async Task<string> InstallCurseForgeAsync(long modId, Instance inst, CancellationToken ct)
    {
        var apiKey = Ctx.Settings.CurseApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Для CurseForge укажите API-ключ в настройках.");

        var loader = inst.Loader == "vanilla" ? 0 : inst.Loader switch
        {
            "forge" => 1,
            "fabric" => 4,
            "neoforge" => 6,
            _ => 0,
        };
        var url = $"https://api.curseforge.com/v1/mods/{modId}/files" +
                  $"?gameVersion={Uri.EscapeDataString(inst.McVersion)}&modLoaderFilter={loader}";
        var (st, j) = await SendCurseForgeAsync(HttpMethod.Get, url, apiKey, ct);
        if (st != 200) throw new HttpRequestException($"CurseForge: HTTP {st}");

        var file = j.Arr("data")
            .Where(f => f.Arr("gameVersions").Any(g => g.GetString() == inst.McVersion))
            .OrderByDescending(f => f.Str("fileDate"))
            .FirstOrDefault();
        if (file.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Нет файлов мода для Minecraft {inst.McVersion}");

        var fname = SanitizeName(file.Str("fileName") ?? "mod.jar");
        var dest = Path.Combine(ModsDir(inst), fname);
        if (!File.Exists(dest))
        {
            var url2 = file.Str("downloadUrl");
            if (string.IsNullOrEmpty(url2))
            {
                var fileId = file.GetLong("id");
                var download = await SendCurseForgeAsync(HttpMethod.Get,
                    $"https://api.curseforge.com/v1/mods/{modId}/files/{fileId}/download-url", apiKey, ct);
                if (download.status == 200 && download.json.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                    url2 = data.GetString();
            }
            if (string.IsNullOrWhiteSpace(url2))
                throw new InvalidOperationException("CurseForge не предоставил ссылку на этот файл. Попробуйте другой мод или Modrinth.");
            await Net.DownloadFileAsync(url2, dest, null, ct: ct);
        }
        return fname;
    }

    private static JsonElement PickFile(JsonElement version)
    {
        var files = version.Arr("files");
        var primary = files.FirstOrDefault(f => f.GetBool("primary"));
        if (primary.ValueKind == JsonValueKind.Object) return primary;
        var jar = files.FirstOrDefault(f => (f.Str("filename") ?? "").EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        return jar.ValueKind == JsonValueKind.Object ? jar : files.FirstOrDefault();
    }

    private static Task<(int status, JsonElement json)> SendCurseForgeAsync(HttpMethod method, string url, string apiKey, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return Net.SendRawAsync(request, ct);
    }

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string ModsDir(Instance inst)
    {
        var dir = Path.Combine(inst.GameDir, "mods");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ================= Управление установленными =================

    public static List<string> Installed(Instance inst)
    {
        var dir = Path.Combine(inst.GameDir, "mods");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.jar").Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();
    }

    public static Dictionary<string, string> GetInstalledModsMeta(Instance inst)
    {
        try
        {
            var metaFile = Path.Combine(inst.GameDir, "mods_meta.json");
            if (File.Exists(metaFile))
            {
                var json = File.ReadAllText(metaFile);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { }
        return new Dictionary<string, string>();
    }

    public static void RecordInstalledMod(Instance inst, string hitId, string slug, string title, string fileName)
    {
        try
        {
            var metaFile = Path.Combine(inst.GameDir, "mods_meta.json");
            var dict = GetInstalledModsMeta(inst);

            if (!string.IsNullOrEmpty(hitId)) dict[hitId.ToLowerInvariant()] = fileName;
            if (!string.IsNullOrEmpty(slug)) dict[slug.ToLowerInvariant()] = fileName;
            if (!string.IsNullOrEmpty(title)) dict[title.ToLowerInvariant()] = fileName;

            File.WriteAllText(metaFile, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void RemoveInstalledModRecord(Instance inst, string fileName)
    {
        try
        {
            var metaFile = Path.Combine(inst.GameDir, "mods_meta.json");
            if (File.Exists(metaFile))
            {
                var dict = GetInstalledModsMeta(inst);
                var toRemove = dict.Where(kv => kv.Value.Equals(fileName, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                foreach (var k in toRemove) dict.Remove(k);
                File.WriteAllText(metaFile, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    public static void Delete(Instance inst, string fileName)
    {
        var p = Path.Combine(inst.GameDir, "mods", fileName);
        if (File.Exists(p)) Net.TryDelete(p);
        RemoveInstalledModRecord(inst, fileName);
    }
}
