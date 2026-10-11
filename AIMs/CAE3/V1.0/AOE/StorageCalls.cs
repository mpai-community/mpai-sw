using AIF.SharedStorage;

namespace AIF.SharedStorage;

// The Shared Storage calls by their short names, as AOE and ASE were written in
// D:\AI against an earlier form of ISharedStorage; DI's carries the API names
// MPAI_AIFM_SharedStorage_*.
public static class StorageCalls
{
    public static void Put(this ISharedStorage s, string key, byte[] data) => s.MPAI_AIFM_SharedStorage_Put(key, data);
    public static byte[] Get(this ISharedStorage s, string key) => s.MPAI_AIFM_SharedStorage_Get(key);
    public static void Delete(this ISharedStorage s, string key) => s.MPAI_AIFM_SharedStorage_Delete(key);
    public static IReadOnlyList<string> List(this ISharedStorage s, string prefix) => s.MPAI_AIFM_SharedStorage_List(prefix);
    public static bool Exists(this ISharedStorage s, string key) => s.MPAI_AIFM_SharedStorage_Exists(key);
    public static KeyInfo GetKeyInfo(this ISharedStorage s, string key) => s.MPAI_AIFM_SharedStorage_GetKeyInfo(key);
}
