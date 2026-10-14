using System.IO;
using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Изолированная игровая установка (инстанс): своя папка с .minecraft-структурой.</summary>
public sealed class Instance
{
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public string LoaderVersion { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Dir => Path.Combine(Paths.InstancesDir, Name);

    [System.Text.Json.Serialization.JsonIgnore]
    public string GameDir => Path.Combine(Dir, "minecraft");

    [System.Text.Json.Serialization.JsonIgnore]
    public string Icon => Loader switch
    {
        "forge" => "⚙",
        "neoforge" => "◈",
        "fabric" => "🧵",
        _ => "⛏",
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string Subtitle => Loader == "vanilla"
        ? $"Minecraft {McVersion}"
        : $"Minecraft {McVersion} • {Loader} {LoaderVersion}";
}

/// <summary>Реестр инстансов + подготовка новой версии к запуску (ваниль + загрузчик + библиотеки).</summary>
public static class Instances
{
    private static string IndexFile => Path.Combine(Paths.Root, "instances.json");

    public static List<Instance> LoadAll()
    {
        try
        {
            if (File.Exists(IndexFile))
                return JsonSerializer.Deserialize<List<Instance>>(File.ReadAllText(IndexFile)) ?? new();
        }
        catch { /* повреждён — начинаем с пустого */ }
        return new();
    }

    public static void SaveAll(IEnumerable<Instance> list)
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(IndexFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Полная подготовка инстанса к запуску: ванильные файлы, загрузчик, общие библиотеки.</summary>
    public static async Task PrepareAsync(Instance inst, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        Directory.CreateDirectory(inst.GameDir);
        var spec = new VersionSpec
        {
            McVersion = inst.McVersion,
            Loader = inst.Loader,
            LoaderVersion = inst.LoaderVersion,
        };

        // 1. Ванильные компоненты (клиент/ресурсы качаются один раз в общий кэш-мир лаунчера)
        await Mojang.InstallVanillaAsync(inst.McVersion, LauncherLayout.SharedRoot, prog, ct);

        // 2. Загрузчик
        if (inst.Loader == "fabric")
            await Loaders.InstallFabricAsync(spec, LauncherLayout.SharedRoot, prog.Slice(0, 90), ct);
        else if (inst.Loader is "forge" or "neoforge")
            await Loaders.InstallForgeLikeAsync(spec, LauncherLayout.SharedRoot, prog.Slice(0, 90), ct);
        inst.LoaderVersion = spec.LoaderVersion;

        // 3. Дочерний json версии для этого инстанса (папка игры = инстанс)
        await WriteInstanceVersionAsync(inst, ct);
    }

    /// <summary>Создаёт в shared/versions копию json версии с overrides: gameDir инстанса.</summary>
    private static async Task WriteInstanceVersionAsync(Instance inst, CancellationToken ct)
    {
        var spec = new VersionSpec
        {
            McVersion = inst.McVersion,
            Loader = inst.Loader,
            LoaderVersion = inst.LoaderVersion,
        };
        var versionsRoot = Path.Combine(LauncherLayout.SharedRoot, "versions");
        var id = ResolveInstalledId(spec, versionsRoot);
        var srcFile = Path.Combine(versionsRoot, id, id + ".json");
        if (!File.Exists(srcFile)) return;

        var j = JsonExt.ParseFile(srcFile);
        // Рекурсивно подменяем inheritsFrom-цепочку: каждый уровень получает наш gameDir.
        var patched = PatchGameDir(j, inst.GameDir);
        var instVersions = Path.Combine(inst.Dir, "versions");
        Directory.CreateDirectory(Path.Combine(instVersions, id));
        await File.WriteAllTextAsync(Path.Combine(instVersions, id, id + ".json"),
            JsonSerializer.Serialize(patched, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static JsonElement PatchGameDir(JsonElement j, string gameDir)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var p in j.EnumerateObject())
        {
            if (p.Name is "inheritsFrom" or "jar") continue; // разрешаем самостоятельный запуск
            dict[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Array => (object?)p.Value.Clone(),
                JsonValueKind.Object => (object?)p.Value.Clone(),
                _ => null,
            };
        }
        dict["gameDir"] = gameDir;
        dict["javaDir"] = "";
        // Mojang-лаунчер параметры: только разрешённые ключи
        dict["argumentsOverride"] = null;
        dict.Remove("argumentsOverride");
        // Через overrides не передать произвольные ключи? Можно — экземплярный json это полноценная версия.
        // Убираем лишний javaDir: он не является стандартным ключом версии.
        dict.Remove("javaDir");
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>Определяет фактический id установленной версии загрузчика в shared/versions.</summary>
    public static string ResolveInstalledId(VersionSpec spec, string versionsRoot)
    {
        if (!Directory.Exists(versionsRoot)) return spec.DirName;
        var dirs = Directory.EnumerateDirectories(versionsRoot).Select(Path.GetFileName).OfType<string>().ToList();

        string Find(string needle)
        {
            return dirs.FirstOrDefault(d =>
                       d.Equals(spec.DirName, StringComparison.OrdinalIgnoreCase))
                   ?? dirs.FirstOrDefault(d =>
                       d.StartsWith(spec.McVersion + "-", StringComparison.OrdinalIgnoreCase)
                       && d.Contains(needle, StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        var id = spec.Loader switch
        {
            "fabric" => dirs.FirstOrDefault(d =>
                d.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) &&
                d.EndsWith("-" + spec.McVersion, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(spec.LoaderVersion) || d.Contains(spec.LoaderVersion, StringComparison.OrdinalIgnoreCase))) ?? "",
            "forge" => Find("forge"),
            "neoforge" => Find("neoforge"),
            _ => spec.McVersion,
        };
        if (string.IsNullOrEmpty(id)) id = spec.DirName;
        // Приоритет: точное совпадение "<mc>-forge" уже покрыто; для NeoForge имя "neoforge-<ver>"
        if (spec.Loader == "neoforge")
        {
            var alt = dirs.FirstOrDefault(d => d.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(alt) && spec.LoaderVersion.Length > 0 &&
                alt.Contains(spec.LoaderVersion)) id = alt;
        }
        return id;
    }
}

/// <summary>Общая папка файлов игры (клиенты, библиотеки, ресурсы), используемая всеми инстансами.</summary>
public static class LauncherLayout
{
    public static string SharedRoot => Path.Combine(Paths.Root, "shared");
}
