using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Cav.Ess;

// THE AIMs OF ESS STAGE 1, as the Controller asks for them by their AIM Instance.
// root: the folder the relative paths of the settings are resolved against - the
// repository, where Models is.
public sealed class EssProvider(string root) : IAimProvider
{
    public const string Sag = "1CAV-SAG-V2.0-I01", Bvs = "1OSD-BVS-V1.5-I02", Bed = "1CAV-BED-V2.0-I01";

    public string Root { get; } = root;

    public bool CanCreate(string aimName) => aimName is Sag or Bvs or Bed;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(EssProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => aimName switch
    {
        Sag => new SpatialAttitudeGeneration(aimName, settings),
        Bvs => new BasicVisualSceneDescription(aimName, settings, Root),
        Bed => new BasicEnvironmentDescription(aimName, settings),
        _ => throw new ArgumentException($"{aimName} is not an AIM of ESS Stage 1.")
    };
}
