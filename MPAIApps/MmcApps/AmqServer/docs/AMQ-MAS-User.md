# AMQ-MAS - User Guidelines

**AMQ-MAS** is AMQ run across a network. The **Service** holds the models and does
the work; the **Client** holds a microphone, a screen and the Speaking Avatar, and
holds no model at all.

Two people are usually involved: whoever installs the Service, and whoever uses
the Client. This guide covers both, in that order.

---

## Part 1 - Running the Service

### What you need

- **Windows**, with the .NET 10 SDK.
- The **models** listed in the Developer guide, placed under `Models\`. These are
  the large files; the Client needs none of them.
- A **certificate**, if anyone outside this machine is to reach it.
- A **port** open in the firewall, for the same reason.

### On one machine, to try it

With Service and Client on the same computer nothing else is needed:

```
dotnet run --project MPAIApps\MmcApps\AmqServer\src\AmqServer.csproj -c Release
```

It binds `https://localhost:5005/`, uses the ASP.NET development certificate and
accepts any caller. That is for one machine only and must never be exposed.

Wait for these lines before starting a Client:

```
MMC-AMQ-V2.5: ready
[MAS] Listening on https://localhost:5005/
```

The first start takes a minute or so while the models load.

### On a machine others will reach

Write `mas-server.json` beside the executable, or pass its path as the only
argument:

```json
{
  "ListenUrl":       "https://0.0.0.0:5005/",
  "CertificatePath": "/path/to/cert.pem",
  "PrivateKeyPath":  "/path/to/privkey.pem",
  "AuthorityPath":   "/path/to/chain.pem",
  "BearerToken":     "a-uuid-or-other-opaque-string",
  "AmdDirectory":    "/opt/mpai/AIMs/AMDs",
  "SettingsPath":    "/opt/mpai/AIMs/aim-settings.json",
  "OutputFolder":    "/var/opt/mpai/output"
}
```

Then:

```
AmqServer.exe mas-server.json
```

**The Service refuses to start on a reachable address without a certificate, or
without a bearer token, and says which is missing.** That is deliberate: a Service
quietly serving a development certificate, or accepting every caller, looks
exactly like one that is working.

`AuthorityPath` is optional - it holds the intermediate certificates the Service
presents alongside its own, so a client can build a path to a root it already
trusts. Omit it and the Service behaves as though it were absent.

Open the port:

```
New-NetFirewallRule -DisplayName "MPAI MAS 5005" -Direction Inbound `
    -Protocol TCP -LocalPort 5005 -Action Allow -Profile Private,Domain
```

Then tell your users two things: **the address** - the host name the certificate
covers, not an IP address - and **the token**.

---

## Part 2 - Running the Client

### What you need

- **Windows**, with the .NET 10 runtime.
- A **microphone**, if you want to ask aloud.
- Loudspeakers or headphones.
- **No models.** That is the point of the Client.
- The address and token from whoever runs the Service.
- If the Service uses a private or self-signed certificate, its **root**
  installed in your machine's trusted roots. Ask them for it.

### Running it

Build once:

```
dotnet build MPAIApps\MmcApps\AmqClient\src\AmqClient.csproj -c Release
```

Set the address and the token in the window you will start it from:

```
$env:MPAI_MAS_SERVER = "https://the.service.name:5005/"
$env:MPAI_MAS_TOKEN  = "the token you were given"
```

Then:

```
MPAIApps\MmcApps\AmqClient\src\bin\Release\net10.0-windows10.0.19041.0\AmqClient.exe
```

It behaves exactly as the standalone AMQ does: wait for the avatar, load an
image, ask a question aloud or in writing, and the avatar answers. The difference
is invisible - the work happens on the Service.

The Client will not run without `MPAI_MAS_SERVER`. It holds no models and has
nothing to fall back to, and a Client that quietly ran the Module locally would be
carrying the models it was built to do without.

---

## If something is wrong

| What you see | What it usually means |
|---|---|
| **Client:** could not establish trust | The certificate: the name does not match the address you dialled, the root is not trusted on your machine, or the intermediates were not presented. The Service saw nothing - your machine refused before sending. |
| **Client:** connection refused | Nothing is listening on that port. Wrong port, or the Service is not running. |
| **Client hangs** | The firewall. A blocked port drops the request rather than refusing it. |
| **401 on every request** | The token differs from the Service's, or was not set. |
| **Service:** FATAL, certificate file does not exist | The path in `mas-server.json`. Relative paths resolve against the working directory, not the executable. |
| **Service:** address already in use | Another Service is still running on that port. |
| The avatar appears but never answers | The Service could not complete. Its window says what failed. |

Both halves write a log beside their executable. Read one with:

```
Get-Content amq-crash.log -Tail 20 -Encoding UTF8
```

---

## Licence

BSD 3-Clause. See `LICENSE`.
