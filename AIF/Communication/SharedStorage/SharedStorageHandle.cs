namespace AIF.SharedStorage;

// THE SHARED STORAGE HANDLE AN AIM HOLDS (M3219 3.3): wherever its Module's scope
// now is - a User Agent may say where after the Module has started - the Shared
// Storage at that location, reached under its rules by this AIM of this Module.
// The plain calls of M3203 4.10 as every AIM makes them, and the calls with rules
// beside them. The Module instance and the session are asked at each call too:
// an AIM is retained across the instances of its Module.
public sealed class SharedStorageHandle : ISharedStorage, IRuledStorage
{
    private readonly Func<string?> location;
    private readonly StorageHolder holder;
    private readonly Func<string> moduleInstance;
    private readonly Func<string> session;
    private readonly string requestedBy;
    private readonly Func<DateTimeOffset> now;

    public SharedStorageHandle(Func<string?> location, StorageHolder holder, Func<string> moduleInstance, Func<string> session,
                               string requestedBy, Func<DateTimeOffset> now)
    {
        this.location = location;
        this.holder = holder;
        this.moduleInstance = moduleInstance;
        this.session = session;
        this.requestedBy = requestedBy;
        this.now = now;
    }

    private ISharedStorage Plain() => (ISharedStorage)Here();

    private IRuledStorage Here()
    {
        var root = location() ?? throw new InvalidOperationException(
            "No Shared Storage is initialised for this Module (MPAI_AIFU_SharedStorage_Init).");
        return (IRuledStorage)RuledStore.SharedAt(root, now).PlainFor(holder, moduleInstance(), session(), requestedBy);
    }

    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data) => Plain().MPAI_AIFM_SharedStorage_Put(key, data);
    public void MPAI_AIFM_SharedStorage_Put(string key, byte[] data, long offset) => Plain().MPAI_AIFM_SharedStorage_Put(key, data, offset);
    public byte[] MPAI_AIFM_SharedStorage_Get(string key) => Plain().MPAI_AIFM_SharedStorage_Get(key);
    public byte[] MPAI_AIFM_SharedStorage_Get(string key, long offset, long length) => Plain().MPAI_AIFM_SharedStorage_Get(key, offset, length);
    public void MPAI_AIFM_SharedStorage_Delete(string key) => Plain().MPAI_AIFM_SharedStorage_Delete(key);
    public IReadOnlyList<string> MPAI_AIFM_SharedStorage_List(string prefix) => Plain().MPAI_AIFM_SharedStorage_List(prefix);
    public bool MPAI_AIFM_SharedStorage_Exists(string key) => Plain().MPAI_AIFM_SharedStorage_Exists(key);
    public KeyInfo MPAI_AIFM_SharedStorage_GetKeyInfo(string key) => Plain().MPAI_AIFM_SharedStorage_GetKeyInfo(key);

    public string Holder => holder.ToString();
    public StorageOutcome MPAI_AIFM_RuledStorage_Put(string key, byte[] data, string category, IReadOnlyCollection<string>? readers = null, StorageTime? time = null) =>
        Here().MPAI_AIFM_RuledStorage_Put(key, data, category, readers, time);
    public StorageOutcome MPAI_AIFM_RuledStorage_Get(string key, out byte[] data) => Here().MPAI_AIFM_RuledStorage_Get(key, out data);
    public StorageOutcome MPAI_AIFM_RuledStorage_Delete(string key) => Here().MPAI_AIFM_RuledStorage_Delete(key);
    public IReadOnlyList<string> MPAI_AIFM_RuledStorage_List(string? category = null, string prefix = "") => Here().MPAI_AIFM_RuledStorage_List(category, prefix);
    public bool MPAI_AIFM_RuledStorage_Exists(string key) => Here().MPAI_AIFM_RuledStorage_Exists(key);
    public StorageOutcome MPAI_AIFM_RuledStorage_Trace(string key, out StorageTrace? trace) => Here().MPAI_AIFM_RuledStorage_Trace(key, out trace);
    public StorageOutcome MPAI_AIFM_RuledStorage_SetRule(string category, StorageRule rule) => Here().MPAI_AIFM_RuledStorage_SetRule(category, rule);
}
