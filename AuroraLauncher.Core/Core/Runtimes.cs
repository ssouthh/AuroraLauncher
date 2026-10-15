using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

/// <summary>
/// Управление Java: поиск локальных установок, автоподбор под требования игры и модов,
/// скачивание проверенных дистрибутивов OpenJDK (8, 17, 21, 25).
/// </summary>
public static class Runtimes
{
    private static readonly ConcurrentDictionary<string, int?> MajorCache = new();

    /// <summary>
    /// Автоматически выявляет требуемую версию Java для запуска с учетом версии игры и установленных модов.
    /// </summary>
    public static int DetectRequiredJavaMajor(Instance inst, JsonElement vanillaJson)
    {
        var (_, majorFromVanilla) = ParseJavaReq(vanillaJson);
        var req = majorFromVanilla;

        // Если это снапшот нового цикла (25, 26, 25w..., 26w...)
        if (inst.McVersion.StartsWith("25") || inst.McVersion.StartsWith("26") ||
            inst.McVersion.Contains("25w", StringComparison.OrdinalIgnoreCase) ||
            inst.McVersion.Contains("26w", StringComparison.OrdinalIgnoreCase))
        {
            req = Math.Max(req, 25);
        }
        else
        {
            req = Math.Max(req, RequiredJavaMajorForMc(inst.McVersion));
        }

        // Сканируем установленные моды на ограничения по Java
        var modsDir = Path.Combine(inst.GameDir, "mods");
        if (Directory.Exists(modsDir))
        {
            foreach (var jarPath in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(jarPath);

                    // 1. Fabric mod dependencies (fabric.mod.json)
                    var fabricEntry = zip.GetEntry("fabric.mod.json");
                    if (fabricEntry != null)
                    {
                        using var s = fabricEntry.Open();
                        using var doc = JsonDocument.Parse(s);
                        if (doc.RootElement.TryGetProperty("depends", out var depends))
                        {
                            if (depends.TryGetProperty("java", out var javaDep))
                            {
                                var val = javaDep.ValueKind == JsonValueKind.String ? javaDep.GetString() : javaDep.ToString();
                                if (val != null)
                                {
                                    var match = Regex.Match(val, @"(?:>=|>|=)\s*(\d+)");
                                    if (match.Success && int.TryParse(match.Groups[1].Value, out var m))
                                    {
                                        if (m > req)
                                        {
                                            Ctx.Log($"[JavaReq] Мод {Path.GetFileName(jarPath)} требует Java {m} (выше {req})");
                                            req = m;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 2. Forge / NeoForge mod dependencies (META-INF/mods.toml)
                    var forgeEntry = zip.GetEntry("META-INF/mods.toml");
                    if (forgeEntry != null)
                    {
                        using var reader = new StreamReader(forgeEntry.Open());
                        var content = reader.ReadToEnd();
                        var match = Regex.Match(content, @"modId\s*=\s*""java""[\s\S]*?versionRange\s*=\s*""\[(\d+)");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var fm))
                        {
                            if (fm > req)
                            {
                                Ctx.Log($"[JavaReq] Forge-мод {Path.GetFileName(jarPath)} требует Java {fm} (выше {req})");
                                req = fm;
                            }
                        }
                    }
                }
                catch { /* игнорируем поврежденные jar-файлы */ }
            }
        }

        return req;
    }

    /// <summary>
    /// Автоматический подбор и установка подходящей Java для инстанса.
    /// Если в системе нет подходящей версии (например, для 26.3 нужна Java 25), она автоматически скачивается.
    /// </summary>
    public static async Task<string> GetJavaForInstanceAsync(Instance inst, JsonElement vanillaJson, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var requiredMajor = DetectRequiredJavaMajor(inst, vanillaJson);
        Ctx.Log($"[Java] Требуемая версия Java для инстанса \"{inst.Name}\" ({inst.McVersion}): Java {requiredMajor}");

        // 1. Проверяем явно выбранную в настройках Java
        var explicitPath = Ctx.Settings.JavaPath;
        if (File.Exists(explicitPath))
        {
            var probe = SiblingJava(explicitPath) ?? explicitPath;
            var major = await ProbeMajorAsync(probe, ct);
            if (major != null && major >= requiredMajor)
            {
                Ctx.Log($"[Java] Используется Java {major} из настроек: {explicitPath}");
                return explicitPath;
            }
            Ctx.Log($"[Java] Java из настроек ({major}) ниже требуемой ({requiredMajor}). Лаунчер автоматически подбирает и устанавливает Java {requiredMajor}...");
        }

        // 2. Проверяем локальные установки в системе (PATH, Program Files, .jdks и т.д.)
        var local = await FindBestLocalAsync(requiredMajor, ct);
        if (local != null)
        {
            Ctx.Log($"[Java] Найдена подходящая локальная Java {requiredMajor}+: {local}");
            return local;
        }

        // 3. Проверяем, есть ли уже загруженный рантайм в папке runtimes/
        var runtimeDir = Path.Combine(Paths.RuntimesDir, $"zulu-{requiredMajor}");
        var existing = FindJavaExe(runtimeDir) ?? FindJavaExe(Path.Combine(Paths.RuntimesDir, $"runtime-{requiredMajor}"));
        if (existing != null)
        {
            Ctx.Log($"[Java] Найдена ранее скачанная Java {requiredMajor}: {existing}");
            return existing;
        }

        // 4. Если ничего не найдено, автоматически скачиваем нужную версию Java до запуска
        prog.Report(new ProgressReport { Phase = $"Авто-установка Java {requiredMajor}", Detail = "Загрузка рантайма…", Percent = 50 });
        Ctx.Log($"[Java] Автоматическая установка требуемой Java {requiredMajor} перед запуском игры...");
        return await EnsureJavaMajorAsync(requiredMajor, prog, ct);
    }

    /// <summary>Возвращает javaw/java для запуска, при необходимости скачивая рантайм Mojang/Adoptium.</summary>
    public static async Task<string> GetJavaForVersionAsync(JsonElement vanillaJson, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var (component, requiredMajor) = ParseJavaReq(vanillaJson);

        var explicitPath = Ctx.Settings.JavaPath;
        if (File.Exists(explicitPath))
        {
            var probe = SiblingJava(explicitPath);
            var major = probe == null ? (int?)null : await ProbeMajorAsync(probe, ct);
            if (major == null || major >= requiredMajor) return explicitPath;
            Ctx.Log($"Java из настроек ({major}) старее требуемой ({requiredMajor}) — ищем другую.");
        }

        var local = await FindBestLocalAsync(requiredMajor, ct);
        if (local != null) return local;

        prog.Report(new ProgressReport { Phase = $"Java {requiredMajor}", Percent = -1 });
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
                "java-runtime-gamma" => 17,
                "java-runtime-delta" => 21,
                "java-runtime-epsilon" => 25,
                _ => 17,
            };
        return (component, major);
    }

    public static int RequiredJavaMajorForMc(string mcVersion)
    {
        if (mcVersion.StartsWith("25") || mcVersion.StartsWith("26") ||
            mcVersion.Contains("25w", StringComparison.OrdinalIgnoreCase) ||
            mcVersion.Contains("26w", StringComparison.OrdinalIgnoreCase))
            return 25;

        var m = Regex.Match(mcVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (m.Success)
        {
            var minor = int.Parse(m.Groups[1].Value);
            var patch = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (minor >= 21 || (minor == 20 && patch >= 5)) return 21;
            if (minor >= 18) return 17;
            if (minor == 17) return 16;
            return 8;
        }
        return 21;
    }

    public static string MojangComponentForMajor(int major) => major switch
    {
        >= 25 => "java-runtime-epsilon",
        >= 21 => "java-runtime-delta",
        >= 17 => "java-runtime-gamma",
        16 => "java-runtime-beta",
        _ => "jre-legacy",
    };

    /// <summary>Скачивает и распаковывает проверенный JRE (OpenJDK x64), возвращает путь к javaw.exe.</summary>
    public static async Task<string> EnsureRuntimeAsync(string component, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var major = component switch
        {
            "java-runtime-epsilon" => 25,
            "java-runtime-delta" => 21,
            "java-runtime-gamma" => 17,
            "java-runtime-beta" => 16,
            "jre-legacy" or "java-runtime-alpha" => 8,
            _ when int.TryParse(component, out var m) => m,
            _ => 21,
        };
        return await EnsureJavaMajorAsync(major, prog, ct);
    }

    /// <summary>Список проверенных зеркал и CDN для загрузки соответствующей версии Java (x64 Windows).</summary>
    public static List<string> GetMirrorsForMajor(int major) => major switch
    {
        >= 25 => new List<string>
        {
            "https://cdn.azul.com/zulu/bin/zulu25.36.205-ca-jre25.0.4.1-win_x64.zip",
            "https://static.azul.com/zulu/bin/zulu25.36.205-ca-jre25.0.4.1-win_x64.zip",
            "https://cdn.azul.com/zulu/bin/zulu25.0.31-ca-jdk25-win_x64.zip",
            "https://static.azul.com/zulu/bin/zulu25.0.31-ca-jdk25-win_x64.zip"
        },
        >= 21 => new List<string>
        {
            "https://cdn.azul.com/zulu/bin/zulu21.52.203-ca-jre21.0.12.1-win_x64.zip",
            "https://static.azul.com/zulu/bin/zulu21.52.203-ca-jre21.0.12.1-win_x64.zip",
            "https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.5%2B11/OpenJDK21U-jre_x64_windows_hotspot_21.0.5_11.zip",
            "https://aka.ms/download-jdk/microsoft-jdk-21.0.5-windows-x64.zip"
        },
        >= 17 => new List<string>
        {
            "https://cdn.azul.com/zulu/bin/zulu17.68.203-ca-jre17.0.20.1-win_x64.zip",
            "https://static.azul.com/zulu/bin/zulu17.68.203-ca-jre17.0.20.1-win_x64.zip",
            "https://github.com/adoptium/temurin17-binaries/releases/download/jdk-17.0.13%2B11/OpenJDK17U-jre_x64_windows_hotspot_17.0.13_11.zip",
            "https://aka.ms/download-jdk/microsoft-jdk-17.0.13-windows-x64.zip"
        },
        _ => new List<string>
        {
            "https://cdn.azul.com/zulu/bin/zulu8.96.0.205-ca-jre8.0.504-win_x64.zip",
            "https://static.azul.com/zulu/bin/zulu8.96.0.205-ca-jre8.0.504-win_x64.zip",
            "https://github.com/adoptium/temurin8-binaries/releases/download/jdk8u432-b06/OpenJDK8U-jre_x64_windows_hotspot_8u432b06.zip"
        }
    };

    /// <summary>
    /// Опрашивает список зеркал, определяет наиболее быстрое и скачивает с автоматическим переключением при сбое.
    /// </summary>
    public static async Task<string> DownloadFromBestMirrorAsync(List<string> candidateUrls, string destPath, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        // 1. Проверяем доступность и замеряем время отклика (ping) параллельно
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(4) };

        var checkTasks = candidateUrls.Select(async url =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    sw.Stop();
                    return (url, ok: true, latency: sw.ElapsedMilliseconds);
                }
            }
            catch { }
            return (url, ok: false, latency: long.MaxValue);
        });

