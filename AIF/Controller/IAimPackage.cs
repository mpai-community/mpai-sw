using System.Text.Json.Nodes;

using AIF.Trust;

namespace AIF.Controller;

// AN IMPLEMENTATION THAT CONTAINS AIMs (M3223 3.3): a package - a composite
// delivered as one binary, whose L3 names the AIMs inside it as SubAIMs with the
// Relation Packaged. The Controller does not build them and does not route between
// them; it builds the package as one AIM. Under trust it credentials the package as
// an issuer, and the package presents each AIM inside it - its identity, the
// credential it issues it with the issuer given, and, where it measures it, the
// evidence of what it runs. The Controller verifies each through the chain to
// itself: it then knows what runs inside the package and can hold each AIM to
// account. It does not enforce anything inside: the channels between the package's
// AIMs are the package's.
public interface IAimPackage
{
    IReadOnlyList<PackagedAim> Present(PtfIssuer asIssuer, string packageInstanceId, DateTimeOffset now);
}

// One AIM inside a package, as the package presents it. Its instance is
// <package instance>/<AIM>.
public sealed record PackagedAim(string AimName, JsonObject Cii, JsonObject Credential, JsonObject? Evidence);

// A Module refused because an AIM of it was not trusted (M3223 3.8): the Controller
// API's outcome NOT_TRUSTED.
public sealed class TrustRefusedException(string message) : InvalidOperationException(message);
