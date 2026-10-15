using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace AuroraLauncher.Core;

public sealed class AuroraPackManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string McVersion { get; set; } = "";
    public string Loader { get; set; } = "vanilla";
    public string? LoaderVersion { get; set; }
    public string Icon { get; set; } = "🎮";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> Mods { get; set; } = new();
}

public static class BuildPackageManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Экспортирует сборку в файл с расширением .albuild (содержит манифест, все моды, конфиги и настройки).
    /// </summary>
    public static async Task ExportBuildAsync(Instance inst, string destinationFilePath, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Подготовка файлов сборки...");

        if (File.Exists(destinationFilePath))
        {
            File.Delete(destinationFilePath);
        }

        var modsDir = Path.Combine(inst.GameDir, "mods");
        var configDir = Path.Combine(inst.GameDir, "config");
        var optionsFile = Path.Combine(inst.GameDir, "options.txt");

        var installedMods = Directory.Exists(modsDir)
            ? Directory.GetFiles(modsDir, "*.jar").Select(Path.GetFileName).Where(f => !string.IsNullOrEmpty(f)).Cast<string>().ToList()
            : new List<string>();

        var manifest = new AuroraPackManifest
        {
            FormatVersion = 1,
            Name = inst.Name,
            McVersion = inst.McVersion,
            Loader = inst.Loader ?? "vanilla",
            LoaderVersion = inst.LoaderVersion,
            Icon = inst.Icon ?? "🎮",
            CreatedAt = DateTime.UtcNow,
            Mods = installedMods
        };

        using var zipStream = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        // 1. Запись aurora-pack.json
        progress?.Report("Создание манифеста сборки...");
        var manifestEntry = archive.CreateEntry("aurora-pack.json", CompressionLevel.Optimal);
        using (var entryStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(entryStream, manifest, JsonOpts, ct);
        }

        // 2. Упаковка папки mods/
        if (Directory.Exists(modsDir))
        {
            var jarFiles = Directory.GetFiles(modsDir, "*.jar");
            for (int i = 0; i < jarFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var jar = jarFiles[i];
                var fileName = Path.GetFileName(jar);
                progress?.Report($"Упаковка модов: {fileName} ({i + 1}/{jarFiles.Length})...");
                archive.CreateEntryFromFile(jar, $"mods/{fileName}", CompressionLevel.Fastest);
            }
        }

        // 3. Упаковка папки config/
        if (Directory.Exists(configDir))
        {
            progress?.Report("Архивация конфигураций модов...");
            var configFiles = Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories);
            foreach (var cfg in configFiles)
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(configDir, cfg).Replace('\\', '/');
                archive.CreateEntryFromFile(cfg, $"config/{rel}", CompressionLevel.Optimal);
            }
        }

        // 4. Упаковка options.txt (настройки игры)
        if (File.Exists(optionsFile))
        {
            progress?.Report("Архивация настроек Minecraft...");
            archive.CreateEntryFromFile(optionsFile, "options.txt", CompressionLevel.Optimal);
        }

        progress?.Report("Экспорт сборки успешно завершён!");
    }

    /// <summary>
    /// Импортирует сборку из файла .albuild, создает новую изолированную сборку, восстанавливает все моды и конфиги.
    /// </summary>
    public static async Task<Instance> ImportBuildAsync(string albuildFilePath, string? customName, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Чтение пакета сборки .albuild...");

        using var zipStream = new FileStream(albuildFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var manifestEntry = archive.GetEntry("aurora-pack.json");
        if (manifestEntry == null)
        {
            throw new InvalidDataException("Файл не является корректным пакетом AuroraLauncher (.albuild отсутствует aurora-pack.json).");
        }

        AuroraPackManifest? manifest;
        using (var ms = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<AuroraPackManifest>(ms, JsonOpts, ct);
        }

        if (manifest == null)
        {
            throw new InvalidDataException("Не удалось прочитать манифест сборки из файла.");
        }

        var instanceName = !string.IsNullOrWhiteSpace(customName) ? customName.Trim() : manifest.Name;
        // Избегаем коллизий имён
        var existing = Instances.LoadAll();
        var baseName = instanceName;
        int counter = 1;
        while (existing.Any(i => i.Name.Equals(instanceName, StringComparison.OrdinalIgnoreCase)))
        {
            instanceName = $"{baseName} ({counter++})";
        }

        progress?.Report($"Создание сборки \"{instanceName}\"...");
        var inst = new Instance
        {
            Name = instanceName,
            McVersion = manifest.McVersion,
            Loader = manifest.Loader ?? "vanilla",
            LoaderVersion = manifest.LoaderVersion ?? ""
        };

        existing.Add(inst);
        Instances.SaveAll(existing);
        Directory.CreateDirectory(inst.GameDir);

        // Распаковка модов, конфигов и настроек
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName == "aurora-pack.json" || entry.FullName.EndsWith('/'))
                continue;

            var targetPath = Path.Combine(inst.GameDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            progress?.Report($"Распаковка: {entry.Name}...");
            entry.ExtractToFile(targetPath, overwrite: true);
        }

        progress?.Report($"Сборка \"{instanceName}\" успешно импортирована!");
        return inst;
    }

    /// <summary>
    /// Очищает кэши, старые логи (.log.gz), краш-дампы и временные файлы из папки сборки.
    /// Возвращает количество освобожденных мегабайт.
    /// </summary>
    public static (int deletedFiles, double freedMb) CleanInstanceStorage(Instance inst)
    {
        int count = 0;
        long bytesFreed = 0;

        void TryDeleteFiles(string folder, string pattern)
        {
            if (!Directory.Exists(folder)) return;
            try
            {
                foreach (var file in Directory.GetFiles(folder, pattern, SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        bytesFreed += info.Length;
                        File.Delete(file);
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 1. Старые сжатые логи .log.gz
        TryDeleteFiles(Path.Combine(inst.GameDir, "logs"), "*.log.gz");
        // 2. Временные файлы .tmp и .dat_old
        TryDeleteFiles(inst.GameDir, "*.tmp");
        TryDeleteFiles(inst.GameDir, "*.tmp.jar");
        // 3. Краш-репорты старше недели
        var crashDir = Path.Combine(inst.GameDir, "crash-reports");
        if (Directory.Exists(crashDir))
        {
            foreach (var report in Directory.GetFiles(crashDir, "crash-*.txt"))
            {
                try
                {
                    var info = new FileInfo(report);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(7))
                    {
                        bytesFreed += info.Length;
                        File.Delete(report);
                        count++;
                    }
                }
                catch { }
            }
        }

        return (count, bytesFreed / (1024.0 * 1024.0));
    }
}
