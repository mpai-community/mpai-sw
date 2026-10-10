using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AIF.Store;

using Mpai.Core;
using Mpai.Aif.Api;
using Mpai.Aif.PortData;
using Mpai.Mas.Server;

using AIF.Controller;
using Mpai.Providers;

namespace Mpai.Mas.Service;

// MasService (formerly AmqServer) - the Modules of the Apps behind the MPAI-MAS API.
//
// FOR WHOEVER INSTALLS THIS. Everything the server needs is in mas-server.json,
// beside the executable or at a path given as the only argument. See
// MasServerConfig.cs for the file''s shape and the meaning of each value. You
// should not need to change any code.
//
//   MasService [path-to-mas-server.json]
//
// IT REFUSES RATHER THAN IMPROVISES. A server reachable from outside that
// quietly served a development certificate, or accepted every caller because no
// token was configured, would look exactly like one that was working. On a
// machine nobody is watching, that is the worst possible failure. So each of
// those conditions stops the server with a message naming what was missing.
internal static class Program
{
    private const string AcrModule = "1MMC-ACR-V2.5-I01";
    internal const string AmqModule = "1MMC-AMQ-V2.5-I01";
    private const string MacModule = "1MMC-MAC-V2.5-I01";
    internal const string MadModule = "1MMC-MAD-V2.5-I01";
    internal const string MasModule = "1MAS-APP-V1.0-I01";
    internal const string MatModule = "1MMC-MAT-V2.5-I01";
    internal const string MpdModule = "1MMC-MPD-V2.5-I01";

    private static async Task<int> Main(string[] args)
    {
        // "approve <config>": the Store's approval of what this Service deploys (below).
        var approving = args.Length > 0 && args[0] == "approve";
        if (approving) args = args[1..];

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

        // A PATH IN THE CONFIGURATION MAY BE RELATIVE: it is then relative to the folder this
        // installation is in (MpaiPaths.Root), so the folder can be put anywhere. Absent,
        // each is where the installation keeps it.
        var amdDir       = string.IsNullOrWhiteSpace(config.AmdDirectory)   ? MpaiPaths.Amds     : MpaiPaths.Resolve(config.AmdDirectory);
        var settingsPath = string.IsNullOrWhiteSpace(config.SettingsPath)   ? MpaiPaths.Settings : MpaiPaths.Resolve(config.SettingsPath);
        var appDirectory = string.IsNullOrWhiteSpace(config.AppDirectory)   ? Path.Combine(MpaiPaths.Root, "Apps") : MpaiPaths.Resolve(config.AppDirectory);
        var warmUpDir    = string.IsNullOrWhiteSpace(config.WarmUpDirectory) ? null : MpaiPaths.Resolve(config.WarmUpDirectory);
        var schemaDir    = string.IsNullOrWhiteSpace(config.SchemaDirectory) ? null : MpaiPaths.Resolve(config.SchemaDirectory);

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
                new[] { AmqModule, MadModule, MatModule, MpdModule, MasModule, MacModule, AcrModule }, amdDir, Console.WriteLine);
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

        // What is heard and said in other scripts - Japanese, Chinese - written as
        // it is, not as question marks.
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (schemaDir is not null)
        {
            if (!Directory.Exists(schemaDir))
            {
                Console.WriteLine($"FATAL: SchemaDirectory {schemaDir} does not exist.");
                return 1;
            }
            PortDataSchema.Root = schemaDir;
        }

        Console.WriteLine(PortDataSchema.Root is null
            ? "  Schemas:      NOT FOUND - port data will not be validated"
            : $"  Schemas:      {PortDataSchema.Root}");

        var store = new AmdStore(amdDir);
        store.Scan();
        Console.WriteLine($"  AMDs found: {store.Count}");

        // A SERVICE OFFERS SEVERAL APPS, SO IT HOLDS SEVERAL PROVIDERS. Adding an
        // App to this Service is adding its provider here - the providers live
        // with the Modules they build, not with the windows that drive them.
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

        // THE STORE'S APPROVAL OF WHAT THIS SERVICE DEPLOYS (M3223 3.2): for every L3 of
        // an AIM these providers implement, the fingerprint of the binary that
        // implements it, recorded where the Controller will check it. The Store's
        // act, done here by whoever deploys the Service - until the Store takes
        // binaries itself.
        if (approving)
        {
            var approvals = new AIF.Store.MpaiStore(amdDir);
            var providers = new IAimProvider[] { new AmqProvider(store), new MadProvider(store), new MatProvider(store), new MpdProvider(store), new MacProvider(store), new AcrProvider(store) };
            foreach (var aim in store.GetCatalog().Select(c => c.AIMName).Distinct().OrderBy(a => a, StringComparer.Ordinal))
            {
                var file = providers.Select(p => p.ImplementationOf(aim)).FirstOrDefault(f => f is not null);
                if (file is null) continue;
                var result = approvals.Approve(aim, new Dictionary<string, string> { [Path.GetFileNameWithoutExtension(file)] = file });
                Console.WriteLine(result.IsValid
                    ? $"  approved {aim}: {Path.GetFileName(file)}{(result.Warnings.Count > 0 ? " - " + string.Join("; ", result.Warnings) : "")}"
                    : $"  NOT approved {aim}: {string.Join("; ", result.Errors)}");
            }
            return 0;
        }

