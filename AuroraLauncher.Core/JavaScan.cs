using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

/// <summary>Найденная на компьютере Java: путь, версия, поставщик, источник обнаружения.</summary>
public sealed record JavaInfo(string ExePath, int Major, string Version, string Vendor, string Source)
{
    public string JavaDir => Path.GetDirectoryName(ExePath) is { } d ? Path.GetDirectoryName(d) ?? d : "";
    public string Display => Major > 0
        ? (!string.IsNullOrWhiteSpace(Vendor) ? $"Java {Major} ({Vendor} · {Version})" : $"Java {Major} ({Version})")
        : "Java (версия неизвестна)";
}

/// <summary>
/// Сканирование компьютера на установленные Java.
/// Быстрое: переменные окружения, PATH, реестр Windows, стандартные каталоги.
/// Глубокое: обход всех фиксированных дисков на глубину 4 уровней по маскам jdk/jre/java/zulu/…
/// Версия определяется из файла release каталога Java (мгновенно), с фолбэком на запуск java -version.
/// </summary>
public static class JavaScan
{
    private static readonly ConcurrentDictionary<string, JavaInfo?> ProbeCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<List<JavaInfo>> ScanAsync(bool deep, IProgress<string>? status, CancellationToken ct)
    {
        status?.Report(deep ? "Сканирование дисков…" : "Быстрое сканирование…");
        var results = new ConcurrentDictionary<string, JavaInfo>(StringComparer.OrdinalIgnoreCase);
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount), CancellationToken = ct };

        if (deep)
        {
            // Глубокий обход дисков: BFS от корня каждого фиксированного диска
            var drives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
            var processed = 0;
            await Parallel.ForEachAsync(drives, opts, async (root, t) =>
            {
                await Task.Run(() => WalkDrive(root, results, status, drives.Count, () => Interlocked.Increment(ref processed), t), t);
            });
        }

        // Каталожные + реестровые источники (применяются и после глубокого — заполняют пропуски)
        var seedDirs = CollectSeedDirs();
        await Parallel.ForEachAsync(seedDirs, opts, async (dir, t) =>
        {
            await Task.Run(() =>
            {
                foreach (var exe in ProbeDirForJava(dir))
                    AddIfJava(results, exe, SourceLabel(dir));
            }, t);
        });

        // Переменные окружения и PATH
        var jh = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (jh != null)
        {
            var javaw = Path.Combine(jh, "bin", "javaw.exe");
            var exe = File.Exists(javaw) ? javaw : Path.Combine(jh, "bin", "java.exe");
            if (File.Exists(exe)) AddIfJava(results, exe, "JAVA_HOME");
        }
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(p.Trim('"'), "javaw.exe");
                if (!File.Exists(exe)) exe = Path.Combine(p.Trim('"'), "java.exe");
                if (File.Exists(exe)) AddIfJava(results, exe, "PATH");
            }
            catch { /* мусор в PATH */ }
        }

        // Дедупликация по каталогу установки (исключает показ одновременно java.exe и javaw.exe)
        var list = results.Values
            .GroupBy(j => j.JavaDir, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => x.ExePath.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.ExePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        status?.Report($"Найдено Java-установок: {list.Count}");
        Ctx.Log($"[JavaScan] Найдено {list.Count} уникальных Java-установок:");
        foreach (var j in list) Ctx.Log($"  - {j.Display} в {j.ExePath} [{j.Source}]");
        return list;
    }

    // ---------- Обход диска ----------

    private static readonly Regex JavaDirName = new(
        @"^(jdk|jre|java|jbr|zulu|corretto|temurin|adoptium|graal|graalvm|semeru|liberica|dragonwell|bisheng|mandrel|openjdk|bellsoft|azul|jetbrains|android)[\w.\-+]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void WalkDrive(string root, ConcurrentDictionary<string, JavaInfo> results,
        IProgress<string>? status, int totalRoots, Func<int> bump, CancellationToken ct)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                // Пропускаем тяжелые системные и временные каталоги
                if (name is "$Recycle.Bin" or "Windows" or "System Volume Information" or "node_modules" or ".git" or "Package Cache") continue;
                if (depth <= 2 && name is "ProgramData" or "$Windows.~BT" or "$WinREAgent") continue;

                // Быстрая проверка: является ли каталог папкой JDK/JRE
                bool isJava = IsJdkDir(name) ||
                              File.Exists(Path.Combine(child, "bin", "javaw.exe")) ||
                              File.Exists(Path.Combine(child, "bin", "java.exe"));

                if (isJava)
                {
                    foreach (var exe in ProbeDirForJava(child))
                        AddIfJava(results, exe, SourceLabel(child));
                    continue; // внутри найденного JDK глубже не спускаемся
                }

                // Увеличенная глубина сканирования (до 8 уровней для нахождения лаунчеров и вложенных папок)
                if (depth < 8) stack.Push((child, depth + 1));
            }
        }
        var done = bump();
        status?.Report($"Сканирование дисков… корень {done}/{totalRoots}");
    }

    private static bool IsJdkDir(string name) => JavaDirName.IsMatch(name);

    /// <summary>Ищет java.exe в каталоге: bin\java.exe или в подкаталогах-версиях (bin у корня или на 1-2 уровня ниже).</summary>
    private static IEnumerable<string> ProbeDirForJava(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var exe in ExesForDir(dir)) yield return exe;
        string[] subdirs = Array.Empty<string>();
        try
        {
            subdirs = Directory.GetDirectories(dir);
        }
        catch { /* нет доступа */ }
        foreach (var sub in subdirs)
        {
            foreach (var exe in ExesForDir(sub)) yield return exe;

            string[] sub2dirs = Array.Empty<string>();
            try
            {
                sub2dirs = Directory.GetDirectories(sub);
            }
            catch { }

            foreach (var sub2 in sub2dirs)
                foreach (var exe in ExesForDir(sub2)) yield return exe;
        }
    }

    private static IEnumerable<string> ExesForDir(string dir)
    {
        var javaw = Path.Combine(dir, "bin", "javaw.exe");
        if (File.Exists(javaw)) { yield return javaw; yield break; }
        var java = Path.Combine(dir, "bin", "java.exe");
        if (File.Exists(java)) { yield return java; yield break; }

        var rootJavaw = Path.Combine(dir, "javaw.exe");
        if (File.Exists(rootJavaw)) { yield return rootJavaw; yield break; }
        var rootJava = Path.Combine(dir, "java.exe");
        if (File.Exists(rootJava)) { yield return rootJava; }
    }

    // ---------- Источники быстрого скана ----------

    private static List<string> CollectSeedDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var roots = new List<string>
        {
            Path.Combine(pf, "Java"), Path.Combine(pf86, "Java"),
            Path.Combine(pf, "Eclipse Adoptium"), Path.Combine(pf86, "Eclipse Adoptium"),
            Path.Combine(pf, "Eclipse Foundation"),
            Path.Combine(pf, "Microsoft"), Path.Combine(pf86, "Microsoft"),
            Path.Combine(pf, "Zulu"), Path.Combine(pf86, "Zulu"),
            Path.Combine(pf, "Amazon Corretto"), Path.Combine(pf86, "Amazon Corretto"),
            Path.Combine(pf, "BellSoft"), Path.Combine(pf86, "BellSoft"),
            Path.Combine(pf, "Azul"), Path.Combine(pf, "GraalVM"),
            Path.Combine(pf, "RedHat"), Path.Combine(pf86, "RedHat"),
            Path.Combine(pf, "Semeru"),
            Path.Combine(user, ".jdks"),
            Path.Combine(user, "scoop", "apps"),
            Path.Combine(user, ".sdkman", "candidates", "java"),
            Path.Combine(local, "Programs", "Eclipse Adoptium"),
            Path.Combine(local, "Programs", "Common", "Java"),
            Path.Combine(Paths.RuntimesDir),

            // Лаунчеры и игровые клиенты
            Path.Combine(appData, "ModrinthApp", "meta", "java_versions"),
            Path.Combine(local, "ModrinthApp", "meta", "java_versions"),
            Path.Combine(appData, "PrismLauncher", "java"),
            Path.Combine(local, "Programs", "PrismLauncher", "java"),
            Path.Combine(appData, "PolyMC", "java"),
            Path.Combine(appData, ".minecraft", "runtime"),
            Path.Combine(user, ".curseforge", "minecraft", "Install", "runtime"),
            Path.Combine(local, "CurseForge", "minecraft", "Install", "runtime"),
            Path.Combine(local, "Overwolf", "CurseForge", "minecraft", "Install", "runtime"),
            Path.Combine(appData, ".feather", "player", "runtimes"),
            Path.Combine(user, ".lunarclient", "offline", "multiver"),
            Path.Combine(appData, ".betacraft"),
            Path.Combine(user, ".betacraft")
        };

        var list = new List<string>();
        foreach (var r in roots)
        {
            if (Directory.Exists(r))
            {
                list.Add(r);
                try
                {
                    foreach (var sub in Directory.GetDirectories(r))
                        list.Add(sub);
                }
                catch { }
            }
        }

        // Проверяем базы лаунчеров (например, Modrinth app.db table java_versions)
        try
        {
            var modrinthDb = Path.Combine(appData, "ModrinthApp", "app.db");
            if (File.Exists(modrinthDb))
            {
                var paths = QueryModrinthJavaDb(modrinthDb);
                foreach (var p in paths)
                {
                    if (File.Exists(p))
                    {
                        var parent = Path.GetDirectoryName(p);
                        if (!string.IsNullOrEmpty(parent)) list.Add(parent);
                    }
                }
            }
        }
        catch { }

        // Реестр Windows: Oracle/OpenJDK/Adoptium пишут сюда
        list.AddRange(RegistryJavaHomes());
        return list;
    }

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string filename, out IntPtr db, int flags, IntPtr vfs);

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStr)] string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr stmt);

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [System.Runtime.InteropServices.DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr stmt);

    private static List<string> QueryModrinthJavaDb(string dbPath)
    {
        var result = new List<string>();
        if (sqlite3_open_v2(dbPath, out var db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0) return result;
        try
        {
            if (sqlite3_prepare_v2(db, "SELECT path FROM java_versions", -1, out var stmt, IntPtr.Zero) == 0)
            {
                try
                {
                    while (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                    {
                        var ptr = sqlite3_column_text(stmt, 0);
                        if (ptr != IntPtr.Zero)
                        {
                            var s = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr);
                            if (!string.IsNullOrEmpty(s)) result.Add(s);
                        }
                    }
                }
                finally { sqlite3_finalize(stmt); }
            }
        }
        finally { sqlite3_close(db); }
        return result;
    }

    private static IEnumerable<string> RegistryJavaHomes()
    {
        foreach (var baseKey in new[] { "SOFTWARE\\JavaSoft", "SOFTWARE\\Eclipse Adoptium", "SOFTWARE\\WOW6432Node\\JavaSoft" })
        {
            var homes = new List<string>();
            Microsoft.Win32.RegistryKey? root = null;
            try
            {
                root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(baseKey);
                if (root == null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    var sub = root.OpenSubKey(subName);
                    if (sub == null) continue;
                    foreach (var verName in sub.GetSubKeyNames())
                    {
                        var ver = sub.OpenSubKey(verName);
                        var home = ver?.GetValue("JavaHome") as string ?? ver?.GetValue("Path") as string;
                        if (!string.IsNullOrEmpty(home)) homes.Add(home);
                    }
                }
            }
            catch { /* реестр недоступен */ }
            finally { root?.Dispose(); }
            foreach (var home in homes) yield return home;
        }
    }

    private static string SourceLabel(string dir) => dir switch
    {
        var d when d.Contains("ModrinthApp", StringComparison.OrdinalIgnoreCase) => "Modrinth App",
        var d when d.Contains("PrismLauncher", StringComparison.OrdinalIgnoreCase) => "Prism Launcher",
        var d when d.Contains(".minecraft", StringComparison.OrdinalIgnoreCase) => "Minecraft Launcher",
        var d when d.Contains("CurseForge", StringComparison.OrdinalIgnoreCase) => "CurseForge",
        var d when d.Contains("BetaCraft", StringComparison.OrdinalIgnoreCase) => "BetaCraft",
        var d when d.Contains(".feather", StringComparison.OrdinalIgnoreCase) => "Feather Launcher",
        var d when d.Contains(".lunarclient", StringComparison.OrdinalIgnoreCase) => "Lunar Client",
        var d when d.Contains(Paths.RuntimesDir, StringComparison.OrdinalIgnoreCase) => "Aurora Launcher",
        var d when d.Contains("Program Files", StringComparison.OrdinalIgnoreCase) => "Program Files",
        var d when d.Contains(".jdks", StringComparison.OrdinalIgnoreCase) || d.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) => "IDE (JetBrains)",
        var d when d.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android Studio",
        var d when d.Contains("scoop", StringComparison.OrdinalIgnoreCase) => "Scoop",
        var d when d.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) => "Adoptium",
        _ => "Система",
    };

    // ---------- Определение версии ----------

    private static void AddIfJava(ConcurrentDictionary<string, JavaInfo> results, string exe, string source)
    {
        try
        {
            if (!File.Exists(exe)) return;
            var full = Path.GetFullPath(exe);
            var info = ProbeJava(full, source);
            if (info != null) results.TryAdd(full, info);
        }
        catch { /* недоступен */ }
    }

    /// <summary>Версия из файла release (быстро) или запуском java -version (медленно). null — не Java.</summary>
    public static JavaInfo? ProbeJava(string exe, string source = "")
    {
        if (ProbeCache.TryGetValue(exe, out var cached)) return cached;

        JavaInfo? info = null;
        try
        {
            var javaDir = Path.GetDirectoryName(Path.GetDirectoryName(exe))!;
            var relFile = Path.Combine(javaDir, "release");
            if (File.Exists(relFile))
            {
                var text = File.ReadAllText(relFile);
                var vm = Regex.Match(text, "JAVA_VERSION=\"([^\"]+)\"");
                if (vm.Success)
                {
                    var version = vm.Groups[1].Value;
                    var major = ParseMajor(version);
                    var vendor = DetectVendor(text, javaDir);
                    info = new JavaInfo(exe, major, version, vendor, source);
                }
            }
            info ??= ProbeByRunning(exe, source);
        }
        catch { info = null; }

        if (info != null && info.Major <= 0 && !File.Exists(exe)) info = null;
        ProbeCache[exe] = info;
        return info;
    }

    private static int ParseMajor(string version)
    {
        if (version.StartsWith("1.")) return 8;
        var m = Regex.Match(version, @"^(\d+)");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    private static string DetectVendor(string releaseText, string fullPath)
    {
        var im = Regex.Match(releaseText, @"(?:IMPLEMENTOR|IMPLEMENTOR_NAME)=\""([^\""]+)\""");
        if (im.Success && !string.IsNullOrWhiteSpace(im.Groups[1].Value))
        {
            var val = im.Groups[1].Value.Trim();
            if (val.Contains("Azul", StringComparison.OrdinalIgnoreCase)) return "Azul Zulu";
            if (val.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) || val.Contains("Eclipse", StringComparison.OrdinalIgnoreCase)) return "Eclipse Temurin";
            if (val.Contains("Amazon", StringComparison.OrdinalIgnoreCase) || val.Contains("Corretto", StringComparison.OrdinalIgnoreCase)) return "Amazon Corretto";
            if (val.Contains("BellSoft", StringComparison.OrdinalIgnoreCase) || val.Contains("Liberica", StringComparison.OrdinalIgnoreCase)) return "BellSoft Liberica";
            if (val.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return "Microsoft Build of OpenJDK";
            if (val.Contains("GraalVM", StringComparison.OrdinalIgnoreCase) || (val.Contains("Oracle", StringComparison.OrdinalIgnoreCase) && fullPath.Contains("graal", StringComparison.OrdinalIgnoreCase))) return "GraalVM";
            return val;
        }

        var lower = fullPath.ToLowerInvariant();
        if (lower.Contains("zulu") || lower.Contains("azul")) return "Azul Zulu";
        if (lower.Contains("corretto") || lower.Contains("amazon")) return "Amazon Corretto";
        if (lower.Contains("temurin") || lower.Contains("adoptium")) return "Eclipse Temurin";
        if (lower.Contains("liberica") || lower.Contains("bellsoft")) return "BellSoft Liberica";
        if (lower.Contains("graal")) return "GraalVM";
        if (lower.Contains("microsoft")) return "Microsoft OpenJDK";
        if (lower.Contains("semeru")) return "IBM Semeru";
        if (lower.Contains("jbr") || lower.Contains("jetbrains")) return "JetBrains Runtime";
        if (lower.Contains("dragonwell")) return "Alibaba Dragonwell";
        if (lower.Contains("bisheng")) return "Huawei BiSheng";
        if (lower.Contains("redhat")) return "Red Hat OpenJDK";
        if (lower.Contains("oracle")) return "Oracle";
        return "OpenJDK";
    }

    private static JavaInfo? ProbeByRunning(string exe, string source)
    {
        try
        {
            var javaExe = exe.EndsWith("javaw.exe", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetDirectoryName(exe) ?? "", "java.exe")
                : exe;

            if (!File.Exists(javaExe)) javaExe = exe;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;

            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(3000);

            var m = Regex.Match(err, "version \"([^\"]+)\"");
            if (!m.Success)
            {
                var outText = p.StandardOutput.ReadToEnd();
                m = Regex.Match(outText, "version \"([^\"]+)\"");
                if (!m.Success) return null;
                err = outText;
            }

            var version = m.Groups[1].Value;
            var vendor = Regex.Match(err, @"(?:Environment|VM|Runtime) \((.+?)\)").Success
                ? Regex.Match(err, @"(?:Environment|VM|Runtime) \((.+?)\)").Groups[1].Value : "";

            return new JavaInfo(exe, ParseMajor(version), version, vendor, source);
        }
        catch { return null; }
    }

    /// <summary>Лучшая Java для версии игры: наибольший major, не ниже требуемого, разумный предел.</summary>
    public static JavaInfo? PickBest(IEnumerable<JavaInfo> list, int requiredMajor)
        => list.Where(j => j.Major >= requiredMajor && j.Major <= 40)
               .OrderByDescending(j => j.Major)
               .FirstOrDefault();
}
