using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

public sealed record CrashReportInfo(
    string Title,
    string Culprit,
    string Explanation,
    string Recommendation,
    string? CulpritModName,
    string FullLog,
    int ExitCode
);

/// <summary>
/// Интеллектуальный анализатор логов падения игры Minecraft.
/// Выявляет виновника падения (мод, память, Java, драйвер) и формирует понятный вердикт на русском языке.
/// </summary>
public static class CrashAnalyzer
{
    public static CrashReportInfo Analyze(string log, int exitCode)
    {
        var lower = log.ToLowerInvariant();

        // 1. Нехватка оперативной памяти (OutOfMemory)
        if (log.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("Java heap space", StringComparison.OrdinalIgnoreCase) ||
            log.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase))
        {
            return new CrashReportInfo(
                "Нехватка оперативной памяти (RAM)",
                "Память (Java Heap)",
                "Игре не хватило выделенной оперативной памяти для загрузки ресурсов и модов.",
                "Откройте «Настройки» в лаунчере и увеличьте объем выделяемой оперативной памяти (рекомендуется 4096-6144 МБ для сборок с модами).",
                null, log, exitCode);
        }

        // 2. Несовместимость версии Java (требуется более новая Java)
        var javaMatch = Regex.Match(log, @"requires version (\d+) or later of '(?:OpenJDK|Java).*?', but only the wrong version is present: (\d+)", RegexOptions.IgnoreCase);
        if (javaMatch.Success)
        {
            var req = javaMatch.Groups[1].Value;
            var cur = javaMatch.Groups[2].Value;
            var modMatch = Regex.Match(log, @"Mod '([^']+)'");
            var mod = modMatch.Success ? modMatch.Groups[1].Value : "Мод";
            return new CrashReportInfo(
                $"Требуется Java {req}",
                $"{mod} (Java {req})",
                $"Мод «{mod}» требует Java версии {req} или новее, но игра была запущена с Java {cur}.",
                $"Лаунчер автоматически подберет Java {req} при следующем запуске. Также вы можете сменить версию во вкладке «Среда Java Runtime».",
                mod, log, exitCode);
        }

        // 3. Ошибка точки входа мода (PreLaunch / ModInit crash)
        var preLaunchMatch = Regex.Match(log, @"Could not execute entrypoint stage '(\w+)' due to errors, provided by '([^']+)'");
        if (preLaunchMatch.Success)
        {
            var stage = preLaunchMatch.Groups[1].Value;
            var modId = preLaunchMatch.Groups[2].Value;
            var classNotFound = Regex.Match(log, @"ClassNotFoundException:\s*([\w.$]+)");
            var detail = classNotFound.Success
                ? $"Мод не смог найти системный класс или библиотеку «{classNotFound.Groups[1].Value}»."
                : "Произошла внутренняя ошибка при выполнении кода инициализации мода.";

            return new CrashReportInfo(
                $"Сбой при запуске мода «{modId}»",
                modId,
                $"{detail} Обычно это происходит, если мод не адаптирован под текущую версию загрузчика или отсутствуют необходимые библиотеки.",
                $"Попробуйте удалить мод «{modId}» во вкладке «Моды» или обновите его на более новую версию.",
                modId, log, exitCode);
        }

        // 4. Ошибки ClassNotFoundException / NoClassDefFoundError
        var noClassMatch = Regex.Match(log, @"(?:NoClassDefFoundError|ClassNotFoundException):\s*([\w/.$]+)");
        if (noClassMatch.Success)
        {
            var missingClass = noClassMatch.Groups[1].Value.Replace('/', '.');
            var culpritMod = DetectModFromStacktrace(log);
            return new CrashReportInfo(
                "Отсутствует необходимый класс или библиотека",
                culpritMod ?? missingClass,
                $"Игра попыталась вызвать компонент «{missingClass}», но он отсутствует в загруженных библиотеках или не поддерживается установленными модами.",
                culpritMod != null
                    ? $"Рекомендуется проверить совместимость или удалить мод «{culpritMod}»."
                    : "Проверьте совместимость установленных модов или обновите сборку.",
                culpritMod, log, exitCode);
        }

        // 5. Ошибки Mixin (конфликт модов)
        if (log.Contains("MixinTransformerError") || log.Contains("org.spongepowered.asm.mixin"))
        {
            var mixinMatch = Regex.Match(log, @"in target '([^']+)'");
            var mod = DetectModFromStacktrace(log);
            return new CrashReportInfo(
                "Конфликт миксинов (Mixin Conflict)",
                mod ?? "Конфликт модов",
                "Два или более мода попытались одновременно изменить один и тот же участок игрового кода, что вызвало критический сбой трансформатора Mixin.",
                mod != null
                    ? $"Конфликт вызван модом «{mod}». Попробуйте временно отключить его в списке установленных модов."
                    : "Попробуйте отключать недавно установленные моды по одному, чтобы найти несовместимый.",
                mod, log, exitCode);
        }

        // 6. Ошибка графического драйвера / Shaders / OpenGL
        if (log.Contains("EXCEPTION_ACCESS_VIOLATION") || exitCode == -1073741819 || lower.Contains("nvoglv64.dll") || lower.Contains("atio6axx.dll") || lower.Contains("ig9ic64.dll"))
        {
            return new CrashReportInfo(
                "Сбой видеодрайвера (Access Violation)",
                "Видеокарта / Драйвер OpenGL",
                "Произошла ошибка доступа к памяти в модуле графического драйвера видеокарты или шейдерном ядре.",
                "Обновите видеодрайвер (NVIDIA, AMD или Intel), отключите ресурсоемкие шейдеры или удалите несовместимые моды оптимизации графики.",
                null, log, exitCode);
        }

        // 7. Общее падение
        var fallbackMod = DetectModFromStacktrace(log);
        return new CrashReportInfo(
            exitCode != 0 ? $"Игра завершилась с ошибкой (код {exitCode})" : "Обнаружен сбой в работе игры",
            fallbackMod ?? "Неизвестная ошибка",
            fallbackMod != null
                ? $"В стеке ошибки фигурирует компонент «{fallbackMod}»."
                : "Игра неожиданно завершила работу без создания стандартного отчета об ошибке.",
            "Просмотрите детальный лог в консоли ниже или проверьте целостность сборки.",
            fallbackMod, log, exitCode);
    }

    private static string? DetectModFromStacktrace(string log)
    {
        // Ищем упоминания mod id в Knot/Fabric строках
        var m = Regex.Match(log, @"knot//net\.caffeinemc\.mods\.(\w+)");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(log, @"provided by '([^']+)'");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(log, @"Mod '([^']+)'");
        if (m.Success) return m.Groups[1].Value;

        m = Regex.Match(log, @"at\s+([a-zA-Z0-9_]+)\.[a-zA-Z0-9_]+\.[a-zA-Z0-9_]+");
        if (m.Success && !m.Groups[1].Value.StartsWith("net") && !m.Groups[1].Value.StartsWith("java"))
            return m.Groups[1].Value;

        return null;
    }
}
