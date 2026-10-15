using System.Diagnostics;
using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Запуск игры: сборка classpath и аргументов из json версии, старт JVM.</summary>
public static class GameLauncher
{
    public static event Action<CrashReportInfo, Instance>? GameCrashed;

    public static async Task<Process> LaunchAsync(Instance inst, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        prog.Report(new ProgressReport { Phase = "Токен Minecraft", Percent = 5 });
        var mcToken = await Auth.EnsureFreshMcTokenAsync(ct);
        var account = Ctx.Account!;

        if (account.IsOffline || string.IsNullOrWhiteSpace(mcToken) || mcToken == "0")
        {
            throw new InvalidOperationException("Запуск игры отклонён: обнаружен неподтверждённый профиль. Для запуска требуется официальная лицензия Minecraft Java Edition и авторизация через Microsoft.");
        }

        var versionsRoot = Path.Combine(LauncherLayout.SharedRoot, "versions");
        var spec = new VersionSpec { McVersion = inst.McVersion, Loader = inst.Loader, LoaderVersion = inst.LoaderVersion };
        var id = Instances.ResolveInstalledId(spec, versionsRoot);
        var vFile = Path.Combine(versionsRoot, id, id + ".json");
        if (!File.Exists(vFile))
        {
            // Профиль загрузчика отсутствует — готовим инстанс заново
            await Instances.PrepareAsync(inst, prog.Slice(5, 40), ct);
        }
        vFile = Path.Combine(versionsRoot, id, id + ".json");
        if (!File.Exists(vFile))
            throw new InvalidOperationException($"Не найден json версии {id}. Переустановите инстанс.");
        var vj = JsonExt.ParseFile(vFile);

        prog.Report(new ProgressReport { Phase = "Java", Percent = 45 });
        var vanillaJson = await VanillaJsonFor(vj, versionsRoot);
        var requiredMajor = Runtimes.DetectRequiredJavaMajor(inst, vanillaJson);
        var java = await Runtimes.GetJavaForInstanceAsync(inst, vanillaJson, prog.Slice(45, 70), ct);

        var actualMajor = await Runtimes.ProbeMajorAsync(java, ct);
        if (actualMajor != null && actualMajor < requiredMajor)
        {
            Ctx.Log($"[GameLauncher] Выбранная Java {actualMajor} ниже требуемой Java {requiredMajor}! Загрузка/подбор гарантированной Java {requiredMajor}...");
            java = await Runtimes.EnsureJavaMajorAsync(requiredMajor, prog.Slice(45, 70), ct);
        }

        var instVersions = Path.Combine(inst.Dir, "versions");
        Directory.CreateDirectory(Path.Combine(instVersions, id));
        var instJson = Path.Combine(instVersions, id, id + ".json");
        if (!File.Exists(instJson))
            File.WriteAllText(instJson, JsonSerializer.Serialize(vj, new JsonSerializerOptions { WriteIndented = true }));

        prog.Report(new ProgressReport { Phase = "Проверка файлов", Percent = 70 });
        var libsRoot = Path.Combine(LauncherLayout.SharedRoot, "libraries");

        // Объединяем библиотеки ванильной версии Minecraft (SLF4J, Log4j, Netty, Guava и т.д.) и загрузчика (Fabric/Forge)
        bool isModded = vj.Str("id") != vanillaJson.Str("id");
        var allLibraries = new List<LibRef>();
        if (vanillaJson.ValueKind == JsonValueKind.Object)
            allLibraries.AddRange(Mojang.CollectLibraries(vanillaJson));
        if (isModded)
            allLibraries.AddRange(Mojang.CollectLibraries(vj));

        var distinctLibraries = allLibraries
            .GroupBy(l => l.RelPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var missing = distinctLibraries
            .Where(l => !File.Exists(l.AbsPath(libsRoot)))
            .ToList();
        var i = 0;
        foreach (var lib in missing)
        {
            await Net.DownloadFileAsync(lib.Url, lib.AbsPath(libsRoot), lib.Sha1, expectedSize: lib.Size, ct: ct);
            prog.Report(new ProgressReport
            {
                Phase = "Доскачивание библиотек",
                Detail = $"{++i}/{missing.Count}",
                Percent = 70 + (int)(20.0 * i / Math.Max(1, missing.Count)),
            });
        }

        // jar игры: у загрузчиков указан "jar"/inheritsFrom → берём jar ванильной версии
        var jarId = vj.TryGetProperty("jar", out var jarProp) && jarProp.ValueKind == JsonValueKind.String
            ? jarProp.GetString()!
            : vj.Str("inheritsFrom") ?? inst.McVersion;
        var gameJar = Path.Combine(versionsRoot, jarId, jarId + ".jar");
        if (!File.Exists(gameJar))
        {
            var vanilla = await Mojang.FetchVersionJsonAsync(jarId, versionsRoot, ct: ct);
            var d = vanilla.El("downloads")?.El("client")!;
            await Net.DownloadFileAsync(d.Str("url")!, gameJar, d.Str("sha1"), expectedSize: d.GetLong("size", -1), ct: ct);
        }

        var nativesDir = Path.Combine(versionsRoot, id, "natives");
        if (vanillaJson.ValueKind == JsonValueKind.Object)
            Mojang.ExtractNatives(vanillaJson, nativesDir, libsRoot);
        if (isModded)
            Mojang.ExtractNatives(vj, nativesDir, libsRoot);

        var (jvmArgs, gameArgs) = BuildArgs(vj, vanillaJson, inst, id, gameJar, nativesDir, distinctLibraries, account, mcToken);
        var mainClass = vj.Str("mainClass") ?? vanillaJson.Str("mainClass") ?? throw new InvalidDataException("в json версии нет mainClass");

        prog.Report(new ProgressReport { Phase = "Запуск игры", Percent = 95 });
        Directory.CreateDirectory(inst.GameDir);

        var psi = new ProcessStartInfo
        {
            FileName = java,
            WorkingDirectory = inst.GameDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,
        };
        foreach (var a in jvmArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(mainClass);
        foreach (var a in gameArgs) psi.ArgumentList.Add(a);

        Ctx.Log($"[Launch Command] \"{java}\" {SanitizeLaunchArgs(psi.ArgumentList)}");

        var gameLog = new System.Text.StringBuilder();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                Ctx.Log("[Minecraft] " + e.Data);
                lock (gameLog) gameLog.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                Ctx.Log("[Minecraft] " + e.Data);
                lock (gameLog) gameLog.AppendLine(e.Data);
            }
        };
        proc.Exited += (_, _) =>
        {
            var code = proc.ExitCode;
            Ctx.Log($"[GameLauncher] Процесс игры завершился с кодом {code}");
            string fullOutput;
            lock (gameLog) fullOutput = gameLog.ToString();

            bool isCrash = code != 0 ||
                           fullOutput.Contains("A mod crashed on startup", StringComparison.OrdinalIgnoreCase) ||
                           fullOutput.Contains("Incompatible mods found", StringComparison.OrdinalIgnoreCase) ||
                           fullOutput.Contains("Exception in thread", StringComparison.OrdinalIgnoreCase) ||
                           fullOutput.Contains("CrashReport", StringComparison.OrdinalIgnoreCase);

            if (isCrash)
            {
                var crashInfo = CrashAnalyzer.Analyze(fullOutput, code);
                GameCrashed?.Invoke(crashInfo, inst);
            }
        };
        proc.Start();
        try { proc.BeginOutputReadLine(); } catch (Exception ex) { Ctx.Log("[Launch] Ошибка перенаправления stdout: " + ex.Message); }
        try { proc.BeginErrorReadLine(); } catch (Exception ex) { Ctx.Log("[Launch] Ошибка перенаправления stderr: " + ex.Message); }
        prog.Report(new ProgressReport { Phase = "Игра запущена", Percent = 100 });
        Ctx.Log($"Minecraft {inst.Subtitle} запущен (PID {proc.Id})");
        return proc;
    }

