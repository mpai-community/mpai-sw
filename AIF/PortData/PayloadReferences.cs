using System;

namespace Mpai.Aif.PortData;

// A PAYLOAD REFERENCE A CONTROLLER ISSUED (M3215 3.6) - aif:payload/... - resolved
// by the codecs through the Controller they run under, which installs the hook.
// Where a Message leaves the Controller its references are inlined already; a
// codec that still meets one resolves it, and refuses any other reference form.
public static class PayloadReferences
{
    public const string Scheme = "aif:payload/";

    public static Func<string, byte[]?>? Resolve { get; set; }

    public static byte[]? TryResolve(string? uri) =>
        uri is not null && uri.StartsWith(Scheme, StringComparison.Ordinal) ? Resolve?.Invoke(uri) : null;
}
