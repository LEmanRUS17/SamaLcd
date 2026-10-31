using System.Globalization;
using System.Text.Json;

namespace SamaLcd.Core.Localization;

/// <summary>Shared UI catalogs, embedded for native UI and copied to wwwroot for WebView2.</summary>
public static class Localizer
{
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "en", "zh-Hans", "ja", "ru" });
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Catalogs = new(LoadCatalogs);
    private static string _language = "en";

    public static string Language
    {
        get => Volatile.Read(ref _language);
        set => Volatile.Write(ref _language, Normalize(value));
    }

    public static string Normalize(string? language) => Languages.FirstOrDefault(
        l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)) ?? "en";

    public static string Get(string key, params object[] args) => Translate(Language, key, args);

    public static string Translate(string? language, string key, params object[] args)
    {
        var locale = Normalize(language);
        var text = Catalogs.Value[locale].GetValueOrDefault(key) ?? Catalogs.Value["en"].GetValueOrDefault(key) ?? key;
        return args.Length == 0 ? text : string.Format(CultureInfo.GetCultureInfo(locale), text, args);
    }

    public static IReadOnlyDictionary<string, string> Catalog(string language) => Catalogs.Value[Normalize(language)];

    private static IReadOnlyDictionary<string, Dictionary<string, string>> LoadCatalogs()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in Languages)
        {
            using var stream = typeof(Localizer).Assembly.GetManifestResourceStream($"SamaLcd.Localization.{language}.json")
                ?? throw new InvalidOperationException($"Missing language resource: {language}");
            result[language] = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        }
        return result;
    }
}

public sealed record LocalizedMessage(string Key, object[] Arguments)
{
    public override string ToString() => Localizer.Get(Key, Arguments);
}
