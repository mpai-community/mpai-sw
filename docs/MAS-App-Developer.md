# MAS-App - Developer Guide

How MAS-App is built, and how to change it or add an App. For installing and
using it, see the [User Guide](MAS-App-User.md); for an overview, see
[MPAI Software](MPAI-Software.md).

---

## 1. The parts

| Part | Project | Role |
|---|---|---|
| Service | `MPAIApps/MmcApps/AmqServer/src` | Hosts the Controller and the Modules; speaks MPAI-MAS over HTTPS; offers the Apps. |
| Desktop client | `MPAIApps/RcaApp/src` | WPF. The User Agent: interprets workflows, captures, renders the avatar (WebView2). |
| Browser client | `MPAIApps/RcaWeb/Client` | Blazor WebAssembly. The same User Agent, in a browser. |
| Browser host | `MPAIApps/RcaWeb/Host` | Serves the browser client and the avatar page, and forwards `/MPAI/AIFU/...` to the Service, so the browser sees one origin. |
| Apps | `Apps/MAD`, `AMQ`, `MAT`, `MPD` | Each: a workflow (`.orch`), `app.json`, an icon. |
| MPAI-MAS | `UAs/Orchestration/MPAI-MAS.orch` | The client's own workflow: the container the Apps run in. Not an App. |

Shared software: the Controller (`AIF/`), the AIMs and their L3s (`AIMs/`), the
middleware (`MW/`), the desktop avatar library (`UAs/Lib/UaKit`), the avatar
assets (`UAs/Assets`), the schemas (`schemas/`).

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

`AmqServer` reads its configuration (`mas-server*.json`, path as first argument;
see the User Guide) and builds one Controller whose AIMs come from a composite
of providers - `AmqProvider`, `MadProvider`, `MatProvider`, `MpdProvider` in
`AIMs/Providers`. It preloads `1MMC-AMQ`, `MAD`, `MAT`, `MPD` and `1MAS-APP` so
the first exchange of each App is fast.

The Data Types it carries are those `MW/PortData` can translate between their
internal form and their wire form: `OSD-BSO`, `OSD-BTO`, `OSD-BVO`, `PAF-FDO`,
`OSD-STM`, `OSD-SEL`, `MMC-SUM`. A datum of another Data Type cannot cross.

Optional: a `BearerToken` in the configuration requires callers to present it;
the desktop client sends the one in the environment variable `MPAI_MAS_TOKEN`.

## 6. The clients

Both clients register **sources** (acquire) and **presenters** (present) with a
`DeviceRegistry`, and run workflows with the interpreter.

- **Desktop** (`RcaApp`): sources are the microphone with voice activity
  detection (via UaKit), the text box, the App list, the language picker, a
  file or the webcam; the avatar is `UAs/Assets/cav-webview.html` in WebView2.
  `MPAI_MAS_SERVER` sets the Service address (default `https://localhost:5005/`).
- **Browser** (`RcaWeb/Client`): the same sources and presenters, with the
  capture in `wwwroot/js/rca.js` (the microphone runs from Start and keeps the
  last second heard; typing ends listening). A browser never lets WebAssembly
  block, so this client has its own asynchronous North API client
  (`Mas/AsyncNorthApi.cs`) and an asynchronous copy of the interpreter
  (`Wdl/AsyncWorkflowInterpreter.cs`), otherwise identical to `MW/Rca`'s.
- **Host** (`RcaWeb/Host`): serves the avatar page with its asset addresses and
  messaging adapted for a browser as it is sent, and forwards `/MPAI/AIFU/...`.

## 7. Adding an App

1. **The Module:** write its L3 in `AIMs/AMDs/`, following section 3. Give it a
   Text Port for spoken prompts and, if it answers with the avatar, contain RSR.
2. **Its AIMs:** if it needs AIMs no provider builds, add a provider in
   `AIMs/Providers` and add it to the Service's composite.
3. **Its Data Types:** any Data Type that must cross MPAI-MAS needs a translator
   in `MW/PortData`, registered in `PortDataCodecs.Default()`.
4. **The App:** a folder in `Apps/` with its workflow, `app.json` and `icon.svg`.
5. **The Service:** add the App to `Apps` in the configuration, and its Module to
   the preload list in `AmqServer/src/Program.cs`.
6. **Test:** the `Test` program loads every composite L3 and reports which load;
   then run the App through both clients.

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
- The desktop client's speech capture cannot be cancelled once started; when the
  person types instead, it ends on its own and its words are dropped.
- The browser client keeps an asynchronous copy of the interpreter; the two
  should become one.
- The Service offers no access control to the browser client.
- `1MMC-TTS-V2.5-I01`'s Spanish voice (`es_ES-davefx-medium`) is male; no
  single-speaker female `es_ES` voice exists at medium or high quality in
  Piper's own catalogue. `es_ES-sharvard-medium` has a female speaker, but as
  speaker index 1 of a multi-speaker file, and `TtsFactory`/`PiperTtsAim` do not
  yet support selecting a speaker index.

## 10. The Store, and a Sub-AIM on another machine

Phase 5 (see `M3xxx - Phase 5 - implementing the MPAI-MAS workflow`) let a
Service take its L3s from the MPAI Store, its AIMs from packages, its models
from third parties, and a Sub-AIM from another machine, over MPAI-MAS. Every
piece is additive and off by default: a Service with none of the settings
below runs exactly as it always has, reading `AmdDirectory` and building every
AIM itself.

### 10.1 The Store

`MPAIApps/StoreService` is a separate program, a repository of approved L3s
with a REST API (`MPAIApps/StoreService/README.md`). Run it once, and any
number of Services can point at it:

```powershell
dotnet run --project D:\BI\MPAIApps\StoreService\StoreService.csproj -- --Urls https://localhost:5020 --Root D:\MPAI\Store --Packages D:\MPAI\Packages
```

Submit L3s with `MPAIApps/StoreService/Submit-L3s.ps1` or the `StoreApp`
window; build packages with `AIMs/Build-Packages.ps1`.

**A Service reachable from another machine cannot use HTTPS with the
development certificate**, since a remote machine will not trust it without
installing it first. For a private network (Tailscale, a LAN), the simplest
working arrangement is plain HTTP throughout - the Store, and every Service
that must be reached from elsewhere:

```powershell
dotnet run --project D:\BI\MPAIApps\StoreService\StoreService.csproj -- --Urls http://0.0.0.0:5020 --Root D:\MPAI\Store --Packages D:\MPAI\Packages
```

Every Service's `StoreUrl` (below) must then use `http://`, not `https://`,
including a Service on the same machine as the Store - a scheme mismatch is a
plain connection failure, not a certificate warning, and the Service falls
back to its last-known L3 cache without saying why in those words.

### 10.2 A Service's own configuration

These settings go in a Service's `mas-server-*.json` (see
`MasServerConfig.cs` for the full set):

| Setting | Effect |
|---|---|
| `L3Source: "Store"`, `StoreUrl`, `L3Cache` | L3s come from the Store instead of `AmdDirectory`. |
| `AimSource: "Packages"`, `PackageCache` | AIMs are built from the packages their L3s name, falling back to the compiled providers for any package missing or built for another machine. |
| `ModelSource: "Fetch"`, `ModelCache` | A model a setting names and the machine lacks is fetched from `Source:<setting>` and checked against `SHA256:<setting>`. |
| `Collections`, `DefaultCollection` | Which Apps this Service offers, and where (`/MPAI/AIFU/c/<name>`). |
| `RemoteAims`, `RemoteToken` | A Sub-AIM this Service does not build itself; see 10.3. |
| `BearerToken` | Required once `ListenUrl` is not loopback (`127.0.0.1` or `localhost`); the Service refuses to start without one, so that a machine reachable from outside cannot be used by an uninvited caller. |

### 10.3 A Sub-AIM on another machine

MPAI-MAS lets a Sub-AIM run on its own machine (`Identifier.Relation` other
than `Internal`), reached over MPAI-MAS through a proxy
(`Mpai.Mas.Client.RemoteAim`). What a Remote Client starts may itself be a
basic AIM - action 6 starts "the AIM selected" - and the Controller and
executor build and run that AIM directly, with no invented containing Module.

**The machine that runs the Sub-AIM** (call it the Sub-AIM's Service) offers
only that AIM, with a token, listening on every interface so it can be
reached:

```json
{
  "ListenUrl": "http://0.0.0.0:5006/",
  "L3Source": "Store", "StoreUrl": "http://<main machine>:5020/",
  "L3Cache": "D:\\MPAI\\SCI\\L3",
  "SettingsPath": "D:\\BI\\AIMs\\aim-settings.json",
  "AppDirectory": "D:\\BI\\Apps", "Apps": [],
  "BearerToken": "<a shared secret>"
}
```

`Apps` is empty: this machine's job is the one AIM, not a composite Module,
and today's startup loop always tries to build all five hardcoded Modules
regardless of `Apps` (see 10.4) - so a model this machine lacks for an
unrelated Module (BLIP for AMQ, say) does not need to be present, only not
fatal, which 10.4 now ensures.

**The machine whose Module uses the Sub-AIM** names where it is:

```json
{
  "RemoteAims": { "1MMC-EDP-V2.5-I01": "http://<Sub-AIM's machine>:5006/" },
  "RemoteToken": "<the same shared secret>"
}
```

**Every Data Type the Sub-AIM's Ports carry must have a wire translator** in
`MW/PortData`, registered in `PortDataCodecs.Default()` - the same
requirement as 7.3 for a new App. `MMC-SUM-V2.5` and `MMC-EPS-V2.5` (Entity
Personal Status, needed for any AIM MPD uses remotely) exist today; a further
AIM would need whatever its own Ports carry.

**Firewall:** the Sub-AIM's port (`5006` above) must accept inbound
connections from the other machine. On Windows, from an elevated PowerShell:

```powershell
New-NetFirewallRule -DisplayName "MPAI EDP 5006" -Direction Inbound -Protocol TCP -LocalPort 5006 -Action Allow
```

A firewall silently dropping the connection looks identical to the Sub-AIM's
process being down: the caller's log shows a plain connection timeout
(`AIF] <AIM>: threw, ... A connection attempt failed because the connected
party did not properly respond ...`), not a refusal. Check the rule before
suspecting the process.

**Tested:** MAD and MPD, each with EDP as a remote AIM - locally (two
Services on one machine) and, for MAD, across two physical machines over
Tailscale - with the conversation's memory (`MMC-SUM-V2.5`) correctly
accumulating turn over turn on the machine that does not hold it locally.

### 10.4 One Module's fault does not stop the Service

The Service's start-up loop (`Program.cs`) always tries all five hardcoded
Modules, regardless of `Apps`. Building an AIM can throw - most often a
missing model file - and `NorthApiRunner.Start` catches that exception and
reports it as that one Module's failure (`<Module>: FAILED - <message>`)
rather than letting it end the process: every other Module still starts. This
matters most for a machine that hosts only some AIMs, as 10.3's Sub-AIM
machine does.

