# MAS-App - Deploying a Sub-AIM on Its Own Machine

For whoever operates a machine that runs one AIM for a MAS-App Service that
lives elsewhere - your own cloud instance, a GPU box, a second site. This
guide is self-contained: it assumes .NET and ordinary systems administration,
not prior familiarity with MPAI-AIF or this repository.

For what the software is, see [MPAI Software](MPAI-Software.md). For how it
is built, see the [Developer Guide](MAS-App-Developer.md), whose section 10
this guide expands into a full walkthrough, with the certificates and
troubleshooting a real deployment needs that a same-machine test does not.

---

## 1. What this is, and why you might run it

A MAS-App Service normally builds every AIM itself, on one machine. MPAI-MAS
also lets one AIM of a Module run on a **different** machine: the main
Service still runs the Module and the conversation, but for one named AIM it
reaches out, over HTTPS, to a Service you run elsewhere, and uses what comes
back exactly as if it had built the AIM itself. Nothing about the client, the
workflow, or the rest of the Module changes.

You would run this guide's machine when the AIM in question needs resources
the main machine does not have or should not carry - a GPU for a larger
language model, for instance - or when it belongs organisationally to another
party. In this implementation, the AIM most often placed this way is
**Entity Dialogue Processing** (`1MMC-EDP-V2.5-I01`), the AIM that talks to a
language model (via Ollama) and needs the most memory and compute of the set.
Everything below is written with that AIM as the example; the mechanism is
the same for any other.

**What your machine will do:** run one MAS-App Service, configured to offer
exactly this one AIM, reachable over the network by the main Service, and
nothing else - it is not a client, it has no avatar, and a person never
addresses it directly.

## 2. Before you start

- **.NET 10 SDK** - <https://dotnet.microsoft.com/download>. Confirm with
  `dotnet --version`.
- **Ollama**, if the AIM you are hosting uses one (Entity Dialogue Processing
  does) - <https://ollama.com>. After installing, pull the model your
  configuration will name:
  ```
  ollama pull llama3.2:3b
  ```
  Confirm it answers before going further:
  ```
  ollama list
  ```
  If this step is skipped, everything below will appear to work until the
  first real exchange, which will fail with a plain connection-refused error
  on port 11434 (section 8).
- **A clone of this repository**, and the model files the AIM you are hosting
  needs (see [MAS-App - Models](MAS-App-Models.md) for what Entity Dialogue
  Processing itself needs - in its case, none beyond Ollama's own).
- **A real TLS certificate for this machine's own name**, unless it sits on a
  private network you fully control end to end (section 4). A certificate
  from your organisation's usual source (a public CA, or an internal one your
  clients already trust) is what production deployment wants; the
  `https+insecure` and plain-HTTP workarounds a same-machine test can use are
  not appropriate here and are not covered by this guide.
- **A firewall rule** admitting inbound connections, on whichever port you
  choose, from the main Service's address (section 6).
- **A bearer token** - any long, unguessable string, agreed between you and
  whoever operates the main Service. Generate one, for instance:
  ```powershell
  [System.Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
  ```
- **The address of the main Service**, and, if it uses one, of the MPAI Store
  it reads L3s from (section 3).

## 3. Where this machine gets its L3

Your machine needs the approved L3 (AIM Metadata) of the AIM it is hosting,
and nothing else. Two ways to get it, either is fine:

**From the MPAI Store**, if the organisation running the main Service runs
one (see the Developer Guide, section 10.1). Your configuration names its
address (`StoreUrl`) and your machine fetches and caches the L3 itself,
picking it up again automatically if it changes. This is the lower-friction
choice if a Store already exists.

**From a local copy**, if there is no Store, or you would rather not depend
on reaching it at start-up: copy the one file you need,
`AIMs/AMDs/1MMC-EDP-V2.5-I01.json` (or whichever AIM you are hosting), from
this repository, and point `AmdDirectory` at the folder holding it. This is
the simpler choice for a first deployment, or for an AIM whose L3 changes
rarely.

## 4. Certificates: do this properly here

