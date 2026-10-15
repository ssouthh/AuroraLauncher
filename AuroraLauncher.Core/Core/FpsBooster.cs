namespace AuroraLauncher.Core;

public sealed record FpsModPack(string Name, string Description, string[] ModSlugs);

/// <summary>
/// Уникальная фишка лаунчера: «1-Клик FPS Ускоритель».
/// Автоматически подбирает и устанавливает лучший пакет оптимизации графики и памяти под текущую версию игры и загрузчик.
/// </summary>
public static class FpsBooster
{
    public static FpsModPack GetRecommendedPack(Instance inst)
    {
        var loader = (inst.Loader ?? "fabric").ToLowerInvariant();
        if (loader is "fabric" or "quilt")
        {
            return new FpsModPack(
                "Пакет экстремального FPS (Sodium + Lithium + FerriteCore)",
                "Увеличивает FPS в 2-4 раза, оптимизирует рендеринг чанков, снижает потребление RAM на 40% и убирает микрофризы.",
                new[] { "sodium", "lithium", "ferrite-core", "entityculling" });
        }
        else if (loader is "forge" or "neoforge")
        {
            return new FpsModPack(
                "Пакет оптимизации Forge/NeoForge (Embeddium + FerriteCore)",
                "Современный порт рендерера Sodium для Forge/NeoForge, значительное увеличение частоты кадров и оптимизация памяти.",
                new[] { "embeddium", "ferrite-core" });
        }
        else
        {
            return new FpsModPack(
                "Ванильный Minecraft",
                "Для установки пакетов оптимизации FPS переключите загрузчик сборки на Fabric или Forge.",
                Array.Empty<string>());
        }
    }

    public static async Task<List<string>> InstallFpsPackAsync(Instance inst, IProgress<string>? status, CancellationToken ct)
    {
        var pack = GetRecommendedPack(inst);
        var installed = new List<string>();

        for (int i = 0; i < pack.ModSlugs.Length; i++)
        {
            var slug = pack.ModSlugs[i];
            status?.Report($"Установка {slug} ({i + 1}/{pack.ModSlugs.Length})…");
            try
            {
                var file = await Mods.InstallAsync("modrinth:" + slug, inst, ct);
                installed.Add(file);
                Ctx.Log($"[FpsBooster] Установлен мод оптимизации: {file}");
            }
            catch (Exception ex)
            {
                Ctx.Log($"[FpsBooster] Не удалось установить {slug}: {ex.Message}");
            }
        }

        return installed;
    }

    public static bool IsPackInstalled(Instance inst, List<string>? installedFiles = null)
    {
        installedFiles ??= Mods.Installed(inst);
        var pack = GetRecommendedPack(inst);
        if (pack.ModSlugs.Length == 0) return false;

        // Если хотя бы основной мод пакета (sodium, embeddium) или большинство модов установлены
        return pack.ModSlugs.Any(slug =>
            installedFiles.Any(f => f.Contains(slug, StringComparison.OrdinalIgnoreCase)));
    }

    public static int UninstallFpsPack(Instance inst)
    {
        var pack = GetRecommendedPack(inst);
        var installedFiles = Mods.Installed(inst);
        int count = 0;
        foreach (var file in installedFiles)
        {
            if (pack.ModSlugs.Any(slug => file.Contains(slug, StringComparison.OrdinalIgnoreCase)))
            {
                Mods.Delete(inst, file);
                count++;
            }
        }
        return count;
    }
}
