# AMQ-MAS - Developer Guidelines (Software Architecture)

**AMQ-MAS** is Audio-Visual Multimodal Question Answering run across a network: a
Client with no models asks a Service that has them. This document is for
developers - which blocks exist, what each is called, what passes between them,
and what the HTTPS layer requires.

The standalone application is `AmqApp`, documented separately. It runs its Module
in process and knows nothing of MPAI-MAS. These are different applications for
different users and they no longer share a window.

---

## 1. What AMQ-MAS is

```
  CLIENT  (AmqClient.exe)                    SERVICE  (AmqServer.exe)
  no models, ~28 MB                          all models, several GB
  microphone, screen, avatar                 MMC-AMQ-V2.5, PAF-RSR-V1.6

      RemoteNorthApi  ---- HTTPS ---->  MasServer  ----> AIF Controller
                      <---------------             <----
```

The split is not between "user interface" and "logic". It is between **what must
be where the user is** - a microphone, a loudspeaker, a screen - and **what must
be where the models are**. Acquisition and delivery are the User Agent's;
everything that reasons is the Service's.

---

## 2. The functional blocks

### On the Client

| Block | Assembly | What it does |
|---|---|---|
| `AmqClient` | `AmqClient.exe` | The window: image, question, avatar. No model, no Framework, no AIM that reasons. |
| `Mpai.UaKit` | `Mpai.UaKit.dll` | Speaking-Avatar host: renders a Speech Object and Face Descriptors as a talking face; captures speech. |
| `Mpai.Mas.Client` | `Mpai.Mas.Client.dll` | `RemoteNorthApi` - the whole of the Client's knowledge of the network. |
| `Mpai.Mas.PortData` | `Mpai.Mas.PortData.dll` | Port-data serialisers and schema validation. **Shared with the Service.** |
| `Mpai.Hci.Api` | `Mpai.Hci.Api.dll` | `INorthApi`, the seam the window is written against. |
| `Mpai.Core` | `Mpai.Core.dll` | Data Types, Qualifiers, JSON. |
| `CAE-AOA`, `MMC-SOD` | device AIMs | Audio acquisition and speech delivery - the AIMs that touch hardware, which travels with the user. |

### On the Service

| Block | Assembly | What it does |
|---|---|---|
| `AmqServer` | `AmqServer.exe` | Reads the configuration, loads the AIM Metadata, instantiates the Modules, starts Kestrel. |
| `Mpai.Mas.Server` | `Mpai.Mas.Server.dll` | `MasServer` - the MPAI-MAS routes; `MasServerConfig` - configuration and TLS material. |
| `AIF.Controller` | `AIF.Controller.dll` | The AI Framework: `UserAgent`, `Controller`, `MachineExecutor`, `AimHost`, `PortRegistry`. |
| `MMC-AMQ-V2.5` | Module | `MMC-ASR` (speech to text), `MMC-TIQ` (the visual question), `MMC-TTS` (text to speech). |
| `PAF-RSR-V1.6` | Module | Renders a spoken response with a face. |
| `Mpai.Mas.PortData` | `Mpai.Mas.PortData.dll` | The same serialisers as the Client. |

**One assembly on both sides.** `Mpai.Mas.PortData` is not duplicated: Client and
Service run the same code to write and read the wire, so a serialisation
difference between them is impossible by construction.

---

## 3. The interfaces

### 3.1 `INorthApi` - the seam

Declared in `MW/HciApi/INorthApi.cs`. Two implementations: `NorthApi` in process,
`RemoteNorthApi` over MPAI-MAS. The Client constructs the second and never refers
to the network again.

| Operation | Meaning |
|---|---|
| `StartFlow(module)` | Start a Module by its AIM name. |
| `Advance(module, inputs)` | Supply boundary data and run. Returns the outputs, or `Suspended` with the Port it waits for. |
| `StopFlow(module)` | Stop the Module and release it. |

`Result.WaitingPort` names the Data Type and Port Number of a boundary input that
was never supplied. A client that reports it turns a silent stall into one line of
diagnosis.

### 3.2 `IPortDataCodec` - what crosses the wire

One serialiser per Data Type, in `MW/PortData`:

```csharp
PortDataCodecs.Default()
    .Register(new BasicSpeechObjectCodec())     // OSD-BSO-V1.5
    .Register(new BasicTextObjectCodec())       // OSD-BTO-V1.5
    .Register(new BasicVisualObjectCodec())     // OSD-BVO-V1.5
    .Register(new FaceDescriptorsObjectCodec()) // PAF-FDO-V1.6
    .Register(new SimpleTimeCodec());           // OSD-STM-V1.5
```

`ToWire` converts the internal representation to the published schema instance;
`ToInternal` converts it back. They are not the same shape - a Speech Qualifier is
`Format` in the code and `Formats` on the wire - which is why the serialisers
exist.

