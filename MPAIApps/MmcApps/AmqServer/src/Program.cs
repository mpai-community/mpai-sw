using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AIF.Store;

using Mpai.Core;
using Mpai.Hci.Api;
using Mpai.Mas.PortData;
using Mpai.Mas.Server;

using AIF.Controller;
using Mpai.Providers;

namespace MmcAmq.Server;

// AmqServer - MMC-AMQ behind the MPAI-MAS API.
//
// FOR WHOEVER INSTALLS THIS. Everything the server needs is in mas-server.json,
// beside the executable or at a path given as the only argument. See
// MasServerConfig.cs for the file''s shape and the meaning of each value. You
// should not need to change any code.
//
//   AmqServer [path-to-mas-server.json]
//
// IT REFUSES RATHER THAN IMPROVISES. A server reachable from outside that
// quietly served a development certificate, or accepted every caller because no
// token was configured, would look exactly like one that was working. On a
// machine nobody is watching, that is the worst possible failure. So each of
// those conditions stops the server with a message naming what was missing.
internal static class Program
{
    private const string AmqModule = "1MMC-AMQ-V2.5-I01";
    private const string MadModule = "1MMC-MAD-V2.5-I01";
    private const string MasModule = "1MAS-APP-V1.0-I01";
    private const string MatModule = "1MMC-MAT-V2.5-I01";
    private const string MpdModule = "1MMC-MPD-V2.5-I01";

    private static async Task<int> Main(string[] args)
    {
        var configPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "mas-server.json");

        Console.WriteLine("=== MPAI-MAS server (AMQ) ===");

        MasServerConfig config;
        try
        {
            config = MasServerConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {configPath} could not be read: {ex.Message}");
            return 1;
        }

        Console.WriteLine(File.Exists(configPath)
            ? $"  Configuration: {configPath}"
            : $"  Configuration: none found at {configPath} - using defaults");

        var amdDir       = config.AmdDirectory ?? MpaiPaths.Amds;
        var settingsPath = config.SettingsPath ?? MpaiPaths.Settings;