    private static async Task<JsonElement> VanillaJsonFor(JsonElement loaderJson, string versionsRoot)
    {
        var mc = loaderJson.Str("inheritsFrom");
        if (!string.IsNullOrEmpty(mc))
        {
            return await Mojang.FetchVersionJsonAsync(mc, versionsRoot);
        }
        return loaderJson;
    }

    private static (List<string> jvm, List<string> game) BuildArgs(
        JsonElement vj, JsonElement vanillaJson, Instance inst, string versionId, string gameJar, string nativesDir,
        List<LibRef> allLibraries, MinecraftAccount account, string mcToken)
    {
        var libsRoot = Path.Combine(LauncherLayout.SharedRoot, "libraries");
        var classpath = string.Join(Path.PathSeparator,
            allLibraries.Select(l => l.AbsPath(libsRoot)).Concat(new[] { gameJar }).Distinct(StringComparer.OrdinalIgnoreCase));

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = account.Username,
            ["version_name"] = versionId,
            ["game_directory"] = inst.GameDir,
            ["assets_root"] = Paths.AssetsDir,
            ["game_assets"] = Path.Combine(Paths.AssetsDir, "virtual", "legacy"),
            ["assets_index_name"] = (vanillaJson.Str("assets") ?? vj.Str("assets")) ?? "legacy",
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = mcToken,
            ["auth_xuid"] = "",
            ["clientid"] = Ctx.Settings.ClientToken,
            ["auth_session"] = "token:" + mcToken + ":" + account.Uuid,
            ["user_type"] = account.IsOffline ? "mojang" : "msa",
            ["version_type"] = vj.Str("type") ?? vanillaJson.Str("type") ?? "release",
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "AuroraLauncher",
            ["launcher_version"] = "1.0.0",
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = libsRoot,
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = "",
            ["user_properties"] = "{}",
            ["resolution_width"] = Ctx.Settings.WindowWidth.ToString(),
            ["resolution_height"] = Ctx.Settings.WindowHeight.ToString(),
        };
        string V(string key) => vars.TryGetValue(key, out var v) ? v : "";
        string Subst(string s) => ArgSub.Replace(s, m => V(m.Groups[1].Value));

        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = Ctx.Settings.CustomResolution,
        };

        var jvm = new List<string>();
        var game = new List<string>();
        var s = Ctx.Settings;
        jvm.Add("-Xmx" + s.RamMb + "M");
        jvm.Add("-Xms" + Math.Min(1024, s.RamMb) + "M");
        foreach (var raw in (s.ExtraJvmArgs ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            jvm.Add(raw);

        // 1. JVM-аргументы из ванильной версии
        if (vanillaJson.El("arguments") is { } vArgsObj)
        {
            foreach (var a in vArgsObj.Arr("jvm"))
                AppendArg(jvm, a, Subst, features);
        }
        else
        {
            jvm.Add("-Djava.library.path=" + nativesDir);
        }

        bool isModded = vj.Str("id") != vanillaJson.Str("id");

        // 2. JVM-аргументы из лоадера (Fabric/Forge)
        if (isModded && vj.El("arguments") is { } lArgsObj)
        {
            foreach (var a in lArgsObj.Arr("jvm"))
                AppendArg(jvm, a, Subst, features);
        }

        // Гарантируем, что nativesDir и classpath переданы в JVM
        if (!jvm.Any(x => x.StartsWith("-Djava.library.path=")))
            jvm.Add("-Djava.library.path=" + nativesDir);

        if (!jvm.Contains("-cp"))
        {
            jvm.Add("-cp");
            jvm.Add(classpath);
        }

        // 3. Game-аргументы из ванильной версии
        if (vanillaJson.El("arguments") is { } vgArgsObj)
        {
            foreach (var a in vgArgsObj.Arr("game"))
                AppendArg(game, a, Subst, features);
        }
        else if (vanillaJson.Str("minecraftArguments") is { } vMcArgs)
        {
            foreach (var tok in Subst(vMcArgs).Split(' '))
                if (tok.Length > 0) game.Add(tok);
        }

        // 4. Game-аргументы из лоадера
        if (isModded && vj.El("arguments") is { } lgArgsObj)
        {
            foreach (var a in lgArgsObj.Arr("game"))
                AppendArg(game, a, Subst, features);
        }

        // Страховка: обязательные игровые аргументы, если версия их не задаёт сама
        EnsurePair(game, "--username", V("auth_player_name"));
        EnsurePair(game, "--version", V("version_name"));
        EnsurePair(game, "--gameDir", V("game_directory"));
        EnsurePair(game, "--assetsDir", V("assets_root"));
        EnsurePair(game, "--assetIndex", V("assets_index_name"));
        EnsurePair(game, "--uuid", V("auth_uuid"));
        EnsurePair(game, "--accessToken", mcToken);
        EnsureValue(game, "--userType", account.IsOffline ? "mojang" : "msa");
        EnsureValue(game, "--versionType", "AuroraLauncher");
        if (Ctx.Settings.CustomResolution)
        {
            EnsurePair(game, "--width", V("resolution_width"));
            EnsurePair(game, "--height", V("resolution_height"));
        }

        return (jvm, game);
    }

    private static readonly System.Text.RegularExpressions.Regex ArgSub =
        new(@"\$\{([A-Za-z0-9_]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void AppendArg(List<string> list, JsonElement a,
        Func<string, string> subst, Dictionary<string, bool> features)
    {
        if (a.ValueKind == JsonValueKind.String)
        {
            var v = subst(a.GetString()!);
            if (v.Length > 0) list.Add(v);
            return;
        }
        if (a.ValueKind != JsonValueKind.Object) return;

        var rules = a.El("rules");
        if (rules != null && !Rules.Allowed(rules, features)) return;

        if (a.TryGetProperty("value", out var sv) && sv.ValueKind == JsonValueKind.String)
        {
            list.Add(subst(sv.GetString()!));
            return;
        }
        foreach (var v in a.Arr("value"))
        {
            var txt = subst(v.GetString() ?? "");
            if (txt.Length > 0) list.Add(txt);
        }
    }

    private static void EnsurePair(List<string> list, string key, string value)
    {
        if (list.Contains(key)) return;
        list.Add(key);
        list.Add(value);
    }

    private static void EnsureValue(List<string> list, string key, string value)
    {
        var idx = list.IndexOf(key);
        if (idx >= 0 && idx + 1 < list.Count) { list[idx + 1] = value; return; }
        if (idx < 0) { list.Add(key); list.Add(value); }
    }

    private static string SanitizeLaunchArgs(IEnumerable<string> args)
    {
        var sanitized = new List<string>();
        bool nextIsSensitive = false;
        foreach (var arg in args)
        {
            if (nextIsSensitive)
            {
                sanitized.Add("***REDACTED***");
                nextIsSensitive = false;
                continue;
            }

            if (arg is "--accessToken" or "-accessToken" or "--session")
            {
                sanitized.Add(arg);
                nextIsSensitive = true;
                continue;
            }

            if (arg.StartsWith("token:", StringComparison.OrdinalIgnoreCase))
            {
                sanitized.Add("token:***REDACTED***");
                continue;
            }

            sanitized.Add(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }
        return string.Join(" ", sanitized);
    }
}
