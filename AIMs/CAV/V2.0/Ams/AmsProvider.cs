using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Cav.Ams;

// THE AIMs OF AMS STAGE 1, as the Controller asks for them by their AIM Instance.
// The composites - the AMS itself, and Trajectory Planning and Decision - are graphs
// the Controller builds from their Metadata.
public sealed class AmsProvider : IAimProvider
{
    public const string Fed = "1CAV-FED-V2.0-I01", Rsp = "1CAV-RSP-V2.0-I01", Psp = "1CAV-PSP-V2.0-I01",
                        Msp = "1CAV-MSP-V2.0-I01", Toa = "1CAV-TOA-V2.0-I01", Amm = "1CAV-AMM-V2.0-I01";

    public bool CanCreate(string aimName) => aimName is Fed or Rsp or Psp or Msp or Toa or Amm;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(AmsProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage,
                                ISharedStorage? privateStorage, IRuledStorage? moduleStorage) => aimName switch
    {
        Fed => new FullEnvironmentDescription(aimName, settings),
        Rsp => new RouteSelectionPlanning(aimName),
        Psp => new PathSelectionPlanning(aimName),
        Msp => new MotionSelectionPlanning(aimName, settings),
        Toa => new TrafficObstacleAvoidance(aimName, settings),
        Amm => new AmsMemory(aimName, moduleStorage),
        _ => throw new ArgumentException($"{aimName} is not an AIM of AMS Stage 1.")
    };
}