        using var north = new ControllerApi(amdDir, settingsPath, s =>
        {
            var providers = new List<IAimProvider>();
            if (fromPackages) providers.Add(new PackageAimProvider(s, packageCache, Console.WriteLine));
            providers.Add(new AmqProvider(s));
            providers.Add(new MadProvider(s));
            providers.Add(new MatProvider(s));
            providers.Add(new MpdProvider(s));
            providers.Add(new MacProvider(s));
            providers.Add(new AcrProvider(s));
            return new CompositeProvider(providers.ToArray());
        });

        // SUB-AIMs ON ANOTHER MACHINE (M3217 3.4): each runs on the AIM host named
        // for it, under this Service's Controller.
        foreach (var (aim, where) in config.AimHosts ?? new())
        {
            north.Controller.AimHosts[aim] = where;
            Console.WriteLine($"  AIM host:      {aim} at {where}");
        }
        north.Controller.AimHostKey = config.AimHostKey ?? "";

        // ZERO TRUST, where configured (M3223).
        if (config.Trust is { Anchor: { Length: > 0 } anchorFile } trust)
        {
            AIF.RootOfTrust.SimulatedTpm? tpm = null;
            AIF.Trust.TrustAnchorKey anchor;
            System.Security.Cryptography.ECDsa key;
            if (AIF.RootOfTrust.TpmParty.Is(anchorFile)) { (anchor, tpm) = AIF.RootOfTrust.TpmParty.Load(anchorFile); key = tpm.IdentityKey; }
            else (anchor, key) = AIF.Trust.TrustAnchorKey.Load(anchorFile);
            north.Controller.Trust = new AIF.Trust.TrustDomain(anchor, key, null, trust.Trace);
            if (tpm is not null) north.Controller.Attest(tpm);
            // A host's Trust Anchor object: the file holds it, or holds a party that has it.
            foreach (var hostAnchor in trust.HostAnchors ?? [])
            {
                var read = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hostAnchor))!.AsObject();
                north.Controller.HostAnchors.Add(read["Anchor"] as System.Text.Json.Nodes.JsonObject ?? read);
            }
            if (trust.Manufacturer is { Length: > 0 } manufacturer)
                north.Controller.Manufacturer = AIF.RootOfTrust.TpmParty.Manufacturer(manufacturer);
            if (trust.HostCode is { Length: > 0 } hostCode)
                foreach (var (file, sha256) in System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hostCode))!.AsObject())
                    north.Controller.HostCode[file] = (string)sha256!;
            Console.WriteLine($"  Trust:         the Controller is the Trust Anchor {anchor.AnchorId}{(tpm is null ? "" : ", its key in a TPM, its code measured")}; " +
                              $"{north.Controller.HostAnchors.Count} host anchor(s){(north.Controller.Manufacturer is null ? "" : ", hosts attested")}" +
                              (trust.Trace is null ? "" : $"; traced in {trust.Trace}"));
        }
        else Console.WriteLine("  Trust:         none configured - nothing is verified");

        var runner = new ControllerApiRunner(north, store);

        Console.WriteLine();
        Console.WriteLine("Loading models. This is the slow part.");

        // ONE CONTROLLER, ONE MODULE. PAF-RSR-V1.6 is an AIM of MMC-AMQ-V2.5 and
        // is built with it; starting it separately would put a second Module under
        // this Controller, which cannot be.
        foreach (var module in new[] { AmqModule, MadModule, MatModule, MpdModule, MasModule, MacModule, AcrModule })
        {
            var failure = runner.Start(module);
            Console.WriteLine(failure is null
                ? $"  {module}: ready"
                : $"  {module}: FAILED - {failure}");
        }

        // THE GALLERY (M3245 3.2): the Shared Storage of Access Control Registration,
        // which registers into it, and of Access Control, which recognises from it.
        var gallery = string.IsNullOrWhiteSpace(config.Gallery) ? Mpai.Core.MpaiPaths.SharedStorage : MpaiPaths.Resolve(config.Gallery);
        foreach (var module in new[] { AcrModule, MacModule })
            north.SharedStorageInit(module, gallery);
        // On the server, what a session registers is kept as long as the session.
        if (config.ForgetOnClose)
            north.SharedStorageKeep(gallery, AIF.SharedStorage.RuledStore.Default, new AIF.SharedStorage.StorageTime(AIF.SharedStorage.StorageLifetime.Session));
        Console.WriteLine($"  Gallery:       {gallery}{(config.ForgetOnClose ? ", what a session registers deleted when it closes" : "")}");

        // THE FIRST ANSWER WARM (WarmUp.cs): every engine's first-request work done
        // here, before anyone connects, not at the first person's first question.
        Console.WriteLine();
        if (config.WarmUp) WarmUp.Run(runner, settingsPath, warmUpDir, Console.WriteLine);
        else Console.WriteLine("Warm-up: off (WarmUp is false) - the first answer of each App will be slow.");

        Console.WriteLine();

        // WHAT IS OFFERED, AND TO WHOM. With collections or a Store configured, the
        // offer is built from them; without, the Apps listed are the offer, as ever.
        var offer = (config.Collections is { Length: > 0 } || !string.IsNullOrWhiteSpace(config.StoreUrl))
            ? await AppOffer.BuildAsync(appDirectory, config.Apps, config.Shell,
                                        config.Collections, config.DefaultCollection, config.StoreUrl,
                                        Console.WriteLine)
            : null;
        var catalogue = offer?.Catalogue ?? AppCatalogue.Scan(appDirectory, config.Apps, config.Shell);
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