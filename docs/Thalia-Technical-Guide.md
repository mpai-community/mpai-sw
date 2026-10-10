# Thalia - Technical Guide

For those who want to build their own Thalia, change what she does, or add an App.
To use Thalia, see the [User Guide](Thalia-User-Guide.md). The models she needs, and
where to download each, are in [Thalia - Models](Thalia-Models.md).

---

## 0. Building your own Thalia

**You need:** a computer with Windows 10/11 (x86-64) or Linux (x86-64), with about
16 GB of memory and 12 GB of disk; the **.NET 10 SDK**; **Git**; **Ollama**; and a browser (Edge or
Chrome). A GPU is not required. The Linux installation, as a service, is described in
`MAS/Service/linux/README.md`.

1. **Get the software.** `git clone https://github.com/mpai-community/mpai-sw.git`. The folder may
   be put anywhere and have any name: the software finds its own root by going up from
   where it runs to the folder that holds `AIMs` and `UserAgent`, and nothing in it names a drive or
   a folder.
2. **Get the models.** Download each model listed in [Thalia - Models](Thalia-Models.md),
   place it under `Models/` at the path named in `AIMs/aim-settings.json`, and check its SHA-256.
   Install Ollama and run `ollama pull llama3.2:3b`. The two ONNX files that the models' authors do not
   publish - the speaker model (ECAPA-TDNN, used by MAC and ACR) and BLIP (used by AMQ) - are published by MPAI at
   <https://huggingface.co/mpai-community>; BLIP is also in this repository. To make the speaker file yourself,
   see `docs/conversion/export_ecapa_tdnn.py`.
3. **Trust the development certificate:** `dotnet dev-certs https --trust`.
4. **Configure the Service.** Copy `MAS/Service/mas-server.example.json` to `MAS/Service/mas-server.json`.
   The example names no folder; a path you add to it may be relative to the folder of the software.
5. **Start the Service:** `dotnet run --project MAS/Service/src/MasService.csproj -- MAS/Service/mas-server.json`.
   It loads the models, which takes a minute or two, and prints `[MAS] Listening on ...` when it is ready.
6. **Start the browser client:** `dotnet run --project UserAgent/Clients/Browser/Host/RcaWeb.Host.csproj -- --Service https://localhost:5105/ --Urls https://localhost:5010`,
   and open `https://localhost:5010`.

**Notes**
- **A desktop client is not distributed.** An earlier Windows desktop client has not been
  tested for a long time and is not in this repository; Thalia is used through the browser.
- **ONNX models run on the CPU.** The ONNX Runtime package used (`Microsoft.ML.OnnxRuntime`) is the CPU
  build, so BLIP, M2M100 and the face and voice models run on the CPU, and the Service says so when it loads
  them. Using a graphics card for them needs the GPU build of the package, which has not been tested here.
  Whisper and the language model have their own GPU support in whisper.cpp and Ollama.
- **Thalia's face is a Ready Player Me avatar** (`UserAgent/Assets/cav-avatar.glb`, CC BY-NC-SA 4.0, for
  demonstration). The avatar page shows a notice on the screen whenever the face is shown. To use the
  software for anything else, replace the avatar by one you may use and change the notice in
  `UserAgent/Assets/cav-webview.html`; see `THIRD-PARTY-NOTICES.md`.
- **Anything beyond this** - another Ollama model, other voices, other Apps - is a matter of the settings and
  the L3s described below.

## 1. The parts

| Part | Project | Role |
|---|---|---|
| Service | `MAS/Service/src` | Hosts the Controller and the Modules; speaks MPAI-MAS over HTTPS; offers the Apps. |
| Browser client | `UserAgent/Clients/Browser/Client` | Blazor WebAssembly. The User Agent, in a browser: interprets workflows, captures, renders the avatar. |
| Browser host | `UserAgent/Clients/Browser/Host` | Serves the browser client and the avatar page, and forwards `/MPAI/AIFU/...` to the Service, so the browser sees one origin. |
| Apps | `Apps/MAD`, `AMQ`, `MAT`, `MPD`, `MAC`, `ACR` | Each: a workflow (`.orch`), `app.json`, an icon. MAC and ACR are access control and registration. |
| MPAI-MAS | `UserAgent/Orchestration/MPAI-MAS.orch` | The client's own workflow: the container the Apps run in. Not an App. |

