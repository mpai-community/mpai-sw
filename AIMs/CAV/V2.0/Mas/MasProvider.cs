using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Cav.Mas;

// THE AIMs OF MAS STAGE 1, as the Controller asks for them by their AIM Instance.
// The MAS itself is a graph the Controller builds from its Metadata.
public sealed class MasProvider : IAimProvider
{
    public const string Ami = "1CAV-AMI-V2.0-I01", Msa = "1CAV-MSA-V2.0-I01", Ica = "1CAV-ICA-V2.0-I01", Mra = "1CAV-MRA-V2.0-I01";

    public bool CanCreate(string aimName) => aimName is Ami or Msa or Ica or Mra;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(MasProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) =>
        Create(aimName, settings, storage, null, null);

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage,
                                ISharedStorage? privateStorage, IRuledStorage? moduleStorage) => aimName switch
    {
        Ami => new AmsMasMessageInterpretation(aimName, settings),
        Msa => new MasSpatialAttitudeGeneration(aimName, settings),
        Ica => new IceConditionAnalysis(aimName, settings),
        Mra => new MasResponseAnalysis(aimName),
        _ => throw new ArgumentException($"{aimName} is not an AIM of MAS Stage 1.")
    };
}
