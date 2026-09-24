using System;
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

    // WHAT AN AIM'S SETTINGS SAY, ONCE WHATEVER THEY NAME IS THERE. A Service may
    // set this - see ModelSource - to obtain a model a setting names and the
    // machine does not have. Unset, settings are handed over exactly as written.
    public static Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>>? Resolve { get; set; }

    public IReadOnlyDictionary<string, string> For(
        string aimName)
    {
        var values = settings.TryGetValue(aimName, out var found)
            ? found
            : new Dictionary<string, string>();

        return Resolve is null ? values : Resolve(aimName, values);
    }
}
