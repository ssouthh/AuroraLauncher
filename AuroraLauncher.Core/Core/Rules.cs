using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuroraLauncher.Core;

/// <summary>Вычисление Mojang-правил (rules) для библиотек и аргументов под текущую ОС.</summary>
public static class Rules
{
    /// <summary>true, если элемент с такими правилами применим на этой машине.</summary>
    public static bool Allowed(JsonElement? rules, Dictionary<string, bool>? featureFlags = null)
    {
        if (rules is null || rules.Value.ValueKind != JsonValueKind.Array || rules.Value.GetArrayLength() == 0)
            return true;
        var result = false;
        foreach (var r in rules.Value.EnumerateArray())
        {
            if (!Applies(r, featureFlags)) continue;
            result = r.Str("action") == "allow";
        }
        return result;
    }

    private static bool Applies(JsonElement rule, Dictionary<string, bool>? featureFlags)
    {
        var os = rule.El("os");
        if (os != null)
        {
            var name = os.Value.Str("name");
            if (name != null && name != "windows") return false;

            var arch = os.Value.Str("arch");
            if (arch != null)
            {
                var is64 = Environment.Is64BitOperatingSystem;
                if (arch == "x86" && is64) return false;
                if (arch is "x86_64" or "amd64" or "x64" && !is64) return false;
            }

            var ver = os.Value.Str("version");
            if (ver != null)
            {
                try { if (!Regex.IsMatch(Environment.OSVersion.VersionString, ver)) return false; }
                catch { return false; }
            }
        }

        // Правила с "features" (демо-режим, быстрый вход и т.п.) включаем только
        // при явной поддержке соответствующей функции.
        var feats = rule.El("features");
        if (feats != null)
        {
            foreach (var kv in feats.Value.EnumerateObject())
            {
                var want = kv.Value.ValueKind == JsonValueKind.True;
                if (featureFlags == null || !featureFlags.TryGetValue(kv.Name, out var have) || have != want)
                    return false;
            }
        }
        return true;
    }
}