Every crossing is validated against the published JSON schema by
`PortDataSchema`, which **reports and does not refuse**. The Service states at
startup where it found the schemas, or says it did not: silence from a check that
never ran looks exactly like silence from a check that passed.

---

## 4. The MPAI-MAS interface over HTTPS

*This clause is normative for interoperability. A client that follows it works
against any conforming Service; a Service that follows it serves any conforming
client.*

### 4.1 The routes

```
POST   /MPAI/AIFU/Controller                          -> 201 {"id": "<cid>"}
POST   /MPAI/AIFU/{cid}/MODULE/Start  {"module":"x"}  -> 200 {state}
GET    /MPAI/AIFU/{cid}/MODULE/{mid}                  -> 200 {state}
GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Pause            -> 200 {state}
GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Resume           -> 200 {state}
GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Stop             -> 200
POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Input/{pid}      -> 200
GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{pid}     -> 200  port data
DELETE /MPAI/AIFU/Controller/{cid}                    -> 200
```

`{cid}` is the Controller Instance from the first call; `{mid}` the Module
Instance from Start. A Service may return an alternative route prefix in the
creation response, and a client must then use it in place of `/MPAI/AIFU`.

### 4.2 The Port identifier

**MPAI-MAS V1.0 does not define the format of `{pid}`.** This implementation uses
the Data Type, optionally followed by a colon and the Port Number - the same pair
the Metadata declares:

```
OSD-BVO-V1.5        the Module declares one Visual Object Port
OSD-BTO-V1.5:2      it declares several Text Object Ports; this is the second
```

Any client and Service must agree on this, and it is the first thing to check
when an Input is accepted and nothing happens.

A Port is addressed by its Data Type and its Port Number, never by a name.
**`PAF-RSR-V1.6` declares `OSD-BTO` twice** - Port 1 feeds Text-To-Speech, Port 2
feeds Generative Face Description - and supplying only the first produces a voice
with a face that does not move to the words.

### 4.3 The body

Content type `MPAI/port-data`. The body is the Data Type's published byte
serialisation - what `IPortDataCodec.ToWire` produces.

An Output Port that produced nothing this run answers **404**, which is not an
error: an optional Port that did not fire is a normal outcome.

---

## 5. HTTPS, in detail

**MPAI-MAS requires TLS.** The Service refuses to start on a reachable address
without a certificate, and refuses without a bearer token, naming which is
missing. A Service quietly serving a development certificate, or accepting every
caller, looks exactly like one that is working.

### 5.1 What the Service needs

| Setting | Meaning |
|---|---|
| `ListenUrl` | Address and port. `https://0.0.0.0:5005/` binds every interface. **The host name clients dial must be one the certificate covers** - a client validates the name it dialled, not the address it reached. |
| `CertificatePath` | PEM or PFX. With PEM, `PrivateKeyPath` accompanies it; with an encrypted PFX, `CertificatePassword`. |
| `PrivateKeyPath` | The private key, when the certificate is PEM. |
| `CertificatePassword` | For an encrypted PFX only. |
| `AuthorityPath` | Optional. The intermediates the Service presents alongside its own certificate. |
| `BearerToken` | Required on any reachable address. Sent as `Authorization: Bearer <token>`. |

### 5.2 The certificate is the thing that goes wrong

In order of how often each is met:

- **The name does not match.** A certificate for one host name presented to a
  client dialling another is refused *before the request is sent*, so the Service
  sees nothing and reports nothing. The failure is visible only at the client.
- **The root is not trusted.** A self-signed or privately issued certificate must
  be imported into each client machine's trusted roots. The Service cannot do
  this for them.
- **The intermediates are missing.** A client that trusts the root still refuses
  if it cannot build the chain. `AuthorityPath` presents them.
- **The certificate expired.** The Service prints Subject, Issuer, validity and
  thumbprint at startup for exactly this reason.
- **A renewed certificate is not picked up.** The file is read at startup; a
  certificate replaced on disk requires a restart.

A Service on loopback only - `https://localhost:5005/` - may run with no
certificate and no token, using the development certificate. That is for one
machine and must never be exposed.

### 5.3 A test certificate, for development

```powershell
# On the Service machine, once:
$c = New-SelfSignedCertificate -DnsName mas.example.local `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1)
$pw = ConvertTo-SecureString "changeit" -Force -AsPlainText
Export-PfxCertificate -Cert $c -FilePath cert.pfx -Password $pw
Export-Certificate   -Cert $c -FilePath cert.cer

# On each client machine, as administrator:
Import-Certificate -FilePath cert.cer -CertStoreLocation Cert:\LocalMachine\Root
# and, if the name is not in DNS, add it to the hosts file:
#   <service-ip>   mas.example.local
```

**A test certificate is a development convenience and nothing else.** The private
key, the PFX and the password must never enter a repository, a package or an
archive.

### 5.4 Firewall

A Service bound to a reachable address still needs the port opened. Without it a
client hangs rather than being refused, which is the least informative failure of
the set.

```powershell
New-NetFirewallRule -DisplayName "MPAI MAS 5005" -Direction Inbound `
    -Protocol TCP -LocalPort 5005 -Action Allow -Profile Private,Domain
```

