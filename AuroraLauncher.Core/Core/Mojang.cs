using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Библиотека из версии Minecraft: путь, источник, контрольная сумма, признак нативной.</summary>
public sealed record LibRef(string RelPath, string Url, string? Sha1, long Size, bool Native, string[] Exclude)
{
    public string AbsPath(string librariesRoot)
        => Path.Combine(librariesRoot, RelPath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Скачивание и установка ванильных версий Minecraft (официальные серверы Mojang).</summary>
public static class Mojang
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string AssetsCdn = "https://resources.download.minecraft.net/";
    private const string LibrariesCdn = "https://libraries.minecraft.net/";

    private static JsonElement? _manifest;
    private static DateTimeOffset _manifestAt;

    public static async Task<JsonElement> GetManifestAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _manifest != null && (DateTimeOffset.UtcNow - _manifestAt).TotalMinutes < 15)
            return _manifest.Value;

        var cacheFile = Path.Combine(Paths.CacheDir, "version_manifest_v2.json");
        JsonElement manifest = default;
        var fromCache = false;
        try
        {
            if (!force && File.Exists(cacheFile) &&
                File.GetLastWriteTimeUtc(cacheFile) > DateTime.UtcNow.AddHours(-12))
            {
                manifest = JsonExt.ParseFile(cacheFile);
                fromCache = true;
            }
        }
        catch { /* кэш повреждён — качаем заново */ }

