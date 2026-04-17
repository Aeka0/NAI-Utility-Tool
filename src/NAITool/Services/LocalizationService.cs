using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Globalization;
using Windows.System.UserProfile;

namespace NAITool.Services;

public sealed record SupportedLanguage(string Code, string CultureName, string DisplayNameKey);

public sealed class LocalizationService
{
    private const string DefaultLanguageCode = "en_us";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, Dictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Assembly _assembly = typeof(LocalizationService).Assembly;
    private Dictionary<string, string> _fallbackStrings = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _currentStrings = new(StringComparer.OrdinalIgnoreCase);

    public static LocalizationService Instance { get; } = new();

    public static IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } =
    [
        new("en_us", "en-US", "language.english"),
        new("zh_cn", "zh-CN", "language.zh_cn"),
        new("zh_tw", "zh-TW", "language.zh_tw"),
        new("ja_jp", "ja-JP", "language.ja_jp"),
    ];

    public string CurrentLanguageCode { get; private set; } = DefaultLanguageCode;

    public event EventHandler? LanguageChanged;

    private LocalizationService()
    {
        _fallbackStrings = LoadLanguageMap(DefaultLanguageCode);
        _currentStrings = _fallbackStrings;
    }

    public string Initialize(string? savedLanguageCode)
    {
        string resolved = string.IsNullOrWhiteSpace(savedLanguageCode)
            ? DetectSystemLanguageCode()
            : NormalizeLanguageCode(savedLanguageCode);
        SetLanguage(resolved, raiseEvent: false);
        return CurrentLanguageCode;
    }

    public void SetLanguage(string? languageCode, bool raiseEvent = true)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        _currentStrings = LoadLanguageMap(normalized);
        CurrentLanguageCode = normalized;
        ApplyCulture(normalized);

        if (raiseEvent)
            LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string key)
    {
        if (_currentStrings.TryGetValue(key, out string? current))
            return current;
        if (_fallbackStrings.TryGetValue(key, out string? fallback))
            return fallback;
        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string format = GetString(key);
        return string.Format(CultureInfo.CurrentCulture, format, args);
    }

    public string GetLanguageDisplayName(string code)
    {
        string normalized = NormalizeLanguageCode(code);
        var match = SupportedLanguages.FirstOrDefault(x => x.Code == normalized);
        return match == null ? normalized : GetString(match.DisplayNameKey);
    }

    public static string NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return DefaultLanguageCode;

        string normalized = languageCode.Trim().Replace('-', '_').ToLowerInvariant();
        return normalized switch
        {
            "en" or "en_us" or "en_gb" or "english" => "en_us",
            "zh" or "zh_cn" or "zh_sg" or "zh_hans" or "zh_chs" or "chs" or "simplified_chinese" => "zh_cn",
            "zh_tw" or "zh_hk" or "zh_mo" or "zh_hant" or "zh_cht" or "cht" or "traditional_chinese" => "zh_tw",
            "ja" or "ja_jp" or "japanese" => "ja_jp",
            _ => MapCultureName(normalized),
        };
    }

    public string DetectSystemLanguageCode()
    {
        if (TryGetSystemPreferredLanguageCode(out string systemLanguage))
            return systemLanguage;

        if (TryGetSupportedLanguageCode(CultureInfo.CurrentCulture.Name, out string currentCulture))
            return currentCulture;

        if (TryGetSupportedLanguageCode(CultureInfo.CurrentUICulture.Name, out string currentUiCulture))
            return currentUiCulture;

        foreach (string language in GlobalizationPreferences.Languages)
        {
            if (TryGetSupportedLanguageCode(language, out string normalized))
                return normalized;
        }

        return NormalizeLanguageCode(null);
    }

    private static bool TryGetSystemPreferredLanguageCode(out string normalized)
    {
        normalized = "";

        try
        {
            const uint muiLanguageName = 0x8;
            uint bufferLength = 0;
            bool sizeRead = GetSystemPreferredUILanguages(muiLanguageName, out uint languageCount, null, ref bufferLength);
            if (!sizeRead && bufferLength == 0)
                return false;

            if (languageCount == 0 || bufferLength == 0)
                return false;

            char[] buffer = new char[bufferLength];
            if (!GetSystemPreferredUILanguages(muiLanguageName, out languageCount, buffer, ref bufferLength))
                return false;

            foreach (string language in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryGetSupportedLanguageCode(language, out normalized))
                    return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] System preferred language detection failed: {ex.Message}");
        }

        normalized = "";
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSystemPreferredUILanguages(
        uint dwFlags,
        out uint pulNumLanguages,
        [Out] char[]? pwszLanguagesBuffer,
        ref uint pcchLanguagesBuffer);

    private static bool TryGetSupportedLanguageCode(string? languageCode, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(languageCode))
            return false;

        string value = languageCode.Trim().Replace('-', '_').ToLowerInvariant();
        string? mapped = value switch
        {
            "en" or "en_us" or "en_gb" or "english" => "en_us",
            "zh" or "zh_cn" or "zh_sg" or "zh_hans" or "zh_chs" or "chs" or "simplified_chinese" => "zh_cn",
            "zh_tw" or "zh_hk" or "zh_mo" or "zh_hant" or "zh_cht" or "cht" or "traditional_chinese" => "zh_tw",
            "ja" or "ja_jp" or "japanese" => "ja_jp",
            _ when value.StartsWith("zh_hant", StringComparison.Ordinal) ||
                   value.StartsWith("zh_tw", StringComparison.Ordinal) ||
                   value.StartsWith("zh_hk", StringComparison.Ordinal) ||
                   value.StartsWith("zh_mo", StringComparison.Ordinal) => "zh_tw",
            _ when value.StartsWith("zh", StringComparison.Ordinal) => "zh_cn",
            _ when value.StartsWith("ja", StringComparison.Ordinal) => "ja_jp",
            _ when value.StartsWith("en", StringComparison.Ordinal) => "en_us",
            _ => null,
        };

        if (mapped == null || !SupportedLanguages.Any(x => x.Code == mapped))
            return false;

        normalized = mapped;
        return true;
    }

    private static string MapCultureName(string normalized)
    {
        if (normalized.StartsWith("zh_hant", StringComparison.Ordinal) ||
            normalized.StartsWith("zh_tw", StringComparison.Ordinal) ||
            normalized.StartsWith("zh_hk", StringComparison.Ordinal) ||
            normalized.StartsWith("zh_mo", StringComparison.Ordinal))
        {
            return "zh_tw";
        }

        if (normalized.StartsWith("zh", StringComparison.Ordinal))
            return "zh_cn";

        if (normalized.StartsWith("ja", StringComparison.Ordinal))
            return "ja_jp";

        if (normalized.StartsWith("en", StringComparison.Ordinal))
            return "en_us";

        return DefaultLanguageCode;
    }

    private Dictionary<string, string> LoadLanguageMap(string languageCode)
    {
        if (_cache.TryGetValue(languageCode, out Dictionary<string, string>? existing))
            return existing;

        string resourceName = $"NAITool.i18n.{languageCode}.json";
        using Stream? stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            if (languageCode.Equals(DefaultLanguageCode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Missing embedded language resource: {resourceName}");
            return LoadLanguageMap(DefaultLanguageCode);
        }

        using var reader = new StreamReader(stream);
        string json = reader.ReadToEnd();
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        _cache[languageCode] = result;
        return result;
    }

    private static void ApplyCulture(string languageCode)
    {
        string cultureName = SupportedLanguages.FirstOrDefault(x => x.Code == languageCode)?.CultureName ?? "en-US";
        var culture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = cultureName;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Localization] PrimaryLanguageOverride failed: {ex.Message}");
        }

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