        var results = await Task.WhenAll(checkTasks);
        var ordered = results.Where(r => r.ok).OrderBy(r => r.latency).Select(r => r.url).ToList();
        foreach (var u in candidateUrls) if (!ordered.Contains(u)) ordered.Add(u);

        Exception? lastEx = null;
        for (int i = 0; i < ordered.Count; i++)
        {
            var url = ordered[i];
            try
            {
                Ctx.Log($"[Java] Скачивание из зеркала {i + 1}/{ordered.Count}: {url}");
                prog.Report(new ProgressReport { Phase = "Загрузка Java", Detail = $"Зеркало {i + 1}/{ordered.Count}…", Percent = 25 });
                await Net.DownloadFileAsync(url, destPath, ct: ct);
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 1024 * 1024)
                {
                    Ctx.Log($"[Java] Загрузка успешно завершена ({new FileInfo(destPath).Length / 1024 / 1024} МБ) из {url}");
                    return destPath;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Ctx.Log($"[Java] Ошибка зеркала {url}: {ex.Message}");
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            }
        }

        throw lastEx ?? new InvalidOperationException("Все зеркала для загрузки Java недоступны.");
    }

    /// <summary>Скачивает и распаковывает конкретную версию JRE/JDK (OpenJDK x64).</summary>
    public static async Task<string> EnsureJavaMajorAsync(int major, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var dir = Path.Combine(Paths.RuntimesDir, $"zulu-{major}");
        var existing = FindJavaExe(dir) ?? FindJavaExe(Path.Combine(Paths.RuntimesDir, $"runtime-{major}"));
        if (existing != null) return existing;

        var mirrors = GetMirrorsForMajor(major);
        Ctx.Log($"[Java] Поиск доступного зеркала для Java {major} x64 среди {mirrors.Count} кандидатов...");
        prog.Report(new ProgressReport { Phase = $"Загрузка Java {major}", Detail = "Проверка зеркал и скорости…", Percent = 15 });

        var zipPath = Path.Combine(Paths.CacheDir, $"zulu-{major}.zip");
        await DownloadFromBestMirrorAsync(mirrors, zipPath, prog, ct);

        prog.Report(new ProgressReport { Phase = $"Установка Java {major}", Detail = "Распаковка…", Percent = 70 });
        var tempExtract = Path.Combine(Paths.CacheDir, $"zulu_extract_{major}_{Guid.NewGuid():N}");
        try
        {
            if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempExtract);

            var subDirs = Directory.GetDirectories(tempExtract);
            var rootDir = subDirs.Length == 1 ? subDirs[0] : tempExtract;

            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(rootDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(rootDir, file);
                var target = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
        finally
        {
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }

        var exe = FindJavaExe(dir) ?? throw new InvalidOperationException($"В распакованном Java {major} не найден javaw.exe");
        Ctx.Log($"[Java] Успешно установлена Java {major}: {exe}");
        prog.Report(new ProgressReport { Phase = $"Java {major} готова", Detail = "Завершено", Percent = 100 });
        return exe;
    }

    /// <summary>Ищет javaw.exe/java.exe в каталоге (приоритет у javaw.exe).</summary>
    public static string? FindJavaExe(string root)
    {
        if (!Directory.Exists(root)) return null;

        var javaw = Path.Combine(root, "bin", "javaw.exe");
        if (File.Exists(javaw)) return javaw;
        var java = Path.Combine(root, "bin", "java.exe");
        if (File.Exists(java)) return java;

        foreach (var sub in Directory.GetDirectories(root))
        {
            javaw = Path.Combine(sub, "bin", "javaw.exe");
            if (File.Exists(javaw)) return javaw;
            java = Path.Combine(sub, "bin", "java.exe");
            if (File.Exists(java)) return java;
        }
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
        // 1. Полноценное сканирование всех путей (Modrinth, Prism, PolyMC, .minecraft, CurseForge, реестр, PATH)
        try
        {
            var scanned = await JavaScan.ScanAsync(false, null, ct);
            var best = JavaScan.PickBest(scanned, requiredMajor);
            if (best != null && File.Exists(best.ExePath))
            {
                Ctx.Log($"[Java] Найдена подходящая локальная Java {best.Major} ({best.Vendor}) в {best.ExePath} [{best.Source}]");
                return best.ExePath;
            }
        }
        catch { }

        // 2. Фолбэк на прямой поиск
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
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModrinthApp", "meta", "java_versions"),
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

        string? fallbackBest = null;
        var bestMajor = 0;
        foreach (var exe in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(20))
        {
            var major = await ProbeMajorAsync(exe, ct);
            if (major is >= 8 and <= 40 && major >= requiredMajor && major > bestMajor)
            {
                fallbackBest = exe;
                bestMajor = major.Value;
            }
        }
        if (fallbackBest != null) Ctx.Log($"Найдена локальная Java {bestMajor}: {fallbackBest}");
        return fallbackBest;
    }
}
