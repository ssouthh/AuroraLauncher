using System.Globalization;

namespace AuroraLauncher.Core;

public record LanguageInfo(string Code, string NativeName, string EnglishName);

/// <summary>
/// Централизованная система интернационализации и мультиязычности (i18n).
/// Поддерживает 14 языков, динамическое переключение на лету, fallback на английский язык
/// и автоматическое определение языка операционной системы Windows.
/// </summary>
public static class I18n
{
    public static readonly IReadOnlyList<LanguageInfo> SupportedLanguages = new[]
    {
        new LanguageInfo("en", "English", "English"),
        new LanguageInfo("ru", "Русский", "Russian"),
        new LanguageInfo("zh-CN", "简体中文", "Chinese (Simplified)"),
        new LanguageInfo("zh-TW", "繁體中文", "Chinese (Traditional)"),
        new LanguageInfo("es", "Español", "Spanish"),
        new LanguageInfo("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new LanguageInfo("fr", "Français", "French"),
        new LanguageInfo("de", "Deutsch", "German"),
        new LanguageInfo("ja", "日本語", "Japanese"),
        new LanguageInfo("ko", "한국어", "Korean"),
        new LanguageInfo("pl", "Polski", "Polish"),
        new LanguageInfo("uk", "Українська", "Ukrainian"),
        new LanguageInfo("tr", "Türkçe", "Turkish"),
        new LanguageInfo("it", "Italiano", "Italian"),
    };

    private static string _currentCode = "en";
    private static IReadOnlyDictionary<string, string> _currentDict = Locales.GetDictionary("en");
    private static readonly IReadOnlyDictionary<string, string> FallbackDict = Locales.GetDictionary("en");

    public static string CurrentLanguage => _currentCode;
    public static bool IsSystemLanguage { get; private set; } = true;

    public static event Action? LanguageChanged;

    public static void Init(string? savedPreference)
    {
        if (string.IsNullOrWhiteSpace(savedPreference) || savedPreference.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            IsSystemLanguage = true;
            _currentCode = DetectSystemLanguage();
        }
        else
        {
            var match = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(savedPreference, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                IsSystemLanguage = false;
                _currentCode = match.Code;
            }
            else
            {
                IsSystemLanguage = true;
                _currentCode = DetectSystemLanguage();
            }
        }

        _currentDict = Locales.GetDictionary(_currentCode);
    }

    public static void SetLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            IsSystemLanguage = true;
            _currentCode = DetectSystemLanguage();
        }
        else
        {
            var match = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                IsSystemLanguage = false;
                _currentCode = match.Code;
            }
            else
            {
                _currentCode = "en";
            }
        }

        _currentDict = Locales.GetDictionary(_currentCode);
        LanguageChanged?.Invoke();
    }

    public static string DetectSystemLanguage()
    {
        try
        {
            var current = CultureInfo.CurrentUICulture;
            var name = current.Name;

            var exact = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact.Code;

            var twoLetter = current.TwoLetterISOLanguageName.ToLowerInvariant();
            if (twoLetter == "zh")
            {
                if (name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("HK", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("MO", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Hant", StringComparison.OrdinalIgnoreCase))
                    return "zh-TW";
                return "zh-CN";
            }
            if (twoLetter == "pt")
            {
                return "pt-BR";
            }

            var byTwo = SupportedLanguages.FirstOrDefault(l => l.Code.Equals(twoLetter, StringComparison.OrdinalIgnoreCase));
            if (byTwo != null) return byTwo.Code;
        }
        catch { }

        return "en";
    }

    public static string Get(string key, params object[] args)
    {
        if (!_currentDict.TryGetValue(key, out var text) || string.IsNullOrEmpty(text))
        {
            if (!FallbackDict.TryGetValue(key, out text) || string.IsNullOrEmpty(text))
            {
                return key;
            }
        }

        if (args != null && args.Length > 0)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, text, args);
            }
            catch
            {
                return text;
            }
        }

        return text;
    }

    public static string T(string key, params object[] args) => Get(key, args);
}
