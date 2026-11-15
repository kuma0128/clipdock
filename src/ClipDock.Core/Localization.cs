using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipDock.Core;

// UI text only. Download formats, state codes, URLs and saved filenames stay language-independent.
public static class Localization
{
    private static readonly Dictionary<string, string[]> Catalog = LoadCatalog();
    public static string Language { get; private set; } = "ja";
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "ja", "en", "zh-CN", "es" });

    private static Dictionary<string, string[]> LoadCatalog()
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream("ClipDock.Core.Localization.strings.json")
            ?? throw new InvalidOperationException("Missing localization catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }

    public static string Normalize(string? language) => Languages.FirstOrDefault(code =>
        string.Equals(code, language, StringComparison.OrdinalIgnoreCase)) ?? "ja";

    public static string ReadPreference(string settingsFile)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsFile));
            return Normalize(document.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return "ja"; }
    }

    // Called at launch. Choosing a different language never interrupts an active download.
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
