using System.Text.Json;

namespace AIF.Store;

public sealed class LocalStoreService : IStoreService
{
    private readonly string amdFolder;

    private readonly AmdStore amdStore;

    private readonly MpaiStore mpaiStore;

    public LocalStoreService(
        string amdFolder)
    {
        this.amdFolder =
            amdFolder;

        amdStore =
            new AmdStore(amdFolder);

        mpaiStore =
            new MpaiStore(amdFolder);

        amdStore.Scan();
    }

    public IReadOnlyList<string> List()
    {
        return mpaiStore.List();
    }

    public bool Exists(
        Identifier identifier)
    {
        return amdStore.Exists(identifier);
    }

    public string Retrieve(
        Identifier identifier)
    {
        return amdStore
            .GetAMD(identifier)
            .RootElement
            .GetRawText();
    }

    public StoreResult Publish(
        string amdJson,
        bool replace = false)
    {
        var result =
            mpaiStore.Publish(
                amdJson,
                replace);

        if (result.WasPublished)
        {
            amdStore.Scan();
        }

        return result;
    }

    public StoreResult PublishFile(
        string amdFile,
        bool replace = false)
    {
        var result =
            mpaiStore.PublishFile(
                amdFile,
                replace);

        if (result.WasPublished)
        {
            amdStore.Scan();
        }

        return result;
    }

    public IReadOnlyList<CatalogItem> GetCatalog()
    {
        return amdStore.GetCatalog();
    }
}