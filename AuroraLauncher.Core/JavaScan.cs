using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

/// <summary>Найденная на компьютере Java: путь, версия, поставщик, источник обнаружения.</summary>
public sealed record JavaInfo(string ExePath, int Major, string Version, string Vendor, string Source)
{
    public string JavaDir => Path.GetDirectoryName(ExePath) is { } d ? Path.GetDirectoryName(d) ?? d : "";
    public string Display => Major > 0 ? $"Java {Version}" : "Java (версия неизвестна)";
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
            foreach (var exe in new[] { Path.Combine(jh, "bin", "javaw.exe"), Path.Combine(jh, "bin", "java.exe") })
                AddIfJava(results, exe, "JAVA_HOME");
        }
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(p.Trim('"'), "javaw.exe");
                if (!File.Exists(exe)) exe = Path.Combine(p.Trim('"'), "java.exe");
                AddIfJava(results, exe, "PATH");
            }
            catch { /* мусор в PATH */ }
        }

        var list = results.Values
            .DistinctBy(j => j.ExePath)
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.ExePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        status?.Report($"Найдено Java-установок: {list.Count}");
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
                if (name is "$Recycle.Bin" or "Windows" or "ProgramData" && depth == 0) continue;
                if (IsJdkDir(name))
                {
                    foreach (var exe in ProbeDirForJava(child))
                        AddIfJava(results, exe, "Сканирование дисков");
                    continue; // внутри JDK ищем нечего
                }
                if (depth < 4) stack.Push((child, depth + 1));
            }
        }
        var done = bump();
        status?.Report($"Сканирование дисков… корень {done}/{totalRoots}");
    }

    private static bool IsJdkDir(string name) => JavaDirName.IsMatch(name);

    /// <summary>Ищет java.exe в каталоге: bin\java.exe или в подкаталогах-версиях (bin у корня или на 1 уровень ниже).</summary>
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
            foreach (var exe in ExesForDir(sub)) yield return exe;
    }

    private static IEnumerable<string> ExesForDir(string dir)
    {
        foreach (var exe in new[] { Path.Combine(dir, "bin", "javaw.exe"), Path.Combine(dir, "bin", "java.exe") })
            if (File.Exists(exe)) yield return exe;
    }

    // ---------- Источники быстрого скана ----------

    private static List<string> CollectSeedDirs()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new[]
        {
            Path.Combine(pf, "Java"), Path.Combine(pf86, "Java"),
            Path.Combine(pf, "Eclipse Adoptium"), Path.Combine(pf86, "Eclipse Adoptium"),
            Path.Combine(pf, "Eclipse Foundation"),
            Path.Combine(pf, "Microsoft"), Path.Combine(pf86, "Microsoft"),
            Path.Combine(pf, "Zulu"), Path.Combine(pf, "Amazon Corretto"),
            Path.Combine(pf86, "Amazon Corretto"),
            Path.Combine(pf, "BellSoft"), Path.Combine(pf86, "BellSoft"),
            Path.Combine(pf, "Azul"), Path.Combine(pf, "GraalVM"),
            Path.Combine(pf, "RedHat"), Path.Combine(pf86, "RedHat"),
            Path.Combine(pf, "IBM"), Path.Combine(pf86, "IBM"),
            Path.Combine(pf, "Semeru"),
            Path.Combine(pf, "Android", "Android Studio"),
            Path.Combine(user, ".jdks"),
            Path.Combine(user, "scoop", "apps"),
            Path.Combine(local, "Programs", "Eclipse Adoptium"),
            Path.Combine(local, "JetBrains"),
        };

        var list = new List<string>();
        foreach (var r in roots)
            if (Directory.Exists(r)) list.Add(r);

        // Реестр Windows: Oracle/OpenJDK/Adoptium пишут сюда
        list.AddRange(RegistryJavaHomes());
        return list;
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
        var d when d.Contains("Program Files", StringComparison.OrdinalIgnoreCase) => "Program Files",
        var d when d.Contains(".jdks", StringComparison.OrdinalIgnoreCase) || d.Contains("JetBrains", StringComparison.OrdinalIgnoreCase) => "IDE (JetBrains)",
        var d when d.Contains("Android", StringComparison.OrdinalIgnoreCase) => "Android Studio",
        var d when d.Contains("scoop", StringComparison.OrdinalIgnoreCase) => "Scoop",
        var d when d.Contains("Adoptium", StringComparison.OrdinalIgnoreCase) => "Program Files",
        _ => "Каталог",
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
                var im = Regex.Match(text, "IMPLEMENTOR=\"([^\"]+)\"");
                if (vm.Success)
                {
                    var version = vm.Groups[1].Value;
                    var major = ParseMajor(version);
                    info = new JavaInfo(exe, major, version,
                        im.Success ? im.Groups[1].Value : "", source);
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

    private static JavaInfo? ProbeByRunning(string exe, string source)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var (_, outText) = Proc.RunCapturedAsync(exe, new[] { "-version" }, null, null, timeout.Token).GetAwaiter().GetResult();
            var m = Regex.Match(outText, "version \"([^\"]+)\"");
            if (!m.Success) return null;
            var version = m.Groups[1].Value;
            var vendor = Regex.Match(outText, "(?:Environment|VM) \\((.+?)\\)").Success
                ? Regex.Match(outText, "(?:Environment|VM) \\((.+?)\\)").Groups[1].Value : "";
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
