using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

using AIF.SharedStorage;
using AIF.Store;

namespace AIF.Controller;

// AIMs FROM PACKAGES - MPAI-MAS actions 10 to 13. An AIM's L3 names, in each of
// its Implementations entries, where its package is (ImplementationURI), what it
// is called (BinaryName) and what it runs on (Architecture, OperatingSystem).
// This provider takes the entry for the machine it is on, fetches the package into
// the SCI's cache, loads it in a load context of its own, finds the IAimPlugin it
// carries, and asks it to build the AIM.
//
// The framework's own types are NOT loaded from the package: a package carrying
// its own copy of AIF.Controller would produce an IAimProcessor the Controller
// could not recognise as one. They are shared with the program, and only what the
// package adds is loaded from it.
//
// It builds what it can. Asked for an AIM whose package is missing, or built for
// another machine, it says it cannot (CanCreate), and a provider beside it -
// today, one compiled into the Service - builds that AIM instead.
public sealed class PackageAimProvider : IAimProvider
{
    private readonly AmdStore store;
    private readonly string cache;
    private readonly Action<string> say;
    private readonly Dictionary<string, IAimPlugin?> byAim = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Assembly> byPackage = new(StringComparer.OrdinalIgnoreCase);

    public PackageAimProvider(AmdStore store, string cacheFolder, Action<string>? say = null)
    {
        this.store = store;
        cache = cacheFolder;
        this.say = say ?? (_ => { });
        Directory.CreateDirectory(cache);
    }

    public bool CanCreate(string aimName) => PluginFor(aimName) is not null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage)
    {
        if (PluginFor(aimName) is not { } plugin)
            throw new InvalidOperationException($"No package provides {aimName}.");

        var processor = plugin.Create(AimPortReader.Load(store, aimName), settings);

        // AN IMPLEMENTATION IS NAMED BY ITS INSTANCE. IAimPlugin.Create is not told
        // which Instance it is building, so a plug-in names its processor with the
        // standard AIM name (MMC-EDP-V2.5); the Controller runs Instances
        // (1MMC-EDP-V2.5-I01) and would not find it. Until the plug-in contract
        // carries the Instance identifier, the provider supplies it.
        return string.Equals(processor.InstanceId, aimName, StringComparison.Ordinal)
            ? processor
            : new AsInstance(aimName, processor);
    }

    private sealed class AsInstance(string instanceId, IAimProcessor inner) : IAimProcessor
    {
        public string InstanceId => instanceId;
        public System.Threading.Tasks.Task<Message> ProcessAsync(Message message) => inner.ProcessAsync(message);
    }

    // 1MMC-TIQ-V2.5-I01 is an implementation of MMC-TIQ-V2.5, which is what a
    // plug-in declares.
    private static string StandardName(string id) =>
        Regex.Replace(Regex.Replace(id, @"^[0-9]+", ""), @"-I[0-9]+$", "");

    private IAimPlugin? PluginFor(string aimName)
    {
        if (byAim.TryGetValue(aimName, out var known)) return known;
        IAimPlugin? plugin = null;
        try { plugin = Load(aimName); }
        catch (Exception ex) { say($"  package for {aimName}: {ex.Message}"); }
        byAim[aimName] = plugin;
        return plugin;
    }

    private IAimPlugin? Load(string aimName)
    {
        var identifier = store.FindByAimName(aimName);
        if (identifier is null) { say($"  package for {aimName}: it has no L3 here."); return null; }

        var l3 = store.GetAMD(identifier).RootElement;
        if (!l3.TryGetProperty("Implementations", out var impls) || impls.ValueKind != JsonValueKind.Array)
        {
            say($"  package for {aimName}: its L3 names no Implementations."); return null;
        }

        foreach (var impl in impls.EnumerateArray())
        {
            string S(string n) => impl.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (!ForThisMachine(S("Architecture"), S("OperatingSystem"))) continue;
            var uri = S("ImplementationURI");
            var binary = S("BinaryName");
            if (uri.Length == 0 || binary.Length == 0) continue;

            var folder = Fetch(uri, identifier.ImplementationID);
            if (folder is null) continue;

            var assemblyPath = Path.Combine(folder, binary + ".dll");
            if (!File.Exists(assemblyPath)) { say($"  package for {aimName}: {binary}.dll is not in {folder}."); continue; }

            if (!byPackage.TryGetValue(assemblyPath, out var assembly))
                byPackage[assemblyPath] = assembly = new PackageContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);

            var standard = StandardName(aimName);
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(IAimPlugin).IsAssignableFrom(type)) continue;
                if (Activator.CreateInstance(type) is not IAimPlugin candidate) continue;
                if (!string.Equals(candidate.AimName, standard, StringComparison.OrdinalIgnoreCase)) continue;
                say($"  {aimName,-22} from the package {binary} at {folder}");
                return candidate;
            }
            say($"  package for {aimName}: {binary}.dll carries no plug-in for {standard}.");
        }
        return null;
    }

    private static bool ForThisMachine(string architecture, string operatingSystem)
    {
        static string Arch(string a) => a.ToLowerInvariant().Replace("-", "").Replace("_", "") switch
        {
            "x8664" or "x64" or "amd64" => "x64",
            "arm64" or "aarch64"        => "arm64",
            "x86" or "i386"             => "x86",
            _                            => a.ToLowerInvariant()
        };
        var here = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var ok = architecture.Length == 0 || Arch(architecture) == Arch(here);
        if (operatingSystem.Length > 0)
        {
            var os = operatingSystem.ToLowerInvariant();
            ok &= os.Contains("window") ? RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                : os.Contains("linux")  ? RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                : os.Contains("mac") || os.Contains("osx") ? RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                : true;
        }
        return ok;
    }

    // THE PACKAGE, IN THE SCI'S CACHE. A file: URI is copied from where the
    // Implementer put it; an http(s) URI is downloaded, and a .zip unpacked.
    // Fetched once: a package already in the cache is used as it is.
    private string? Fetch(string uri, string implementationId)
    {
        var into = Path.Combine(cache, implementationId);
        if (Directory.Exists(into) && Directory.EnumerateFiles(into, "*.dll").Any()) return into;

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) { say($"  package: '{uri}' is not an absolute URI."); return null; }
        try
        {
            if (parsed.IsFile)
            {
                var from = parsed.LocalPath;
                if (Directory.Exists(from))
                {
                    Directory.CreateDirectory(into);
                    foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        var to = Path.Combine(into, Path.GetRelativePath(from, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(file, to, overwrite: true);
                    }
                    return into;
                }
                if (File.Exists(from) && from.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile.ExtractToDirectory(from, into, overwriteFiles: true);
                    return into;
                }
                say($"  package: nothing at {uri}.");
                return null;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = http.GetByteArrayAsync(parsed).GetAwaiter().GetResult();
            Directory.CreateDirectory(into);
            var temp = Path.Combine(into, "package.zip");
            File.WriteAllBytes(temp, bytes);
            ZipFile.ExtractToDirectory(temp, into, overwriteFiles: true);
            File.Delete(temp);
            return into;
        }
        catch (Exception ex) { say($"  package at {uri}: {ex.Message}"); return null; }
    }

    // A package's own load context: what the package adds comes from the package,
    // and everything the program already has is shared with it.
    private sealed class PackageContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;
        public PackageContext(string assemblyPath) : base(isCollectible: false) => resolver = new AssemblyDependencyResolver(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            try
            {
                var shared = Default.LoadFromAssemblyName(name);
                if (shared is not null) return shared;
            }
            catch (FileNotFoundException) { /* the program does not have it: the package must */ }
            var path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
