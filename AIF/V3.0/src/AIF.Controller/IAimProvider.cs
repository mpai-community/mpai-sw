namespace AIF.Controller;

// Supplies an implementation for an AIM named in the Metadata.
//
// The framework stays type-agnostic: it asks for an AIM by its standard name
// and hands over that AIM's settings; the provider — which lives with the
// implementations — decides what to construct.
public interface IAimProvider
{
    IAimProcessor Create(
        string aimName,
        IReadOnlyDictionary<string, string> settings);
}
