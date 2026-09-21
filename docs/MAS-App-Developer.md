# MAS-App - Developer Guide

How MAS-App is built, and how to change it or add an App. For installing and
using it, see the [User Guide](MAS-App-User.md); for an overview, see
[MPAI Software](../README.md).

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
