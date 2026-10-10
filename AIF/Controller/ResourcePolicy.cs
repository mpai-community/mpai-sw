using System.Text.Json;

namespace AIF.Controller;

// A ResourcePolicy declared in an AIM's Metadata.
public sealed class ResourcePolicy
{
    public string Name { get; init; } = string.Empty;
    public string Minimum { get; init; } = string.Empty;
    public string Request { get; init; } = string.Empty;
    public string Maximum { get; init; } = string.Empty;

    public static IReadOnlyList<ResourcePolicy> ReadFrom(
        JsonElement amdRoot)
    {
        var policies =
            new List<ResourcePolicy>();

        if (!amdRoot.TryGetProperty(
                "ResourcePolicies",
                out var array))
        {
            return policies;
        }

        foreach (var item in array.EnumerateArray())
        {
            policies.Add(
                new ResourcePolicy
                {
                    Name = Text(item, "Name"),
                    Minimum = Text(item, "Minimum"),
                    Request = Text(item, "Request"),
                    Maximum = Text(item, "Maximum")
                });
        }

        return policies;
    }

    // "4 GB" (the form of the AIM Metadata schema), and "2_GB" or "4.5_GB" as
    // written before it -> bytes. Returns null when not a memory value.
    public static long? MemoryBytes(
        string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(value.EndsWith(" GB", StringComparison.Ordinal) || value.EndsWith("_GB", StringComparison.Ordinal)))
        {
            return null;
        }

        var number = value[..^3];

        return double.TryParse(
                   number,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var gigabytes)
            ? (long)(gigabytes * 1024 * 1024 * 1024)
            : null;
    }

    // A value as written: a string, or a number (CPU:Number and GPU:Number are
    // integers in the AIM Metadata schema) as its text.
    private static string Text(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
             : value.ValueKind == JsonValueKind.Number ? value.GetRawText()
             : string.Empty;
    }
}
