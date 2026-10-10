using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Mpai.Mas.Service;

// The server''s own configuration, read from mas-server.json beside the
// executable, or from the path given as the first argument.
//
// FOR WHOEVER INSTALLS THIS ON A HOST. Every value below is a path or a string
// you supply; none of it is compiled in. The file looks like this:
//
//   {
//     "ListenUrl":       "https://mas.example.org:443/",
//     "CertificatePath": "/etc/mpai/cert.pem",
//     "PrivateKeyPath":  "/etc/mpai/privkey.pem",
//     "BearerToken":     "a-uuid-or-other-opaque-string",
//     "AmdDirectory":    "/opt/mpai/AIMs/AMDs",
//     "SettingsPath":    "/opt/mpai/AIMs/aim-settings.json",
//     "OutputFolder":    "/var/opt/mpai/output"
//   }
//
// THE CERTIFICATE IS A FILE, NOT A STORE ENTRY. A PEM certificate with its PEM
// private key, or a PFX with an optional password. This is deliberate: a
// certificate found in the Windows certificate store by thumbprint has no Linux
// equivalent, and one placed at a Linux convention has no Windows equivalent.
// A path works identically on both, so the same binary serves either.
//
// THE HOST NAME IN ListenUrl MUST MATCH THE CERTIFICATE. A client validates the
// name it dialled against the certificate presented. "https://0.0.0.0:443/"
// binds every interface, which is usually what a host wants; the clients still
// dial the name, and that name must be one the certificate covers.
//
// ANYTHING ABSENT IS REPORTED, NOT GUESSED. See the preflight in Program.cs.
public sealed class MasServerConfig
{
    // Where to listen. Loopback is the only address for which a missing
    // certificate is tolerated, and then only with a development certificate.
    public string ListenUrl { get; init; } = "https://localhost:5005/";

    // PEM certificate, or PFX. If the platform supplies intermediates as a
    // separate authority file, concatenating them into this file is what lets a
    // client build the chain.
    public string? CertificatePath { get; init; }

    // PEM private key. Omitted when CertificatePath is a PFX.
    public string? PrivateKeyPath { get; init; }

    // For an encrypted PFX. Omitted for PEM and for an unencrypted PFX.
    public string? CertificatePassword { get; init; }

    // THE CHAIN THE SERVER PRESENTS, not a store of things it trusts. A server
    // offers its own certificate and, with it, the intermediates a client needs
    // to build a path to a root it already has. Omit those and a client that
    // trusts the root still refuses the connection - and refuses before sending
    // anything, so the server sees no request and reports nothing.
    //
    // Harmless if the platform supplies a root here instead of intermediates:
    // presenting a certificate the client already trusts costs nothing.
    public string? AuthorityPath { get; init; }

    // WHERE THE APPS ARE. One folder per App, each holding a Workflow
    // Description, a manifest naming it, and an icon. A Service without this
    // serves Modules to clients that already know which they want; a Service
    // with it can hand an application to a client that holds none.
    public string? AppDirectory { get; init; }

    // WHICH APPS THIS SERVICE OFFERS. The Service is told; it does not decide.
    // A folder appearing under AppDirectory is where an App's files happen to
    // be, not a declaration that this Service serves it.
    public string[]? Apps { get; init; }

    // THE APP A CLIENT RUNS IN ORDER TO OFFER THE OTHERS. It is held and served
    // like any other App, and a client fetches it by name; it is not among the
    // Apps a person is offered, because an App that offered itself would be
    // chosen and would run inside itself.
    public string? Shell { get; init; }

    // COLLECTIONS: the Apps offered together, each a descriptor in AppDirectory
    // listing some of the Apps, and each served at /MPAI/AIFU/c/<name>/...
    // Absent means none: the Apps above are the offer, as they always were.
    public string[]? Collections { get; init; }

    // Which collection an address that names none receives. Absent means the Apps above.
    public string? DefaultCollection { get; init; }