        if (manifest.ValueKind == JsonValueKind.Undefined)
        {
            manifest = await Net.GetJsonAsync(ManifestUrl, ct);
            Directory.CreateDirectory(Paths.CacheDir);
            await File.WriteAllTextAsync(cacheFile, manifest.ToString(), ct);
        }
        _ = fromCache;
        _manifest = manifest;
        _manifestAt = DateTimeOffset.UtcNow;
        return manifest;
    }

    public static string? FindVersionUrl(JsonElement manifest, string id)
    {
        foreach (var v in manifest.Arr("versions"))
            if (v.Str("id") == id) return v.Str("url");
        return null;
    }

    /// <summary>Загружает json версии (из кэша-файла или манифеста) и сохраняет в versions/{id}/{id}.json.</summary>
    public static async Task<JsonElement> FetchVersionJsonAsync(string id, string versionsRoot, bool refresh = false, CancellationToken ct = default)
    {
        var file = Path.Combine(versionsRoot, id, id + ".json");
        if (!refresh && File.Exists(file)) return JsonExt.ParseFile(file);

        var url = FindVersionUrl(await GetManifestAsync(ct: ct), id)
                  ?? throw new InvalidOperationException($"Версия {id} не найдена в манифесте Mojang");
        var j = await Net.GetJsonAsync(url, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(j, new JsonSerializerOptions { WriteIndented = true }), ct);
        return j;
    }

    /// <summary>Устанавливает ванильную версию: json, клиент, библиотеки, ресурсы, конфиг логирования.</summary>
    public static async Task InstallVanillaAsync(string versionId, string mcDir, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var versionsRoot = Path.Combine(mcDir, "versions");
        var librariesRoot = Path.Combine(mcDir, "libraries");

        prog.Report(new ProgressReport { Phase = "Манифест версии", Percent = 2 });
        var vj = await FetchVersionJsonAsync(versionId, versionsRoot, ct: ct);

        // Клиентский jar
        prog.Report(new ProgressReport { Phase = "Клиент Minecraft", Percent = 6 });
        var client = vj.El("downloads")?.El("client")
                     ?? throw new InvalidDataException($"у версии {versionId} нет клиентского jar");
        var clientPath = Path.Combine(versionsRoot, versionId, versionId + ".jar");
        if (!File.Exists(clientPath) || new FileInfo(clientPath).Length != client.GetLong("size", -1))
            await Net.DownloadFileAsync(client.Str("url")!, clientPath, client.Str("sha1"),
                expectedSize: client.GetLong("size", -1), ct: ct);

        // Библиотеки (включая нативные классификаторы старых версий)
        var libs = CollectLibraries(vj);
        var libDir = 0;
        var libErrs = new ConcurrentBag<Exception>();
        prog.Report(new ProgressReport { Phase = "Библиотеки", Detail = $"0/{libs.Count}", Percent = 12 });
        await Parallel.ForEachAsync(libs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (lib, t) =>
            {
                try
                {
                    var dest = lib.AbsPath(librariesRoot);
                    var ok = File.Exists(dest) && (lib.Size <= 0 || new FileInfo(dest).Length == lib.Size);
                    if (!ok) await Net.DownloadFileAsync(lib.Url, dest, lib.Sha1, expectedSize: lib.Size, ct: t);
                }
                catch (Exception ex) { libErrs.Add(ex); }
                finally
                {
                    var d = Interlocked.Increment(ref libDir);
                    prog.Report(new ProgressReport
                    {
                        Phase = "Библиотеки",
                        Detail = $"{d}/{libs.Count}",
                        Percent = 12 + (int)(26.0 * d / Math.Max(1, libs.Count)),
                    });
                }
            });
        if (!libErrs.IsEmpty) throw new AggregateException("не скачались библиотеки", libErrs.Take(3));

        // Ресурсы (assets)
        await InstallAssetsAsync(vj, mcDir, prog, ct);

        // Конфиг log4j
        var logCfg = vj.El("logging")?.El("client");
        if (logCfg != null)
        {
            prog.Report(new ProgressReport { Phase = "Конфиг логирования", Percent = 98 });
            var lf = logCfg.El("file")!;
            var id = lf.Str("id")!;
            var dest = Path.Combine(Paths.AssetsDir, "log_configs", id);
            if (!File.Exists(dest))
                await Net.DownloadFileAsync(lf.Str("url")!, dest, lf.Str("sha1"), ct: ct);
        }

        // Распаковка нативных библиотек старых версий (<1.19)
        ExtractNatives(vj, Path.Combine(versionsRoot, versionId, "natives"), librariesRoot);

        prog.Report(new ProgressReport { Phase = "Ванильная версия готова", Percent = 100 });
    }

    private static async Task InstallAssetsAsync(JsonElement vj, string mcDir, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var ai = vj.El("assetIndex") ?? throw new InvalidDataException("у версии нет assetIndex");
        var idxId = ai.Str("id") ?? vj.Str("assets") ?? "legacy";
        var idxFile = Path.Combine(Paths.AssetsDir, "indexes", idxId + ".json");
        var idxSha = ai.Str("sha1");
        bool needIdx = !File.Exists(idxFile);
        if (!needIdx && idxSha != null)
            needIdx = !string.Equals(await Net.HashFileAsync(idxFile, System.Security.Cryptography.SHA1.Create(), ct), idxSha, StringComparison.OrdinalIgnoreCase);
        if (needIdx)
            await Net.DownloadFileAsync(ai.Str("url")!, idxFile, idxSha, expectedSize: ai.GetLong("size", -1), ct: ct);

        var idx = JsonExt.ParseFile(idxFile);
        var isVirtual = idx.GetBool("virtual");
        var mapToResources = idx.GetBool("map_to_resources");
        var objs = idx.El("objects") ?? throw new InvalidDataException("индекс ресурсов без objects");
        var total = 0;
        foreach (var _ in objs.EnumerateObject()) total++;

        var done = 0;
        var errs = new ConcurrentBag<Exception>();
        prog.Report(new ProgressReport { Phase = "Ресурсы", Detail = $"0/{total}", Percent = 40 });
        await Parallel.ForEachAsync(objs.EnumerateObject(), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (kv, t) =>
            {
                try
                {
                    var hash = kv.Value.Str("hash")!;
                    var size = kv.Value.GetLong("size", -1);
                    var rel = hash[..2] + "/" + hash;
                    var dest = Path.Combine(Paths.AssetsDir, "objects", rel.Replace('/', Path.DirectorySeparatorChar));
                    var ok = File.Exists(dest) && (size <= 0 || new FileInfo(dest).Length == size);
                    if (!ok)
                        await Net.DownloadFileAsync(AssetsCdn + rel, dest, sha1: hash, expectedSize: size, ct: t);

                    if (isVirtual) CopyAsset(dest, Path.Combine(Paths.AssetsDir, "virtual", idxId, kv.Name));
                    if (mapToResources) CopyAsset(dest, Path.Combine(mcDir, "resources", kv.Name));
                }
                catch (Exception ex) { errs.Add(ex); }
                finally
                {
                    var d = Interlocked.Increment(ref done);
                    if (d % 25 == 0 || d == total)
                        prog.Report(new ProgressReport
                        {
                            Phase = "Ресурсы",
                            Detail = $"{d}/{total}",
                            Percent = 40 + (int)(56.0 * d / Math.Max(1, total)),
                        });
                }
            });
        if (!errs.IsEmpty) throw new AggregateException("не скачались ресурсы", errs.Take(3));
    }

    private static void CopyAsset(string src, string dest)
    {
        try
        {
            if (File.Exists(dest)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: false);
        }
        catch { /* гонка параллельных копий — не критично */ }
    }

    /// <summary>Собирает применимые на Windows библиотеки версии (обычные + нативные классификаторы).</summary>
    public static List<LibRef> CollectLibraries(JsonElement vj)
    {
        var res = new List<LibRef>();
        foreach (var lib in vj.Arr("libraries"))
        {
            if (!Rules.Allowed(lib.El("rules"))) continue;
            var name = lib.Str("name");

            if (lib.TryGetProperty("downloads", out var dl) && dl.ValueKind == JsonValueKind.Object)
            {
                if (dl.El("artifact") is { } art && art.Str("path") is string apath)
                    res.Add(new LibRef(apath, art.Str("url") ?? LibrariesCdn + apath, art.Str("sha1"),
                        art.GetLong("size", -1), false, Array.Empty<string>()));

                // Старый формат (<1.19): нативные jar-классификаторы под текущую ОС
                if (lib.El("natives") is { } natives && dl.El("classifiers") is { } classifiers)
                {
                    var key = natives.Str("windows")?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
                    if (key != null && classifiers.TryGetProperty(key, out var cl) && cl.ValueKind == JsonValueKind.Object)
                    {
                        var excludes = lib.El("extract")?.Arr("exclude")
                            .Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToArray() ?? Array.Empty<string>();
                        if (cl.Str("path") is string cpath)
                            res.Add(new LibRef(cpath, cl.Str("url") ?? "", cl.Str("sha1"),
                                cl.GetLong("size", -1), true, excludes));
                    }
                }
            }
            else if (name != null && lib.Str("url") is string ubase)
            {
                // Формат Fabric/сторонних репозиториев: имя Maven + базовый URL
                var path = MavenPath(name);
                res.Add(new LibRef(path, ubase.TrimEnd('/') + "/" + path, null, -1, false, Array.Empty<string>()));
            }
            else if (name != null)
            {
                var path = MavenPath(name);
                res.Add(new LibRef(path, LibrariesCdn + path, null, -1, false, Array.Empty<string>()));
            }
        }
        return res;
    }

    /// <summary>"group:artifact:version[:classifier]" → относительный путь в репозитории Maven.</summary>
    public static string MavenPath(string name)
    {
        var p = name.Split(':');
        if (p.Length < 3) return name.Replace('.', '/') + ".jar";
        var group = p[0].Replace('.', '/');
        var artifact = p[1];
        var version = p[2];
        var classifier = p.Length > 3 ? "-" + p[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.jar";
    }

    /// <summary>Распаковывает нативные библиотеки версии в nativesDir (для MC &lt; 1.19).</summary>
    public static void ExtractNatives(JsonElement vj, string nativesDir, string librariesRoot)
    {
        var natives = CollectLibraries(vj).Where(l => l.Native).ToList();
        if (natives.Count == 0) return;
        var stamp = Path.Combine(nativesDir, ".done");
        if (File.Exists(stamp)) return;
        Directory.CreateDirectory(nativesDir);
        var root = Path.GetFullPath(nativesDir);
        foreach (var lib in natives)
        {
            var jar = lib.AbsPath(librariesRoot);
            if (!File.Exists(jar)) continue;
            using var zip = ZipFile.OpenRead(jar);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/")) continue;
                if (lib.Exclude.Any(pref => entry.FullName.StartsWith(pref, StringComparison.OrdinalIgnoreCase))) continue;
                var dest = Path.GetFullPath(Path.Combine(nativesDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // защита от path traversal
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        File.WriteAllText(stamp, "ok");
    }
}
