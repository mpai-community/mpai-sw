using System.Security.Cryptography;

namespace AIF.Trust;

// THE CODE OF A PARTY - a Controller, an AIM host - as its root of trust measures it
// before it runs (M3223 3.5): the AIF assemblies it runs from, each by its SHA-256,
// in a fixed order, into the code register. What a verifier approves - its reference
// values - is the same list, taken from the code as released.
public static class PartyCode
{
    public const string Prefix = "code ";

    public static IReadOnlyList<(string File, string Sha256)> Of(string directory) =>
        Directory.EnumerateFiles(directory, "*.dll")
                 .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("AIF.", StringComparison.Ordinal) || n == "Mpai.Aif.Api.dll"))
                 .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                 .Select(f => (Path.GetFileName(f), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))))
                 .ToList();

    public static void MeasureInto(IRootOfTrust root, string directory)
    {
        foreach (var (file, sha256) in Of(directory)) root.Measure(Attestation.CodeRegister, Prefix + file, sha256);
    }

    // A measurement of code, judged against the reference values: null where it is
    // the code approved.
    public static string? Approve(IReadOnlyDictionary<string, string> reference, Attestation.Measurement m) =>
        !m.What.StartsWith(Prefix, StringComparison.Ordinal) ? $"{m.What} is not code"
        : !reference.TryGetValue(m.What[Prefix.Length..], out var approved) ? $"{m.What[Prefix.Length..]} is not code approved"
        : !string.Equals(approved, m.Sha256, StringComparison.OrdinalIgnoreCase) ? $"{m.What[Prefix.Length..]} is not the code approved (altered)"
        : null;
}
