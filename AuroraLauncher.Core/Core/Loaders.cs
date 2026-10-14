using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

public sealed record LoaderKind(string Id, string Title);

/// <summary>Метаданные версии модификации: загрузчик + версия игры.</summary>
public sealed class VersionSpec
{
    public string McVersion = "";
    public string Loader = "vanilla";   // vanilla | forge | neoforge | fabric
    public string LoaderVersion = "";   // "" = последняя подходящая

    public string DirName => Loader == "vanilla" ? McVersion : $"{McVersion}-{Loader}-{LoaderVersion}";
}

/// <summary>
/// Установка модифицированных версий:
/// • Fabric — через официальный meta-сервер (profidel json).
/// • NeoForge — установщик jar из официального maven (Sponge/EmergingBedrock-совместимый подход).
/// • Forge — установщик jar с files.minecraftforge.net.
/// </summary>
public static class Loaders
{
    public static readonly List<LoaderKind> Supported = new()
    {
        new("vanilla", "Ванильная"),
        new("forge", "Forge"),
        new("neoforge", "NeoForge"),
        new("fabric", "Fabric"),
    };

    // ---------------- Fabric ----------------

    public static async Task<List<string>> FabricVersionsForAsync(string mcVersion, CancellationToken ct)
    {
        var j = await Net.GetJsonAsync($"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}", ct);
        var res = new List<string>();
        foreach (var e in j.EnumerateArray())
        {
            var loader = e.El("loader")?.Str("version");
            if (loader != null) res.Add(loader);
        }
        return res;
    }

    public static async Task InstallFabricAsync(VersionSpec spec, string mcDir, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var versionsRoot = Path.Combine(mcDir, "versions");
        var loaderVer = spec.LoaderVersion;
        var url = string.IsNullOrEmpty(loaderVer)
            ? $"https://meta.fabricmc.net/v2/versions/loader/{spec.McVersion}/stable/1/profile/json"
            : $"https://meta.fabricmc.net/v2/versions/loader/{spec.McVersion}/{loaderVer}/profile/json";

        prog.Report(new ProgressReport { Phase = "Профиль Fabric", Percent = 30 });
        var profile = await Net.GetJsonAsync(url, ct);
        var id = profile.Str("id") ?? spec.DirName;
        spec.LoaderVersion = profile.El("loader")?.Str("version") ?? loaderVer;
        var dir = Path.Combine(versionsRoot, id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, id + ".json"),
            JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), ct);
        prog.Report(new ProgressReport { Phase = "Библиотеки Fabric", Percent = 55 });
        // Библиотеки скачает общий установщик по inheritsFrom — здесь регистрируем только версию.
        prog.Report(new ProgressReport { Phase = "Fabric готов", Percent = 100 });
    }

    // ---------------- Forge / NeoForge (через официальный установщик) ----------------

    public static async Task<List<string>> ForgePromosAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            var j = await Net.GetJsonAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
            var promos = j.El("promos");
            var res = new List<string>();
            var rec = promos?.Str($"{mcVersion}-recommended");
            var lat = promos?.Str($"{mcVersion}-latest");
            if (rec != null) res.Add(rec);
            if (lat != null && lat != rec) res.Add(lat);
            return res;
        }
        catch { return new List<string>(); }
    }

    public static async Task<List<string>> NeoForgeVersionsForAsync(string mcVersion, CancellationToken ct)
    {
        var res = new List<string>();
        try
        {
            var text = await Net.Client.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);
            foreach (Match m in Regex.Matches(text, "<version>([^<]+)</version>"))
            {
                var v = m.Groups[1].Value;
                // NeoForge нумерует версии как <mc>.<сборка>, например 21.1.95 для MC 1.21.1
                var mcNum = McVersionToNeoNumber(mcVersion);
                if (mcNum != null && v.StartsWith(mcNum + ".")) res.Add(v);
            }
            res.Reverse();
        }
        catch { /* сеть недоступна */ }
        return res;
    }

    private static string? McVersionToNeoNumber(string mc)
    {
        var m = Regex.Match(mc, @"^1\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        var minor = m.Groups[1].Value;
        var patch = m.Groups[2].Success ? m.Groups[2].Value : "0";
        return minor + (patch == "0" ? "" : "." + patch);
    }

    /// <summary>Скачивает инсталлятор Forge/NeoForge и устанавливает версию в mcDir.</summary>
    public static async Task InstallForgeLikeAsync(VersionSpec spec, string mcDir, IProgress<ProgressReport> prog, CancellationToken ct)
    {
        var isNeo = spec.Loader == "neoforge";
        string installerUrl;
        if (isNeo)
        {
            var ver = string.IsNullOrEmpty(spec.LoaderVersion)
                ? (await NeoForgeVersionsForAsync(spec.McVersion, ct)).FirstOrDefault()
                  ?? throw new InvalidOperationException($"Нет сборок NeoForge для {spec.McVersion}")
                : spec.LoaderVersion;
            spec.LoaderVersion = ver;
            installerUrl = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar";
        }
        else
        {
            var ver = spec.LoaderVersion;
            if (string.IsNullOrEmpty(ver))
            {
                var promos = await ForgePromosAsync(spec.McVersion, ct);
                ver = promos.FirstOrDefault()
                      ?? throw new InvalidOperationException($"Нет сборок Forge для {spec.McVersion}");
            }
            spec.LoaderVersion = ver;
            // Новый формат версий 1.13+: "<mc>-<forge>", например 1.20.1-47.3.0
            installerUrl = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{spec.McVersion}-{ver}/forge-{spec.McVersion}-{ver}-installer.jar";
        }

        prog.Report(new ProgressReport { Phase = "Скачивание установщика", Percent = 10 });
        var cache = Path.Combine(Paths.CacheDir, "installers");
        Directory.CreateDirectory(cache);
        var installer = Path.Combine(cache, Path.GetFileName(installerUrl));
        if (!File.Exists(installer))
            await Net.DownloadFileAsync(installerUrl, installer, ct: ct);

        // Установщику нужна Java 8+; берём любую локальную или рантайм 17
        prog.Report(new ProgressReport { Phase = "Запуск установщика", Percent = 35 });
        var java = await Runtimes.FindBestLocalAsync(8, ct)
                   ?? await Runtimes.EnsureRuntimeAsync("java-runtime-gamma", prog.Slice(35, 55), ct);

        var id = spec.DirName;
        var args = new List<string> { "-jar", installer, "--installClient", mcDir };
        var (code, output) = await Proc.RunCapturedAsync(java, args, mcDir,
            line => prog.Report(new ProgressReport { Phase = "Установка " + (isNeo ? "NeoForge" : "Forge"), Detail = Net.Trunc(line, 90), Percent = -1 }), ct);

        if (code != 0)
            throw new InvalidOperationException("Установщик завершился с ошибкой (код " + code + "): " + Net.Trunc(output, 500));

        // NeoForge по умолчанию ставит id вида "neoforge-<ver>"; приводим к единообразию не обязательно —
        // просто проверяем, что версия появилась.
        var versionsRoot = Path.Combine(mcDir, "versions");
        var found = Directory.Exists(versionsRoot) && Directory
            .EnumerateDirectories(versionsRoot)
            .Select(Path.GetFileName)
            .Any(n => n != null && n.Contains(isNeo ? "neoforge" : "forge", StringComparison.OrdinalIgnoreCase));
        if (!found)
            throw new InvalidOperationException("Установщик не создал версию — подробности: " + Net.Trunc(output, 500));

        prog.Report(new ProgressReport { Phase = (isNeo ? "NeoForge" : "Forge") + " установлен", Percent = 100 });
    }
}
