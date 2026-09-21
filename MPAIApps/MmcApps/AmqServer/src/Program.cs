using System;
using System.IO;
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
        using var north = new NorthApi(amdDir, settingsPath, s => new CompositeProvider(
            new AmqProvider(s),
            new MadProvider(s),
            new MatProvider(s),
            new MpdProvider(s)));
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
            Catalogue = AppCatalogue.Scan(config.AppDirectory, config.Apps, config.Shell)
        };

        // WHAT THIS SERVICE CAN ACTUALLY RUN. An App is listed only if the Service
        // was told to offer it; whether its Modules can be built is a separate
        // question, and one worth answering at startup rather than at the click.
        foreach (var app in server.Catalogue.Apps)
            Console.WriteLine($"    {app.Id,-6} {app.Name}");

        Console.WriteLine(server.Catalogue.Root is null
            ? "  Apps:         none configured"
            : $"  Apps:         {server.Catalogue.Apps.Count} in {server.Catalogue.Root}");

        await server.RunAsync();
        return 0;
    }
}