---

## 6. Configuration

The Service reads one file: `mas-server.json` beside the executable, or any path
given as its only argument. Nothing is compiled in.

**This file holds a bearer token and the path to a private key. It is excluded
from the repository and must stay excluded.**

The Client is told where to go by two environment variables:

```
MPAI_MAS_SERVER = https://mas.example.local:5005/
MPAI_MAS_TOKEN  = <the same token the Service was given>
```

---

## 7. Build closure

24 projects, computed by following `ProjectReference` from both roots:

```
AIF/V3.0/src/{AIF.Controller, AIF.Store, AIF.SharedStorage, AIF.GlobalStorage}
AIMs/Core
AIMs/MMC/V2.5/{ASR,TIQ,TTS,SOA,SOD,SOD.Windows}
AIMs/PAF/V1.6/{PSD,GFD}
AIMs/CAE3/V1.0/{AOA,AOA.Windows,AOD}
AIMs/OSD/V1.5/TOD
MW/{HciApi, MasClient, MasServer, PortData}
UAs/Lib/UaKit
MPAIApps/MmcApps/{AmqServer, AmqClient}
```

Plus one file from the standalone application: **`MPAIApps/MmcApps/AmqApp/src/AmqProvider.cs`**.
`AmqServer` links it rather than copying it, because both forms of AMQ run
`MMC-AMQ-V2.5` and must construct the same AIMs. The window was separated
deliberately; the provider is shared deliberately.

Also required at run time: `AIMs/AMDs/`, `AIMs/aim-settings.json`, `schemas/`,
and - for the Client - `UAs/Assets/`.

```
dotnet build MPAIApps\MmcApps\AmqServer\src\AmqServer.csproj -c Release
dotnet build MPAIApps\MmcApps\AmqClient\src\AmqClient.csproj -c Release
```

---

## 8. Models and prerequisites

**On the Service only.** The Client needs none - that is its purpose.

| Model | Used by | Under `Models\` |
|---|---|---|
| whisper.cpp + model | `MMC-ASR-V2.5` | `Whisper\bin\whisper-cli.exe`, `Whisper\models\ggml-*.bin` |
| BLIP (ONNX) | `MMC-TIQ-V2.5` | `BLIP\onnx\*.onnx`, `BLIP\blip-vqa-base\vocab.txt` |
| Piper voices | `MMC-TTS-V2.5` | `Piper\piper_windows_amd64\piper\piper.exe`, `Piper\voices\...` |
| espeak-ng | `PAF-GFD-V1.6` | Optional. Without it the mouth follows the spelling rather than the sounds, and the AIM says so. |

**An ONNX model is often two files.** The `.onnx` holds the network's structure
and may be a megabyte; the `.onnx.data` beside it holds the weights and may be
hundreds. A copy bringing one and not the other produces a model that loads and
fails at first use.

**A setting that names a path is resolved against the application's own root.** A
relative path works wherever the folder is placed; an absolute one binds the
installation to one machine.

---

## 9. When it does not work

| What you see | What it is |
|---|---|
| Client: could not establish trust | The certificate. Name mismatch, untrusted root, or missing intermediates. The Service saw nothing. |
| Client: connection refused | Nothing listening on that port. |
| Client hangs | The firewall. A blocked port drops the packet rather than refusing it. |
| 401 on every call | The token differs, or was not sent. |
| Service: FATAL, certificate file does not exist | The path in the configuration; relative paths resolve against the working directory. |
| Service: address already in use | Another Service is still running on that port. |
| `suspended, waiting for ...` | A required boundary input was never supplied. The Port named is the diagnosis. |
| The avatar speaks but the mouth is wrong | espeak-ng absent, or `OSD-BTO` supplied at Port 1 and not Port 2. |
| `[SCHEMA]` lines at the Service | Port data that does not validate against the published schema. Reported, not refused. |

---

## 10. The same infrastructure carries other applications

**Nothing in the MAS layer knows what AMQ is.**

- `MasServer` routes on `{cid}`, `{mid}` and `{pid}` - a Controller Instance, a
  Module Instance and a Port. It has no list of applications.
- `AIF.Controller` instantiates whatever Module the AIM Metadata describes.
  Which AIMs, which Ports, which Topology - all read, none compiled.
- `Mpai.Mas.PortData` is a registry keyed by Data Type. A new Data Type is a new
  entry, not a change to the protocol.
- `RemoteNorthApi` implements `INorthApi` and names no application.

A second application supplies an AIM Metadata file, a provider, serialisers for
any new Data Type, and a window written against `INorthApi`. `MadServer` and
`MadClient` are the demonstration: the same MAS layer serving a different Module.

---

## Licence

BSD 3-Clause. See `LICENSE`.
