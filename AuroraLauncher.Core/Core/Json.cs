using System.Text.Json;

namespace AuroraLauncher.Core;

/// <summary>Мелкие помощники для работы с System.Text.Json без жёстких моделей.</summary>
public static class JsonExt
{
    public static string? Str(this JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static long GetLong(this JsonElement e, string prop, long def = 0)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        }
        return def;
    }

    public static int GetInt(this JsonElement e, string prop, int def = 0) => (int)e.GetLong(prop, def);

    public static bool GetBool(this JsonElement e, string prop, bool def = false)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v))
        {
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
        }
        return def;
    }

    /// <summary>Дочерний объект/массив или null.</summary>
    public static JsonElement? El(this JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
           && (v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array)
            ? v
            : null;

    public static List<JsonElement> Arr(this JsonElement e, string prop)
        => e.El(prop)?.EnumerateArray().ToList() ?? new List<JsonElement>();

    // Перегрузки для цепочек вида a.El("x")?.El("y").Str("z")

    public static string? Str(this JsonElement? e, string prop) => e is { } v ? v.Str(prop) : null;

    public static long GetLong(this JsonElement? e, string prop, long def = 0) => e is { } v ? v.GetLong(prop, def) : def;

    public static int GetInt(this JsonElement? e, string prop, int def = 0) => e is { } v ? v.GetInt(prop, def) : def;

    public static bool GetBool(this JsonElement? e, string prop, bool def = false) => e is { } v ? v.GetBool(prop, def) : def;

    public static JsonElement? El(this JsonElement? e, string prop) => e is { } v ? v.El(prop) : null;

    public static List<JsonElement> Arr(this JsonElement? e, string prop) => e is { } v ? v.Arr(prop) : new List<JsonElement>();

    public static JsonElement ParseFile(string path)
        => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
}
