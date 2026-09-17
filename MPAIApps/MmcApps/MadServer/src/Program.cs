using System;
using System.IO;
using System.Threading.Tasks;

using AIF.Store;

using Mpai.Core;
using Mpai.Hci.Api;
using Mpai.Mas.PortData;
using Mpai.Mas.Server;

using HciMad;   // MadProvider, compiled in from MadApp

namespace MmcMad.Server;

// MadServer - MMC-MAD behind the MPAI-MAS API.
//
// THIRTY LINES OF APPLICATION. Everything above this is shared with AmqServer
// and with whatever comes next: the MAS routes, the Port-data serialisers, the
// certificate handling, the preflight. What MAD contributes is its provider and
// the Modules to warm at startup. Compare this file with AmqServer's Program.cs
// - the difference is two names.
//
//   MadServer [path-to-mas-server.json]
//
// See MasServerConfig.cs for the configuration file's shape.
internal static class Program
{
    private const string MadModule = "MMC-MAD-V2.5";
    private const string RsrModule = "PAF-RSR-V1.6";

    private static async Task<int> Main(string[] args)
    {
        var configPath = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "mas-server.json");

        Console.WriteLine("=== MPAI-MAS server (MAD) ===");

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
            return 1;
        }

        if (certificate is not null)
        {
            Console.WriteLine("  Certificate:");
            Console.WriteLine($"    Subject:    {certificate.Subject}");
            Console.WriteLine($"    Issuer:     {certificate.Issuer}");
            Console.WriteLine($"    Valid:      {certificate.NotBefore:u} to {certificate.NotAfter:u}");
            Console.WriteLine($"    Thumbprint: {certificate.Thumbprint}");

            if (DateTime.Now > certificate.NotAfter)
                Console.WriteLine("    WARNING: THIS CERTIFICATE HAS EXPIRED.");
            else if (DateTime.Now.AddDays(14) > certificate.NotAfter)
                Console.WriteLine("    WARNING: this certificate expires within 14 days.");
        }

        if (string.IsNullOrWhiteSpace(config.BearerToken) && !config.IsLoopback)
        {
            Console.WriteLine();
            Console.WriteLine("FATAL: no bearer token configured, and the listening");
            Console.WriteLine("       address is not loopback. Set BearerToken in");
            Console.WriteLine("       mas-server.json.");
            return 1;
        }

        Console.WriteLine();

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

        using var north = new NorthApi(amdDir, settingsPath, s => new MadProvider(s));
        var runner = new NorthApiRunner(north, store);

        Console.WriteLine();
        Console.WriteLine("Loading models. This is the slow part.");

        foreach (var module in new[] { MadModule, RsrModule })
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
            authority);

        await server.RunAsync();
        return 0;
    }
}