    // THE STORE. When named, an App is offered only if the Store has approved the
    // L3 of its Module. Absent means no check, as before.
    public string? StoreUrl { get; init; }

    // WHERE THE L3s COME FROM. "Store": from the Store at StoreUrl (MPAI-MAS
    // actions 8-9), fetched with their Sub-AIMs into L3Cache, which the Controller
    // then reads. Absent, or anything else: from AmdDirectory, as before.
    public string? L3Source { get; init; }

    // Where L3s fetched from the Store are kept. Absent means
    // <local application data>\MPAI\SCI\L3.
    public string? L3Cache { get; init; }

    // WHERE THE AIMs COME FROM. "Packages": from the package each AIM's L3 names
    // (MPAI-MAS actions 10-13), loaded through the plug-in it carries; an AIM
    // whose package is missing or built for another machine is built by the
    // providers compiled into this Service, as they all are today. Absent: those
    // providers alone, exactly as before.
    public string? AimSource { get; init; }

    // Where packages fetched for this Service are kept. Absent means
    // <local application data>\MPAI\SCI\Packages.
    public string? PackageCache { get; init; }

    // WHERE THE MODELS COME FROM. "Fetch": a model a setting names and this machine
    // does not have is obtained from the source the settings give (<setting>.Source)
    // and checked against <setting>.SHA256. Absent: models must already be on disk,
    // as before.
    public string? ModelSource { get; init; }

    // Where fetched models are kept. Absent means
    // <local application data>\MPAI\SCI\Models.
    public string? ModelCache { get; init; }

    // SUB-AIMs THAT RUN ON ANOTHER MACHINE (M3217 3.4): a Sub-AIM whose L3
    // Relation is not Internal runs on an AIM host, and this names the host for
    // its AIM Instance - or for a composite that contains it:
    //
    //   "AimHosts": { "1MMC-EDP-V2.5-I01": "tcp+tls://other.machine:5207" }
    //
    // A Module with such a Sub-AIM and no host named for it is refused.
    public Dictionary<string, string>? AimHosts { get; init; }

    // THE GALLERY OF REGISTERED PERSONS (M3245 3.2): where Access Control
    // Registration registers and Access Control recognises - the Shared Storage of
    // the two Modules. Absent means the repository's SharedStorage folder.
    public string? Gallery { get; init; }

    // FORGOTTEN WHEN THE SESSION CLOSES (M3245 3.3; the author): what a session
    // registered is deleted when it closes - on the server. Absent or false: the
    // gallery is kept, as a local Service's.
    public bool ForgetOnClose { get; init; }

    // THE FIRST ANSWER WARM (WarmUp.cs). Before accepting anyone, each Module
    // answers a few canned turns, so that loading the language model, starting the
    // voices and the first run of each model happen here and not at a person's
    // first question. On unless false. WarmUpDirectory holds question.wav and
    // picture.jpg for the spoken and the picture turns; without them those turns
    // are skipped.
    public bool WarmUp { get; init; } = true;
    public string? WarmUpDirectory { get; init; }

    // The key the AIM hosts were started with (AIF.AimHost --key).
    public string? AimHostKey { get; init; }

    // ZERO TRUST (M3223): absent, nothing is verified, as before. Present, this
    // Service's Controller is a Trust Anchor - its anchor file, with its key or in a
    // TPM (TpmParty) - and verifies every AIM before it runs against the Store's
    // approvals; it reaches its AIM hosts by the Trust Protocol, trusting the anchors
    // HostAnchors names (files of PTF Trust Anchor objects), and, where Manufacturer
    // (a certificate) and HostCode (a JSON object, file to SHA-256) are named,
    // requires them attested; its decisions are traced in Trace.
    //
    //   "Trust": { "Anchor": "controller.json", "HostAnchors": [ "aimhost-1.json" ],
    //              "Manufacturer": "manufacturer.cer", "HostCode": "host-code.json",
    //              "Trace": "trust-trace.jsonl" }
    public TrustSettings? Trust { get; init; }

