using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MmcMad.Server;

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

    // Required of every request as "Authorization: Bearer <token>". A server
    // reachable from anywhere but loopback will not start without one.
    public string? BearerToken { get; init; }

    // Where the AIM Metadata lives. Absent means the application''s own default.
    public string? AmdDirectory { get; init; }

    // The settings file naming the models. Absent means the default.
    public string? SettingsPath { get; init; }

    // Where delivered output is written. Absent means the default.
    public string? OutputFolder { get; init; }

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