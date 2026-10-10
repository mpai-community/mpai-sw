namespace AIF.SharedStorage;

// THE HANDLE AN AIM HOLDS, WHEREVER ITS MODULE'S STORAGE IS. A User Agent may say
// where a Module's scope is held after the Module has started
// (MPAI_AIFU_SharedStorage_Init, M3203 3.4.1), when its AIMs already hold their
// handles. So the handle asks, at each call, where its Module's scope now is,
// and works on the storage there, stamped with the writer the Controller bound.
public sealed class ModuleSharedStorage : ISharedStorage
{
    private readonly Func<string?> location;
    private readonly string writer;
    private readonly string requestedBy;

    // One storage per location, so that writers to one key share its lock.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), FileSharedStorage> opened = new();

    public ModuleSharedStorage(Func<string?> location, string writer, string requestedBy)
    {
        this.location = location;
        this.writer = writer;
        this.requestedBy = requestedBy;
    }

    private FileSharedStorage Here()
    {
        var root = location() ?? throw new InvalidOperationException(
            "No Shared Storage is initialised for this Module (MPAI_AIFU_SharedStorage_Init).");
        return opened.GetOrAdd((Path.GetFullPath(root), writer), k => new FileSharedStorage(k.Item1, writer, requestedBy));
    }

    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data) => Here().MPAI_AIFM_SharedStorage_Put(key, data);
    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data, long offset) => Here().MPAI_AIFM_SharedStorage_Put(key, data, offset);
    public byte[] MPAI_AIFM_SharedStorage_Get(string key) => Here().MPAI_AIFM_SharedStorage_Get(key);
    public byte[] MPAI_AIFM_SharedStorage_Get(string key, long offset, long length) => Here().MPAI_AIFM_SharedStorage_Get(key, offset, length);
    public void MPAI_AIFM_SharedStorage_Delete(string key) => Here().MPAI_AIFM_SharedStorage_Delete(key);
    public IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix) => Here().MPAI_AIFM_SharedStorage_List(prefix);
    public bool MPAI_AIFM_SharedStorage_Exists(string key) => Here().MPAI_AIFM_SharedStorage_Exists(key);
    public KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string key) => Here().MPAI_AIFM_SharedStorage_GetKeyInfo(key);
}