Shared software: the Controller (`AIF/`), the AIMs and their L3s (`AIMs/`), the
User Agent (`UserAgent/`: the WDL reader, the interpreter, the remote Controller API and
the avatar assets), the schemas (`schemas/`).

## 2. How a turn travels

1. The client's workflow **acquires** a datum - speech, typed text, a picture,
   a face - through the client's sources.
2. It **offers** it to the Module at a boundary Port (`OSD-BSO-V1.5:1`, ...) and
   **asks** for the Ports it wants back. The offers of one exchange are held
   until the ask.
3. Over MPAI-MAS each offer is an HTTP POST to `.../MODULE/{mid}/Input/{pid}`;
   the first GET of `.../Output/{pid}` closes the exchange, and the Service runs
   the Module on what was written. `{pid}` is the Data Type, with `:n` when the
   Module has more than one Port of that Data Type on that side.
4. The Controller runs the Module's AIMs in Topology order. An AIM with no input
   in this exchange does not run; nothing is suspended waiting for a later input.
5. The client **presents** what came back - speech and face together on the
   avatar, text on the screen.

## 3. The L3

A Module's L3 (`AIMs/AMDs/<Instance>.json`) lists its `SubAIMs`, `ExternalPorts`,
`InternalTypes` and `Topology`. Rules the Controller applies:

- **Names are labels.** A Topology line's Port names are resolved once, only
  against the Module's own `ExternalPorts` and `InternalTypes`, to find the Data
  Type of the flow. A Sub-AIM's own Port names are never consulted: its Port is
  found by Data Type, Direction and Port Number. (L3s not yet written this way
  load through a logged "legacy" path.)
- **`PortNumber`** distinguishes Ports of one Data Type on one side; absent means 1.
- **`Input` and `Output`** (M3194): a composite that wants one supply delivered to
  several of its Ports gives each the same `Input` number - RSR's two Text Ports,
  for Text-To-Speech and Generative Face Description, both declare `Input: 1`. The
  flow sent to them declares `Output: 1` on its `InternalType`, or on the boundary
  `ExternalPort` it enters through. The Controller expands such an edge into one
  connection per Port of the group. An edge into a group with no `Output`, or an
  `Output` matching no group, stops the Module from loading.
- **`IsOptional`** documents that a Port may be absent in an exchange.

Every App gives the words the client wants spoken their own Text Port
(`OSD-BTO-V1.5:1`, `Output: 1`, to RSR) and typed input another (`OSD-BTO-V1.5:2`).

## 4. The Workflow Description Language

A workflow runs over one Module, named in its header:

```
workflow MMC-MAD over 1MMC-MAD-V2.5-I01

on Start:
    ask Controller to start
    offer Welcome (OSD-BTO-V1.5:1) = "..."
    ask   WelcomeSpeech (OSD-BSO-V1.5), WelcomeFaceDescriptors (PAF-FDO-V1.6)
    present WelcomeSpeech, WelcomeFaceDescriptors

    loop until Stop:
        acquire UserSpeech (OSD-BSO-V1.5) via VAD
             or UserText   (OSD-BTO-V1.5)
        branch on UserText {
            offer UserText (OSD-BTO-V1.5:2)
        } else {
            offer UserSpeech (OSD-BSO-V1.5:1)
        }
        ask   MachineSpeech (OSD-BSO-V1.5), MachineFaceDescriptors (PAF-FDO-V1.6),
              Memory (MMC-SUM-V2.5)
        present MachineSpeech, MachineFaceDescriptors
        offer Memory (MMC-SUM-V2.5)

on Stop:
    ask Controller to stop
```