A machine reachable from another organisation's infrastructure should present
a certificate a normal client trusts - not a self-signed one, and not the
`https+insecure` shortcut used for a same-machine or same-tailnet test. The
Service supports this directly (`MasServerConfig.cs`):

```json
{
  "ListenUrl":        "https://edp.yourorg.example:5443/",
  "CertificatePath":  "/etc/mpai/cert.pem",
  "PrivateKeyPath":   "/etc/mpai/privkey.pem",
  "AuthorityPath":    "/etc/mpai/chain.pem"
}
```

- `CertificatePath` / `PrivateKeyPath` - a PEM certificate and its PEM private
  key. A PFX works too: give `CertificatePath` the `.pfx` and, if it is
  password-protected, `CertificatePassword`; omit `PrivateKeyPath`.
- `AuthorityPath` - the intermediate certificates your CA issued alongside
  your own, concatenated into one PEM file, so a client can build the chain
  to a root it already trusts. Omit this and a client that would otherwise
  trust you refuses the connection outright, silently as far as your own logs
  are concerned - the request never arrives, because the client refused
  before sending it.
- **The name in `ListenUrl` must be the name the certificate covers.** A
  client dials the name and validates the certificate against it; a
  certificate for `edp.yourorg.example` will not satisfy a client dialling
  `edp.internal` or an IP address, even on the same machine.

If Let's Encrypt or your organisation's own CA issues certificates
automatically and rotates them, point `CertificatePath` and
`PrivateKeyPath` at wherever that automation writes them, and restart the
Service after a rotation (it reads the files once, at start-up).

## 5. Your machine's configuration

Write a configuration file - anywhere, its path is the Service's first
argument:

```json
{
  "ListenUrl":       "https://edp.yourorg.example:5443/",
  "CertificatePath": "/etc/mpai/cert.pem",
  "PrivateKeyPath":  "/etc/mpai/privkey.pem",
  "AuthorityPath":   "/etc/mpai/chain.pem",
  "BearerToken":     "<the token you generated in section 2>",

  "L3Source": "Store",
  "StoreUrl": "https://store.yourorg.example/",
  "L3Cache":  "/var/opt/mpai/L3",

  "SettingsPath":  "/opt/mpai/AIMs/aim-settings.json",
  "AppDirectory":  "/opt/mpai/Apps",
  "Apps": []
}
```

A few things worth being deliberate about:

- **`Apps` is empty.** This machine's job is one AIM, not a runnable App; an
  empty list is correct and expected, not an oversight. (The Service's
  start-up loop still tries to build several Modules regardless of `Apps` -
  this is a known implementation detail, not something for you to work
  around; see the Developer Guide section 10.4. A missing model or AIM your
  machine does not need does not stop the ones it does.)
- **`BearerToken` is not optional here.** `ListenUrl` is not loopback, so the
  Service refuses to start without one - this is enforced, not merely
  documented, and is the framework protecting you from accidentally running
  an open door.
- **If you are using a local L3 copy instead of a Store** (section 3), use
  `AmdDirectory` in place of `L3Source`/`StoreUrl`/`L3Cache`:
  ```json
  { "AmdDirectory": "/opt/mpai/AIMs/AMDs-local" }
  ```
- **`SettingsPath`** points at `AIMs/aim-settings.json` from this
  repository (or your own copy of the entries the AIM you host needs); it
  carries model paths and, for Entity Dialogue Processing, which Ollama model
  to use and at what address, if not the local default.

## 6. Firewall

Admit inbound connections on the port `ListenUrl` names, from the main
Service's address specifically where your policy allows narrowing it that
far, otherwise from the range your organisation's edge already restricts
traffic to. On a Linux host this is ordinarily `ufw` or `firewalld`; on
Windows, from an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "MAS-App remote AIM" -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow
```

**A blocked port and a stopped process look identical from the caller's
side**: the main Service's log will show a plain connection timeout, not a
refusal (`threw, ... A connection attempt failed because the connected party
did not properly respond ...`). If a start looks right but every exchange
times out, check the firewall before suspecting the process.

## 7. Starting it, and what the main Service needs

Start your machine's Service:

```
dotnet run --project MPAIApps/MmcApps/AmqServer/src/AmqServer.csproj -- /path/to/your-config.json
```

It is ready when it prints `[MAS] Listening on https://edp.yourorg.example:5443/`
and lists the AIM as `ready`, with no `FAILED` line for it.