        // L3s FROM THE STORE, when so configured: the L3s of the Modules this
        // Service serves, and of their Sub-AIMs, fetched into a cache - which is
        // then the folder the Controller reads, in place of AmdDirectory.
        if (string.Equals(config.L3Source, "Store", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.StoreUrl))
            {
                Console.WriteLine("FATAL: L3Source is Store, but no StoreUrl is configured.");
                return 1;
            }
            amdDir = config.L3Cache ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "SCI", "L3");
            Console.WriteLine($"  L3s:           from the Store at {config.StoreUrl}, kept in {amdDir}");
            var fetched = await StoreL3Source.FetchAsync(config.StoreUrl,
                new[] { AmqModule, MadModule, MatModule, MpdModule, MasModule }, amdDir, Console.WriteLine);
            Console.WriteLine($"  L3s:           {fetched.Fetched} from the Store, {fetched.FromCache} from the cache, " +
                              $"{fetched.Missing.Count} missing{(fetched.Missing.Count > 0 ? ": " + string.Join(", ", fetched.Missing) : "")}");
        }

        Console.WriteLine($"  Listen:        {config.ListenUrl}");
        Console.WriteLine($"  AMDs:          {amdDir}");
        Console.WriteLine($"  Settings:      {settingsPath}");
        Console.WriteLine();

        // ---- preflight -----------------------------------------------------
        // Each check names the thing it could not find. This is the only
        // diagnostic channel available when the server runs somewhere its
        // author cannot see.

        if (!Directory.Exists(amdDir))
        {
            Console.WriteLine($"FATAL: the AMD directory does not exist: {amdDir}");
            return 1;
        }

        if (!File.Exists(settingsPath))
        {
            Console.WriteLine($"FATAL: the settings file does not exist: {settingsPath}");
            return 1;
        }

        // ---- the certificate ------------------------------------------------
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate;
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection? authority;
        try
        {
            certificate = config.LoadCertificate();
            authority   = config.LoadAuthority();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {ex.Message}");
            return 1;
        }

        if (config.IsHttps && certificate is null && !config.IsLoopback)
        {
            Console.WriteLine("FATAL: no certificate configured, and the listening");
            Console.WriteLine("       address is not loopback. Set CertificatePath and");
            Console.WriteLine("       PrivateKeyPath in mas-server.json.");
            Console.WriteLine();
            Console.WriteLine("       Serving a development certificate on a reachable");
            Console.WriteLine("       address would look like it was working.");
            return 1;
        }

        if (certificate is not null)
        {
            Console.WriteLine("  Certificate:");
            Console.WriteLine($"    Subject:    {certificate.Subject}");
            Console.WriteLine($"    Issuer:     {certificate.Issuer}");
            Console.WriteLine($"    Valid:      {certificate.NotBefore:u} to {certificate.NotAfter:u}");
            Console.WriteLine($"    Thumbprint: {certificate.Thumbprint}");

            // Said plainly, because a certificate that expired last week
            // produces a client-side error that looks like anything but.
            if (DateTime.Now > certificate.NotAfter)
                Console.WriteLine("    WARNING: THIS CERTIFICATE HAS EXPIRED.");
            else if (DateTime.Now.AddDays(14) > certificate.NotAfter)
                Console.WriteLine("    WARNING: this certificate expires within 14 days.");

            Console.WriteLine();
            Console.WriteLine("    A certificate replaced on disk is not used until this");
            Console.WriteLine("    process restarts.");
        }
        else if (config.IsHttps)
        {
            Console.WriteLine("  Certificate:  none configured - the ASP.NET development");
            Console.WriteLine("                certificate will be used. Loopback only.");
        }

        // ---- the token -------------------------------------------------------
        if (string.IsNullOrWhiteSpace(config.BearerToken) && !config.IsLoopback)
        {
            Console.WriteLine();
            Console.WriteLine("FATAL: no bearer token configured, and the listening");
            Console.WriteLine("       address is not loopback. Set BearerToken in");
            Console.WriteLine("       mas-server.json.");
            Console.WriteLine();
            Console.WriteLine("       Without one, any caller that can reach the port can");
            Console.WriteLine("       run Modules on this machine.");
            return 1;
        }

        Console.WriteLine();

        // ---- the Framework ----------------------------------------------------
        // WHAT CROSSES THE WIRE, CHECKED AGAINST THE PUBLISHED SCHEMA. Reports
        // only: a Module stopped because a schema was revised would be worse than
        // one that says so.
        PortDataSchema.Sink = (dataType, direction, complaint) =>
            Console.WriteLine($"[SCHEMA] {dataType} {direction} {complaint}");

        Console.WriteLine(PortDataSchema.Root is null
            ? "  Schemas:      NOT FOUND - port data will not be validated"
            : $"  Schemas:      {PortDataSchema.Root}");

        var store = new AmdStore(amdDir);
        store.Scan();
        Console.WriteLine($"  AMDs found: {store.Count}");

        // A SERVICE OFFERS SEVERAL APPS, SO IT HOLDS SEVERAL PROVIDERS. Adding an
        // App to this Service is adding its provider here - the providers live
        // with the Modules they build, not with the windows that drive them.
        // SUB-AIMs ON ANOTHER MACHINE, when configured: the Controller asks for each
        // AIM, and what is answered stands in for it - carrying its Ports over
        // MPAI-MAS to the Service that runs it.
        if (config.RemoteAims is { Count: > 0 } remote)
        {
            foreach (var (aim, where) in remote)
                Console.WriteLine($"  Remote AIM:    {aim} at {where}");
            AIF.Controller.Controller.RemoteAims = (aimName, relation) =>
                remote.TryGetValue(aimName, out var where)
                    ? new Mpai.Mas.Client.RemoteAim(aimName, store, where, config.RemoteToken, Console.WriteLine)
                    : null;
        }

        // MODELS FROM THE PARTIES THAT PUBLISH THEM, when configured: a model a
        // setting names and this machine does not have is fetched and checked.
        if (string.Equals(config.ModelSource, "Fetch", StringComparison.OrdinalIgnoreCase))
        {
            var modelCache = config.ModelCache ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "SCI", "Models");
            Console.WriteLine($"  Models:        fetched when missing, kept in {modelCache}");
            AimSettings.Resolve = (aim, values) =>
                AIF.Store.ModelSource.Resolve(aim, values, MpaiPaths.Root, modelCache, Console.WriteLine);
        }

        // AIMs FROM PACKAGES, when configured: the package provider is asked first,
        // and what it cannot build - a package missing, or for another machine - the
        // providers compiled into this Service build, as they always have.
        var fromPackages = string.Equals(config.AimSource, "Packages", StringComparison.OrdinalIgnoreCase);
        var packageCache = config.PackageCache ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "SCI", "Packages");
        if (fromPackages) Console.WriteLine($"  AIMs:          from their packages, kept in {packageCache}");

        using var north = new NorthApi(amdDir, settingsPath, s =>
        {
            var providers = new List<IAimProvider>();
            if (fromPackages) providers.Add(new PackageAimProvider(s, packageCache, Console.WriteLine));
            providers.Add(new AmqProvider(s));
            providers.Add(new MadProvider(s));
            providers.Add(new MatProvider(s));
            providers.Add(new MpdProvider(s));
            return new CompositeProvider(providers.ToArray());
        });
        var runner = new NorthApiRunner(north, store);

        Console.WriteLine();
        Console.WriteLine("Loading models. This is the slow part.");

        // ONE CONTROLLER, ONE MODULE. PAF-RSR-V1.6 is an AIM of MMC-AMQ-V2.5 and
        // is built with it; starting it separately would put a second Module under
        // this Controller, which cannot be.
        foreach (var module in new[] { AmqModule, MadModule, MatModule, MpdModule, MasModule })
        {
            var failure = runner.Start(module);
            Console.WriteLine(failure is null
                ? $"  {module}: ready"
                : $"  {module}: FAILED - {failure}");
        }

        Console.WriteLine();

        // WHAT IS OFFERED, AND TO WHOM. With collections or a Store configured, the
        // offer is built from them; without, the Apps listed are the offer, as ever.
        var offer = (config.Collections is { Length: > 0 } || !string.IsNullOrWhiteSpace(config.StoreUrl))
            ? await AppOffer.BuildAsync(config.AppDirectory, config.Apps, config.Shell,
                                        config.Collections, config.DefaultCollection, config.StoreUrl,
                                        Console.WriteLine)
            : null;
        var catalogue = offer?.Catalogue ?? AppCatalogue.Scan(config.AppDirectory, config.Apps, config.Shell);
        offer ??= AppOffer.FromCatalogue(catalogue);

        var server = new MasServer(
            runner,
            PortDataCodecs.Default(),
            config.ListenUrl,
            string.IsNullOrWhiteSpace(config.BearerToken) ? null : config.BearerToken,
            certificate,
            authority)
        {
            // WHAT THIS SERVICE OFFERS. Empty unless a catalogue is configured, in
            // which case a client holding no application can ask what is here.
            Catalogue = catalogue,
            Offer     = offer
        };

        // WHAT THIS SERVICE CAN ACTUALLY RUN. An App is listed only if the Service
        // was told to offer it; whether its Modules can be built is a separate
        // question, and one worth answering at startup rather than at the click.
        foreach (var app in server.Offer.Default.Apps)
            Console.WriteLine($"    {app.Id,-6} {app.Name}");
        foreach (var collection in server.Offer.Named)
            Console.WriteLine($"  Collection {collection.Id} (/MPAI/AIFU/c/{collection.Id}): " +
                              string.Join(", ", collection.Apps.Select(a => a.Id)));

        Console.WriteLine(server.Catalogue.Root is null
            ? "  Apps:         none configured"
            : $"  Apps:         {server.Catalogue.Apps.Count} in {server.Catalogue.Root}");

        await server.RunAsync();
        return 0;
    }
}