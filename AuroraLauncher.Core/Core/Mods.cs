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

    public static async Task<List<ModHit>> SearchModrinthAsync(string query, string mcVersion, string? loader, CancellationToken ct, int offset = 0, int limit = 30)
    {
        if (loader == "vanilla") return new List<ModHit>();

        var facets = $"[[\"versions:{mcVersion}\"],[\"project_type:mod\"]]";
        if (!string.IsNullOrEmpty(loader))
            facets = $"[[\"versions:{mcVersion}\"],[\"categories:{loader.ToLower()}\"],[\"project_type:mod\"]]";
        var queryParam = string.IsNullOrWhiteSpace(query) ? "" : $"query={Uri.EscapeDataString(query.Trim())}&";
        var indexParam = string.IsNullOrWhiteSpace(query) ? "index=downloads&" : "index=relevance&";
        var url = $"https://api.modrinth.com/v2/search?{queryParam}{indexParam}offset={offset}&limit={limit}&facets={Uri.EscapeDataString(facets)}";
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
                loader ?? ""));
        }
        return res;
    }

    public static async Task<List<ModHit>> SearchCurseForgeAsync(string apiKey, string query, string mcVersion, string? loader, CancellationToken ct, int offset = 0, int limit = 30)
    {
        if (loader == "vanilla") return new List<ModHit>();
        var loaderParam = loader switch
        {
            "forge" => "&modLoaderType=1",
            "fabric" => "&modLoaderType=4",
            "quilt" => "&modLoaderType=5",
            "neoforge" => "&modLoaderType=6",
            _ => ""
        };
        var req = new HttpRequestMessage(HttpMethod.Get,
            "https://api.curseforge.com/v1/mods/search?gameId=432&classId=6&searchFilter=" + Uri.EscapeDataString(query) +
            "&gameVersion=" + Uri.EscapeDataString(mcVersion) + loaderParam + $"&sort=1&index={offset}&pageSize={limit}");
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
            // 1. Пытаемся получить точные версии для текущей игры и загрузчика
            var url = string.IsNullOrEmpty(loaderFolder)
                ? $"https://api.modrinth.com/v2/project/{id}/version?game_versions=[\"{inst.McVersion}\"]"
                : $"https://api.modrinth.com/v2/project/{id}/version?loaders=[\"{loaderFolder}\"]&game_versions=[\"{inst.McVersion}\"]";
            var j = await Net.GetJsonAsync(url, ct);
            var v = j.EnumerateArray().FirstOrDefault();

            // 2. Фолбэк: если точной версии нет в фильтре, запрашиваем версии под данный загрузчик и адаптируем
            if (v.ValueKind != JsonValueKind.Object)
            {
                var allUrl = string.IsNullOrEmpty(loaderFolder)
                    ? $"https://api.modrinth.com/v2/project/{id}/version"
                    : $"https://api.modrinth.com/v2/project/{id}/version?loaders=[\"{loaderFolder}\"]";
                var allJ = await Net.GetJsonAsync(allUrl, ct);
                v = allJ.EnumerateArray().FirstOrDefault(elem =>
                {
                    var gv = elem.Arr("game_versions").Select(x => x.GetString() ?? "").ToList();
                    return gv.Contains(inst.McVersion) ||
                           gv.Any(x => !string.IsNullOrEmpty(x) && (inst.McVersion.StartsWith(x) || x.StartsWith(inst.McVersion)));
                });

                if (v.ValueKind != JsonValueKind.Object)
                    v = allJ.EnumerateArray().FirstOrDefault();
            }

            if (v.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Нет совместимой версии мода для Minecraft {inst.McVersion} ({inst.Loader})");

            var file = v.Arr("files").FirstOrDefault(f => f.GetBool("primary")) is { } pf ? pf : v.Arr("files").FirstOrDefault();
            if (file.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Файл мода не найден в выбранной версии");

            var fname = file.Str("filename")!;
            var dest = Path.Combine(modsDir, fname);
            if (!File.Exists(dest))
                await Net.DownloadFileAsync(file.Str("url")!, dest, file.El("hashes")?.Str("sha1"), ct: ct);

            // Проверяем требования к Java в установленном моде и заблаговременно подготавливаем Java
            PrecacheModJavaRequirement(dest);

            return fname;
        }

        if (source == "curseforge")
        {
            var apiKey = Ctx.Settings.CurseApiKey;
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("Для CurseForge укажите API-ключ в настройках");
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.curseforge.com/v1/mods/{id}/files?gameVersion={Uri.EscapeDataString(inst.McVersion)}&modLoaderFilter={LoaderFilter(inst.Loader)}");
            req.Headers.Add("x-api-key", apiKey);
            var (st, j) = await Net.SendRawAsync(req, ct);
            if (st != 200) throw new HttpRequestException($"CurseForge: HTTP {st}");

            var files = j.Arr("data")
                .Where(f => f.Arr("gameVersions").Any(g => g.GetString() == inst.McVersion))
                .ToList();
            if (files.Count == 0)
            {
                files = j.Arr("data")
                    .Where(f => f.Arr("gameVersions").Any(g =>
                    {
                        var s = g.GetString();
                        return s != null && (inst.McVersion.StartsWith(s) || s.StartsWith(inst.McVersion));
                    }))
                    .ToList();
            }

            var f = files.FirstOrDefault();
            if (f.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Нет файлов мода для этой версии игры");

            var fname = f.Str("fileName")!;
            var dest = Path.Combine(modsDir, fname);
            if (!File.Exists(dest))
                await Net.DownloadFileAsync(f.Str("downloadUrl")!, dest, null, ct: ct);

            PrecacheModJavaRequirement(dest);

            return fname;
        }

        throw new InvalidOperationException("Неизвестный источник мода: " + source);
    }

    private static void PrecacheModJavaRequirement(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            var fEntry = zip.GetEntry("fabric.mod.json");
            if (fEntry != null)
            {
                using var s = fEntry.Open();
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("depends", out var dep) && dep.TryGetProperty("java", out var jDep))
                {
                    var val = jDep.ValueKind == JsonValueKind.String ? jDep.GetString() : jDep.ToString();
                    if (val != null)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(val, @"(?:>=|>|=)\s*(\d+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out var reqJava) && reqJava > 21)
                        {
                            Ctx.Log($"[Mods] Мод {Path.GetFileName(jarPath)} требует Java {reqJava}+. Фоновая подготовка рантайма...");
                            _ = Task.Run(async () =>
                            {
                                try { await Runtimes.EnsureJavaMajorAsync(reqJava, new Progress<ProgressReport>(), CancellationToken.None); } catch { }
                            });
                        }
                    }
                }
            }
        }
        catch { }
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

    public static void OpenFolder(Instance inst)
    {
        var dir = Path.Combine(inst.GameDir, "mods");
        Directory.CreateDirectory(dir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
        catch { }
    }
}
