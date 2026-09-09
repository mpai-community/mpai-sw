using System.Text.Json;

namespace AIF.Store;

// Deployment settings for AIMs, keyed by standard AIM name:
//
//   { "MMC-TIQ-V2.5": { "VisionModel": "D:/...", "VocabFile": "D:/..." }, ... }
//
// Model and tool locations belong here, not in code, so the same AIM binaries
// run on any machine.
public sealed class AimSettings
{
    private readonly Dictionary<string, Dictionary<string, string>> settings =
        new();

    public static AimSettings Empty =>
        new();

    public static AimSettings Load(
        string path)
    {
        var loaded =
            new AimSettings();

        if (!File.Exists(path))
        {
            return loaded;
        }

        using var document =
            JsonDocument.Parse(
                File.ReadAllText(path));

        foreach (var aim in document.RootElement.EnumerateObject())
        {
            var values =
                new Dictionary<string, string>();

            foreach (var setting in aim.Value.EnumerateObject())
            {
                values[setting.Name] =
                    setting.Value.ValueKind == JsonValueKind.String
                        ? setting.Value.GetString() ?? string.Empty
                        : setting.Value.ToString();
            }

            loaded.settings[aim.Name] = values;
        }

        return loaded;
    }

    public IReadOnlyDictionary<string, string> For(
        string aimName)
    {
        return settings.TryGetValue(aimName, out var values)
            ? values
            : new Dictionary<string, string>();
    }
}