| Step | Meaning |
|---|---|
| `offer L (T[:n])` / `= "text"` | Give the Module a datum (held until the next `ask`). A literal is allowed for Text. |
| `ask L (T[:n]), ...` | Close the exchange and take back the named Ports. |
| `acquire L (T) [via VAD] [= {Qualifier}]` | Obtain a datum from the person or the world. The Qualifier says what is wanted, e.g. `{ "Attributes": { "VisualObjectType": "Face" } }` for a face from the camera. |
| `acquire A (T) ... or B (T) ...` | Wait for all, keep the first to arrive. |
| `branch on L { } else { }` | Follow the path a label took (true if it arrived), or `branch on L contains "yes"`. |
| `present`, `display`, `prompt`, `wait`, `end`, `run L` | Render; show text; show a message; pause; leave the loop; run the App a datum names. |
| `on Stop:` | Runs when the App ends, even after the Stop button. |

**Memory.** EDP keeps no memory of its own: each turn asks for `Memory
(MMC-SUM-V2.5)` and offers it back with the next. A new run starts with none.

## 5. The Service

`MasService` (formerly `AmqServer`) reads its configuration (`mas-server*.json`, path as first argument;
see the User Guide) and builds one Controller whose AIMs come from a composite
of providers - `AmqProvider`, `MadProvider`, `MatProvider`, `MpdProvider` in
`AIMs/Providers`. It preloads `1MMC-AMQ`, `MAD`, `MAT`, `MPD` and `1MAS-APP` so
the first exchange of each App is fast.

The Data Types it carries are those `AIF/PortData` can translate between their
internal form and their wire form: `OSD-BSO`, `OSD-BTO`, `OSD-BVO`, `PAF-FDO`,
`OSD-STM`, `OSD-SEL`, `MMC-SUM`. A datum of another Data Type cannot cross.

Optional: a `BearerToken` in the configuration requires callers to present it; the browser
host forwards the one in the environment variable `MPAI_MAS_TOKEN`.

## 6. The client

The browser client registers **sources** (acquire) and **presenters** (present) with a
`DeviceRegistry`, and runs workflows with the interpreter, `UserAgent/Rca`. Every call it makes on the
Controller API is awaited (`IAsyncControllerApi`), because a browser never lets WebAssembly block.

- **Client** (`Browser/Client`): the sources are the microphone with voice activity detection, the
  text box, the App list, the language picker, a file or the camera. The capture is in `wwwroot/js/rca.js`:
  the microphone runs from Start and keeps the last second heard, and typing ends listening. It hands the
  interpreter `RemoteControllerApiAsync` (`UserAgent/Remote`), which awaits the network.
- **Host** (`Browser/Host`): serves the avatar page (`UserAgent/Assets`) with its asset addresses and
  messaging adapted for a browser as it is sent, and forwards `/MPAI/AIFU/...` to the Service.

## 7. Adding an App

1. **The Module:** write its L3 in `AIMs/AMDs/`, following section 3. Give it a
   Text Port for spoken prompts and, if it answers with the avatar, contain RSR.
2. **Its AIMs:** if it needs AIMs no provider builds, add a provider in
   `AIMs/Providers` and add it to the Service's composite.
3. **Its Data Types:** any Data Type that must cross MPAI-MAS needs a translator
   in `AIF/PortData`, registered in `PortDataCodecs.Default()`.
4. **The App:** a folder in `Apps/` with its workflow, `app.json` and `icon.svg`.
5. **The Service:** add the App to `Apps` in the configuration, and its Module to
   the preload list in `MAS/Service/src/Program.cs`.
6. **Test:** start the Service: it loads every Module it serves and reports which load and which
   fail; then run the App through the browser client.

## 8. Diagnostics

All diagnostics go through `AIMs/Core/MpaiDiag.cs`: off unless `MPAI_DIAG=1`,
and then written only to `<temporary folder>/mpai-diag`. Use it for any new
trace; never write to a folder of your own.

