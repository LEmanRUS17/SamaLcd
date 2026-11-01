using System.Text.Json;
using System.Text.RegularExpressions;
using SamaLcd.Core;
using SamaLcd.Core.Localization;
using SamaLcd.Core.Rendering;

namespace SamaLcd.Tests;

public class LocalizationTests
{
    [Fact]
    public void ExistingSettingsWithoutLanguageDefaultToEnglish()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"theme":"Neon","brightness":42}""", Theme.JsonOptions)!;
        Assert.Equal("en", settings.Language);
        Assert.Equal("Neon", settings.Theme);
        Assert.Equal(42, settings.Brightness);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hans")]
    [InlineData("ja")]
    [InlineData("ru")]
    public void SelectedLanguageSurvivesSettingsRoundTrip(string language)
    {
        var original = new AppSettings { Language = language, Theme = "Orbit", Brightness = 63 };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original, Theme.JsonOptions), Theme.JsonOptions)!;
        Assert.Equal(language, restored.Language);
        Assert.Equal(original.Theme, restored.Theme);
        Assert.Equal(original.Brightness, restored.Brightness);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("de", "en")]
    [InlineData("JA", "ja")]
    [InlineData("zh-hans", "zh-Hans")]
    public void InvalidLanguagesFallBackToEnglish(string? language, string expected)
    {
        Assert.Equal(expected, Localizer.Normalize(language));
        Assert.Equal(expected, new AppSettings { Language = language! }.Language);
    }

    [Fact]
    public void AllLanguagesHaveEveryKeyAndKeepFormatArguments()
    {
        var english = Localizer.Catalog("en");
        foreach (var language in Localizer.Languages)
        {
            var catalog = Localizer.Catalog(language);
            Assert.Equal(english.Keys.Order(), catalog.Keys.Order());
            foreach (var (key, text) in catalog)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key}");
                static string[] Arguments(string value) => Regex.Matches(value, @"\{\d+(?::[^}]+)?\}")
                    .Select(m => m.Value).Order().ToArray();
                Assert.Equal(Arguments(english[key]), Arguments(text));
                if (Arguments(text).Length > 0)
                    Assert.NotEmpty(Localizer.Translate(language, key, 123, 45.6, 78, 90));
            }
        }
    }

    [Fact]
    public void BrowserBundleMatchesNativeCatalogs()
    {
        var bundle = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "locales.js"));
        var json = bundle[(bundle.IndexOf(" = ", StringComparison.Ordinal) + 3)..].Trim().TrimEnd(';');
        var catalogs = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
        Assert.Equal(Localizer.Languages.Order(), catalogs.Keys.Order());
        foreach (var language in Localizer.Languages)
            Assert.Equal(Localizer.Catalog(language).OrderBy(p => p.Key), catalogs[language].OrderBy(p => p.Key));
    }

    [Fact]
    public void TranslationsFormatValuesAndPreserveLiteralTemplateExamples()
    {
        Assert.Equal("已连接：COM3", Localizer.Translate("zh-Hans", "Connected: {0}", "COM3"));
        Assert.Equal("接続済み：COM3", Localizer.Translate("ja", "Connected: {0}", "COM3"));
        Assert.Equal("Своя раскладка", Localizer.Translate("ru", "Custom layout"));
        Assert.Equal("Language", Localizer.Translate("unknown", "Language"));
        Assert.Equal("new message", Localizer.Translate("ja", "new message"));
        Assert.Equal("{键名:0.0}", Localizer.Translate("zh-Hans", "{key:0.0}"));
    }
}
