using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

/// <summary>
/// Управление Java: поиск локальных установок, скачивание официальных рантаймов Mojang
/// (java-runtime-gamma = 17, delta = 21 и т.д.) и выбор подходящей для конкретной версии игры.
/// </summary>
public static class Runtimes
{
    private const string ProductBase = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df4216b4587c4bd";

    private static readonly ConcurrentDictionary<string, int?> MajorCache = new();

    /// <summary>Возвращает javaw/java для запуска, при необходимости скачивая рантайм Mojang.</summary>
    public static async Task<string> GetJavaForVersionAsync(JsonElement vanillaJson, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var (component, requiredMajor) = ParseJavaReq(vanillaJson);

        // 1. Явно указанный пользователем путь
        var explicitPath = Ctx.Settings.JavaPath;
        if (File.Exists(explicitPath))
        {
            var probe = SiblingJava(explicitPath);
            var major = probe == null ? (int?)null : await ProbeMajorAsync(probe, ct);
            if (major == null || major >= requiredMajor) return explicitPath;
            Ctx.Log($"Java из настроек ({major}) старее требуемой ({requiredMajor}) — ищем другую.");
        }

        // 2. Локальные установки (PATH, Program Files, .jdks, JAVA_HOME)
        var local = await FindBestLocalAsync(requiredMajor, ct);
        if (local != null) return local;

        // 3. Официальный рантайм Mojang
        prog.Report(new ProgressReport { Phase = "Java (рантайм Mojang)", Percent = -1 });
        return await EnsureRuntimeAsync(component ?? "java-runtime-gamma", prog, ct);
    }

    public static (string? component, int major) ParseJavaReq(JsonElement vanillaJson)
    {
        var jv = vanillaJson.El("javaVersion");
        var component = jv?.Str("component");
        var major = jv?.GetInt("majorVersion", 0) ?? 0;
        if (major == 0)
            major = component switch
            {
                "jre-legacy" => 8,
                "java-runtime-alpha" => 8,
                "java-runtime-beta" => 16,
                "java-runtime-delta" => 21,
                _ => 17,
            };
        return (component, major);
    }

    /// <summary>Скачивает и распаковывает рантайм Mojang, возвращает путь к javaw.exe.</summary>
    public static async Task<string> EnsureRuntimeAsync(string component, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var dir = Path.Combine(Paths.RuntimesDir, component);
        var existing = FindJavaExe(dir);
        if (existing != null) return existing;

        Ctx.Log($"Скачиваю Java-рантайм Mojang: {component}…");
        var j = await Net.GetJsonAsync($"{ProductBase}/{component}-windows-x64.json", ct);
        if (j.ValueKind == JsonValueKind.Array)
            j = j.EnumerateArray().FirstOrDefault();

        var files = j.El("files") ?? throw new InvalidOperationException("пустой манифест Java-рантайма");
        var entries = files.EnumerateObject().ToList();
        foreach (var kv in entries.Where(kv => kv.Value.Str("type") == "directory"))
            Directory.CreateDirectory(Path.Combine(dir, kv.Name.Replace('/', Path.DirectorySeparatorChar)));

        var fileEntries = entries.Where(kv => kv.Value.Str("type") == "file").ToList();
        var done = 0;
        var errs = new ConcurrentBag<Exception>();
        await Parallel.ForEachAsync(fileEntries, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct },
            async (kv, t) =>
            {
                try
                {
                    var raw = kv.Value.El("downloads")?.El("raw");
                    var url = raw?.Str("url");
                    if (url == null) return;
                    var sha1 = raw?.Str("sha1") ?? kv.Value.El("hashes")?.Str("sha1");
                    var dest = Path.Combine(dir, kv.Name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest))
                        await Net.DownloadFileAsync(url, dest, sha1, expectedSize: raw?.GetLong("size", -1) ?? -1, ct: t);
                }
                catch (Exception ex) { errs.Add(ex); }
                finally
                {
                    var d = Interlocked.Increment(ref done);
                    if (d % 20 == 0 || d == fileEntries.Count)
                        prog.Report(new ProgressReport
                        {
                            Phase = $"Java ({component})",
                            Detail = $"{d}/{fileEntries.Count}",
                            Percent = (int)(100.0 * d / Math.Max(1, fileEntries.Count)),
                        });
                }
            });
        if (!errs.IsEmpty) throw new AggregateException("не скачался Java-рантайм", errs.Take(3));

        var exe = FindJavaExe(dir) ?? throw new InvalidOperationException("в рантайме нет java.exe");
        Ctx.Log($"Java готова: {exe}");
        return exe;
    }

    /// <summary>Ищет java.exe/javaw.exe в каталоге (bin или глубже).</summary>
    public static string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root)) return null;
        foreach (var rel in new[] { @"bin\javaw.exe", @"bin\java.exe" })
        {
            var p = Path.Combine(root, rel);
            if (File.Exists(p)) return p;
        }
        try
        {
            foreach (var pattern in new[] { "javaw.exe", "java.exe" })
            {
                var hit = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        catch { /* доступ запрещён */ }
        return null;
    }

    private static string? SiblingJava(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var java = Path.Combine(dir, "java.exe");
        return File.Exists(java) ? java : null;
    }

    /// <summary>Определяет major-версию Java запуском "java -version" (с кэшем).</summary>
    public static async Task<int?> ProbeMajorAsync(string javaExe, CancellationToken ct)
    {
        if (MajorCache.TryGetValue(javaExe, out var cached)) return cached;
        int? result = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var (_, outText) = await Proc.RunCapturedAsync(javaExe, new[] { "-version" }, null, null, timeout.Token);
            var m = Regex.Match(outText, "version \"([^\"]+)\"");
            if (m.Success)
            {
                var v = m.Groups[1].Value;
                result = v.StartsWith("1.") ? 8 : int.TryParse(v.Split('.')[0], out var maj) ? maj : (int?)null;
            }
        }
        catch { /* не выполнился — игнорируем */ }
        MajorCache[javaExe] = result;
        return result;
    }

    /// <summary>Ищет установленную Java с версией не ниже требуемой.</summary>
    public static async Task<string?> FindBestLocalAsync(int requiredMajor, CancellationToken ct)
    {
        var candidates = new List<string>();

        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (jh != null) candidates.Add(Path.Combine(jh, "bin", "java.exe"));

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java",
                     @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft",
                     @"C:\Program Files\Zulu",
                     @"C:\Program Files\Amazon Corretto",
                     @"C:\Program Files\BellSoft",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"),
                 })
        {
            try
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories));
            }
            catch { /* нет доступа */ }
        }

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var jp = Path.Combine(p.Trim('"'), "java.exe");
                if (File.Exists(jp)) candidates.Add(jp);
            }
            catch { /* мусор в PATH */ }
        }

        string? best = null;
        var bestMajor = 0;
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
        {
            var major = await ProbeMajorAsync(exe, ct);
            if (major is >= 8 and <= 40 && major >= requiredMajor && major > bestMajor)
            {
                best = exe;
                bestMajor = major.Value;
            }
        }
        if (best != null) Ctx.Log($"Найдена локальная Java {bestMajor}: {best}");
        return best;
    }
}
