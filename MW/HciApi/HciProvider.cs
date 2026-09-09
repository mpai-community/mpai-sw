using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AIF.Controller;
using AIF.Store;

namespace Mpai.Hci.Api;

// The HCI middleware provider â€” now a pure plug-in host. It has NO compile-time
// dependency on any AIM. At first use it scans the deployed assemblies for
// IAimPlugin implementations, maps them by AimName, and constructs each AIM through
// its plug-in (which builds its own dependencies from settings). Whichever AIM DLLs
// are present in the application's output are the AIMs available â€” so an application
// that references only its own AIMs runs with only those, and nothing else.
public sealed class HciProvider : IAimProvider, IDisposable
{
    private readonly AmdStore _store;
    private Dictionary<string, IAimPlugin>? _plugins;

    // galleryPath retained for source compatibility with existing callers; the
    // gallery is now loaded by the Face/Speaker Recognition plug-ins from settings.
    public HciProvider(AmdStore store, string? galleryPath = null) => _store = store;

    private Dictionary<string, IAimPlugin> Plugins()
    {
        if (_plugins is not null) return _plugins;
        var map = new Dictionary<string, IAimPlugin>();
        foreach (var dll in Directory.GetFiles(AppContext.BaseDirectory, "*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); } catch { continue; }
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IAimPlugin).IsAssignableFrom(t)) continue;
                try { var p = (IAimPlugin)Activator.CreateInstance(t)!; map[p.AimName] = p; }
                catch { }
            }
        }
        return _plugins = map;
    }

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings)
    {
        if (Plugins().TryGetValue(aimName, out var plugin))
            return plugin.Create(AimPortReader.Load(_store, aimName), settings);
        throw new NotSupportedException(
            $"HciProvider: no plug-in provides '{aimName}'. Is its AIM assembly deployed alongside the application?");
    }

    public void Dispose() { }   // plug-ins own the lifetime of their own dependencies
}
