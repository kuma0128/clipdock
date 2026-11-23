using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipDock.Core;

// UI text only. Download formats, state codes, URLs and saved filenames stay language-independent.
public static class Localization
{
    private static readonly Dictionary<string, string[]> Catalog = LoadCatalog();
    // The catalog's own language until the app sets the display language at launch.
    public static string Language { get; private set; } = "ja";
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "ja", "en", "zh-CN", "es" });
    // For a system whose languages ClipDock does not have, Traditional Chinese included.
    private const string SystemFallback = "en";

    private static Dictionary<string, string[]> LoadCatalog()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("ClipDock.Core.Localization.strings.json")
            ?? throw new InvalidOperationException("Missing localization catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }

    // A code from Languages, matched regardless of case; anything else stays in the current display language.
    public static string Normalize(string? language) => Supported(language) ?? Language;
    private static string? Supported(string? language) => Languages.FirstOrDefault(code =>
        string.Equals(code, language, StringComparison.OrdinalIgnoreCase));

    // The language chosen in 「設定」→「表示言語」, or null when there is none: on first launch, in settings
    // from before the menu existed, or when the file cannot be read. The system's display language applies then.
    public static string? ReadPreference(string settingsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            return document.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? Supported(value.GetString()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return null; }
    }

    // The display language without a saved choice. systemLanguages are the system's, most preferred first: Windows
    // gives its display language, macOS the preferred languages of Language & Region. The first one ClipDock has wins.
    public static string FromSystem(IEnumerable<string?> systemLanguages) =>
        systemLanguages.Select(Match).FirstOrDefault(language => language is not null) ?? SystemFallback;

    // A language tag ("ja-JP", "zh-Hans-HK", "es-419", or "ja_JP.UTF-8" as in LANG) to a code from Languages, or null.
    private static string? Match(string? tag)
    {
        var parts = (tag ?? "").Split('-', '_', '.', '@');
        bool Has(params string[] subtags) => parts.Skip(1).Any(part => subtags.Contains(part, StringComparer.OrdinalIgnoreCase));
        return parts[0].ToLowerInvariant() switch
        {
            "ja" => "ja",
            "en" => "en",
            "es" => "es",
            // A script decides when there is one (zh-Hans-HK is Simplified). Without it, Taiwan, Hong Kong and Macao
            // write Traditional Chinese, and every other region, or none, Simplified.
            "zh" => Has("Hans") || !Has("Hant", "TW", "HK", "MO") ? "zh-CN" : null,
            _ => null
        };
    }

    // Called at launch: the saved choice, otherwise the system's display language (see FromSystem).
    public static void Initialize(string settingsFile, IEnumerable<string?> systemLanguages) =>
        Language = ReadPreference(settingsFile) ?? FromSystem(systemLanguages);

    // Sets the display language directly. Choosing a different language in the app never interrupts an active
    // download; it is saved and applied at the next launch.
    public static void Initialize(string? language) => Language = Normalize(language);

    public static string Text(string japanese, string? language = null)
    {
        var index = Normalize(language ?? Language) switch { "en" => 0, "zh-CN" => 1, "es" => 2, _ => -1 };
        return index >= 0 && Catalog.TryGetValue(japanese, out var translations) ? translations[index] : japanese;
    }

    public static string Format(FormattableString message) => string.Format(
        CultureInfo.GetCultureInfo(Language), Text(message.Format), message.GetArguments());

    public static string ResourceKey(string japanese) => "L" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(japanese)))[..16];
    public static IEnumerable<KeyValuePair<string, string>> Resources => Catalog.Keys.Select(key =>
        new KeyValuePair<string, string>(ResourceKey(key), Text(key)));
}
