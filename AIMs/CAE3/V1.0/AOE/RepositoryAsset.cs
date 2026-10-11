namespace Mpai.Repository;

// A minimal plain data shape - AssetId and AssetType only. Kept solely so
// existing calling code (ASMApp reads .AssetId off whatever AoeAim/AseAim
// return) does not need to change. Carries no behavior of its own -
// AoeAim/AseAim now read and write directly through ISharedStorage
// (AIF.SharedStorage), not through a Repository class or method vocabulary.
public sealed class RepositoryAsset
{
    public required string AssetId { get; init; }
    public required AssetType AssetType { get; init; }
}