## 9. Open items

- The executor finds a Sub-AIM's output by Data Type only, ignoring the Port
  Number resolved at load time: in AMQ a spoken yes or no is also given to Text
  and Image Query, which then fails harmlessly.
- MPD: EDP's instructions for the affective path make a small model avoid
  personal questions, and EDP accepts only strictly valid JSON from it.
- The Service offers no access control to the browser client.
- `1MMC-TTS-V2.5-I01`'s Spanish voice (`es_ES-davefx-medium`) is male; no
  single-speaker female `es_ES` voice exists at medium or high quality in
  Piper's own catalogue. `es_ES-sharvard-medium` has a female speaker, but as
  speaker index 1 of a multi-speaker file, and `TtsFactory`/`PiperTtsAim` do not
  yet support selecting a speaker index.

## 10. Optional settings of the Service

Beyond the Apps, the Service reads further settings from its configuration; `MasServerConfig.cs` has
the full set, each with its comment. A Service with none of them runs as described above, reading the L3s
from `AIMs/AMDs` and building every AIM itself.

| Setting | Effect |
|---|---|
| `ModelSource: "Fetch"`, `ModelCache` | A model a setting names and the machine lacks is fetched from `Source:<setting>` and checked against `SHA256:<setting>`. |
| `Collections`, `DefaultCollection` | Which Apps this Service offers, and where (`/MPAI/AIFU/c/<name>`). |
| `BearerToken` | Required once `ListenUrl` is not loopback (`127.0.0.1` or `localhost`); the Service refuses to start without one, so that a machine reachable from outside cannot be used by an uninvited caller. |
| `SchemaDirectory`, `AmdDirectory`, `SettingsPath`, `AppDirectory`, `WarmUpDirectory` | Where each is, if not where the installation keeps it. A relative path is taken from the folder of the software. |

### 10.1 One Module's fault does not stop the Service

The Service's start-up loop (`Program.cs`) always tries all five hardcoded
Modules, regardless of `Apps`. Building an AIM can throw - most often a
missing model file - and `ControllerApiRunner.Start` catches that exception and
reports it as that one Module's failure (`<Module>: FAILED - <message>`)
rather than letting it end the process: every other Module still starts. This
matters most for a machine that hosts only some AIMs.

## 11. Who uses a public Thalia

Behind a proxy (RunPod's, for one) the client's host sees every request as coming from the proxy, and the person's
own address is only in a header the proxy adds. `UserAgent/Clients/Browser/Host/UsageLog.cs` writes, for each person
who opens the client (`visit`) and each session they start (`session`), a line in
`<root>/logs/usage-YYYY-MM-DD.jsonl` with the address (read from `CF-Connecting-IP`, `True-Client-IP`, `X-Real-IP` or
`X-Forwarded-For`, in that order), the country the proxy states, and the time; and `GET /MPAI/Usage?days=7` gives the
numbers - visits, sessions, distinct addresses, per day, per address and per country - to whoever presents the
bearer token.

It is **off** unless `appsettings.Production.json` beside the host says so:

```json
{ "Usage": { "Enabled": true, "IpMode": "full", "RetentionDays": 30, "SummaryToken": "<a secret>", "Probe": false } }
```

- `IpMode`: `full`, `truncated` (IPv4 to /24, IPv6 to /48) or `hashed` (a keyed hash: addresses can be counted, not read).
- `RetentionDays`: how long a file is kept; the host deletes older ones.
- `Probe`: for the first 20 requests, writes every header that may hold an address to `usage-probe.log`, to see which one
  the proxy sets. A header a person can send themselves (`X-Forwarded-For`) can be forged; the proxy's own cannot.

**An IP address is personal data.** Whoever runs a public Thalia and switches this on must say so in their privacy
notice: what is kept (the address, the country, the time, that a visit or a session happened), why, and for how long.
Nothing a person says or shows is looked at.