Tell whoever operates the main Service to add, to **their** configuration:

```json
{
  "RemoteAims":  { "1MMC-EDP-V2.5-I01": "https://edp.yourorg.example:5443/" },
  "RemoteToken": "<the same token you generated>"
}
```

Nothing else on the main Service changes. No workflow, no client, and no
other AIM is aware that this one is remote.

## 8. Testing it

Once both Services are running, from the main Service's side, run through a
client any App whose Module uses the AIM you are hosting (MAD or MPD, for
Entity Dialogue Processing) and hold a short conversation. On your own
machine's console you should see, for each turn:

```
[MAS] Controller Instance <id>
[MAS] Module 1MMC-EDP-V2.5-I01 started as <id>
[MAS] input OSD-BTO-V1.5#1 (... bytes)
[MAS] input MMC-EPS-V2.5#1 (... bytes)
[AIF] 1MMC-EDP-V2.5-I01: started on its own, Ports=2
```

and, on a second turn, a growing `MMC-SUM-V2.5` input alongside the others -
this is the conversation's memory arriving from the main Service and being
handed back, growing turn over turn, the concrete sign that the remote path
is genuinely carrying the exchange and not merely reachable.

## 9. Troubleshooting

| What you see | What it means | What to do |
|---|---|---|
| `FATAL: no bearer token configured, and the listening address is not loopback` | `ListenUrl` is not loopback and `BearerToken` is absent. | Add `BearerToken` to your configuration (section 5). |
| `A connection attempt failed because the connected party did not properly respond` (on the main Service's side, for this AIM) | Almost always a firewall silently dropping the connection, not your process being down. | Check the firewall rule (section 6) before restarting anything. |
| `The SSL connection could not be established, see inner exception` | A scheme or certificate mismatch - most often `StoreUrl` using `https://` where the Store itself is plain HTTP, or the reverse. | Confirm both ends use the same scheme, and that `ListenUrl`'s host name matches the certificate (section 4). |
| Your Service starts, but the AIM it needs shows `FAILED - ... File doesn't exist` (a model) | The model file the AIM needs is not on this machine at the path `aim-settings.json` names. | Obtain the model (see [MAS-App - Models](MAS-App-Models.md)) and place it at that path, or point the setting at where you keep it. |
| `LLM call failed (is Ollama running?): ... (127.0.0.1:11434)` | Ollama is not running on this machine, or the AIM's settings point at the wrong address. | Start Ollama (`ollama serve`, or as a service) and confirm `ollama list` shows the model this AIM's settings name. |
| The exchange completes, but the browser client shows an error and the desktop client does not | The browser client (`RcaWeb.Host`) does not currently support presenting a `BearerToken` to the main Service; this only matters if the **main** Service (not yours) also requires one and is being reached through the browser client. Not something this machine can fix. | Use the desktop client against a token-gated main Service, or leave the main Service's own `BearerToken` unset if it is reachable only on a network you already trust. |
| Everything above looks right, and it still does not work | Confirm the AIM's Data Types have a wire codec registered (`MW/PortData/IPortDataCodec.cs`) on **both** machines - an AIM whose Ports carry a Data Type with no codec cannot cross MPAI-MAS at all, and the failure surfaces as a schema or serialisation error rather than anything naming the missing codec directly. | See the Developer Guide, section 10.3, for what a new AIM placed remotely needs beyond configuration. |

## 10. What this machine does not need

To keep the scope of a first deployment small: this machine does not need
the browser or desktop client, the avatar assets, an MPAI Store of its own,
or any App folder beyond an empty `Apps` list. It needs exactly the AIM it
hosts, that AIM's L3, that AIM's models, and the configuration above.