    public sealed class TrustSettings
    {
        public string? Anchor { get; init; }
        public string[]? HostAnchors { get; init; }
        public string? Manufacturer { get; init; }
        public string? HostCode { get; init; }
        public string? Trace { get; init; }
    }

    // Required of every request as "Authorization: Bearer <token>". A server
    // reachable from anywhere but loopback will not start without one.
    public string? BearerToken { get; init; }

    // Where the AIM Metadata lives. Absent means the application''s own default.
    public string? AmdDirectory { get; init; }

    // The settings file naming the models. Absent means the default.
    public string? SettingsPath { get; init; }

    // Where delivered output is written. Absent means the default.
    public string? OutputFolder { get; init; }

    // Where the schemas of the Data Types are, against which port data is
    // validated. Absent means found by walking up from the Service's own folder -
    // which finds them in the repository, not in an installation elsewhere.
    public string? SchemaDirectory { get; init; }

    // True when ListenUrl names a loopback address. This is the only case in
    // which a missing certificate and a missing token are tolerated, because it
    // is the only case in which nothing outside the machine can connect.
    public bool IsLoopback
    {
        get
        {
            if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var uri)) return false;

            return uri.IsLoopback ||
                   string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsHttps =>
        ListenUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Loads the file, or returns defaults if there is none. A malformed file is
    // an error worth stopping for: a server that silently fell back to defaults
    // would listen on the wrong address with the wrong certificate.
    public static MasServerConfig Load(
        string path)
    {
        if (!File.Exists(path)) return new MasServerConfig();

        var text = File.ReadAllText(path);

        var config = JsonSerializer.Deserialize<MasServerConfig>(
            text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return config
            ?? throw new FormatException($"{path} is not a JSON object.");
    }

    // The intermediates to present, or null when none are configured. A PEM file
    // may hold several certificates; all are loaded.
    public System.Security.Cryptography.X509Certificates.X509Certificate2Collection? LoadAuthority()
    {
        if (string.IsNullOrWhiteSpace(AuthorityPath)) return null;

        if (!File.Exists(AuthorityPath))
            throw new FileNotFoundException(
                $"The authority file does not exist: {AuthorityPath}");

        var chain = new System.Security.Cryptography.X509Certificates.X509Certificate2Collection();
        chain.ImportFromPemFile(AuthorityPath!);
        return chain.Count > 0 ? chain : null;
    }

    // The certificate this configuration names, or null when none is named.
    //
    // Throws with the path in the message when a file is named and cannot be
    // used - on a host nobody is watching, "the file at X could not be read as a
    // certificate" is worth far more than an exception from inside Kestrel.
    public X509Certificate2? LoadCertificate()
    {
        if (string.IsNullOrWhiteSpace(CertificatePath)) return null;

        if (!File.Exists(CertificatePath))
            throw new FileNotFoundException(
                $"The certificate file does not exist: {CertificatePath}");

        try
        {
            // A PEM certificate with a separate PEM key.
            if (!string.IsNullOrWhiteSpace(PrivateKeyPath))
            {
                if (!File.Exists(PrivateKeyPath))
                    throw new FileNotFoundException(
                        $"The private key file does not exist: {PrivateKeyPath}");

                var pem = X509Certificate2.CreateFromPemFile(
                    CertificatePath!, PrivateKeyPath!);

                // On Windows a certificate built from PEM cannot be used by
                // Kestrel directly; exporting and reimporting it produces one
                // that can. Harmless elsewhere, so it is done unconditionally
                // rather than behind a platform test.
                return X509CertificateLoader.LoadPkcs12(
                    pem.Export(X509ContentType.Pkcs12), null);
            }

            // A PFX, with or without a password.
            return string.IsNullOrEmpty(CertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(CertificatePath!, null)
                : X509CertificateLoader.LoadPkcs12FromFile(
                      CertificatePath!, CertificatePassword);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The certificate at {CertificatePath} could not be loaded: {ex.Message}",
                ex);
        }
    }
}