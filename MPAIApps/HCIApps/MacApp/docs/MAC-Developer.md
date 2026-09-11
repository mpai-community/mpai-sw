# MAC — Multimodal Access Control · Developer Guide

MAC is a genuine **MPAI-AIF V3.0 Module** (`MMC-MAC-V2.5`) executed by the
Controller and driven by a thin **User Agent**. This guide covers how MAC is
composed, how the UA drives it, the build closure, and the models and settings it
needs.

---

## 1. Composition (the Module)

The Controller builds `MMC-MAC-V2.5` from its L3 descriptor
(`AIMs/AMDs/1MMC-MAC-V2.5-I01.json`). The application's **provider**
(`MacProvider`) supplies the leaf AIMs the L3 names:

| Sub-AIM | Role | Technology |
| --- | --- | --- |
| `PAF-FIR` | Face recognition | SCRFD detector + ArcFace embeddings, via ONNX Runtime |
| `MMC-SIR` | Speaker recognition | ECAPA-TDNN embeddings, via ONNX Runtime |
| `OSD-IDR` | Identity reconciliation | Reconciles the face and speaker identities; issues the verdict (Response) and its Personal Status |
| `PAF-RSR` | Response & Scene Rendering | Composite: `PAF-PSD` + `MMC-TTS` (Piper) + `PAF-GFD` — the lip-synced Speaking Avatar |

`PAF-FIR` and `MMC-SIR` read the enrolment gallery from **Shared Storage**
(reached through the Controller API, not through ports). The reconciled identity
stays inside the Module; the User Agent consumes only the Response, the spoken
verdict and the avatar's face descriptors.

## 2. Boundary (addressed by data type)

The Module's boundary ports (from its L3 `ExternalPorts`):

- **Inputs:** `FaceObject` (`OSD-BVO-V1.5`), `SpeechObject` (`OSD-BSO-V1.5`).
- **Outputs:** `Response` (`OSD-BTO-V1.5`), `VocalResponse` (`OSD-BSO-V1.5`),
  `FaceDescriptors` (`PAF-FDO-V1.6`).

Acquisition time accompanies each captured object as its own standard member
(`VisualObjectTime` / `SpeechObjectTime`); it is not a separate boundary port.

## 3. How the User Agent drives it

The UA holds two roles only — real-world I/O and orchestration — and drives the
Module through the **North API** (`MW/HciApi`, `NorthApi`), addressing data by
**data type**, never by port name:

1. `StartFlow("MMC-MAC-V2.5")`.
2. Supply the face: `Advance` with `OSD-BVO-V1.5`. The Module runs face
   recognition and **suspends**, needing speech.
3. Supply the speech: `Advance` with `OSD-BSO-V1.5`. Speaker recognition,
   reconciliation and rendering complete.
4. Read outputs **by type**: `OSD-BTO-V1.5` (verdict text → banner),
   `OSD-BSO-V1.5` (spoken verdict → play), `PAF-FDO-V1.6` (avatar).
5. `StopFlow`.

The UA realises the WDL guidebook `UAs/Orchestration/HCI-MAC.orch`. Device
acquisition (webcam via Windows Media Capture, microphone) and delivery (the
WebView2 avatar) live in the UA / `UAs/Lib/UaKit`.

## 4. Build closure

`MacApp` references, transitively:

- **Runtime:** `AIF.Controller`, `AIF.Store`, `AIF.SharedStorage`,
  `AIF.GlobalStorage`.
- **Shared:** `AIMs/Core`, `MW/HciApi`, `UAs/Lib/UaKit`, `AIMs/Gallery`.
- **AIMs:** `PAF-FIR`, `MMC-SIR`, `OSD-IDR` (+ `HCI/IDR`), `OSD/VisualScene`,
  `PAF-PSD`, `MMC-TTS`, `PAF-GFD`, and the device AIMs
  `CAE3/AOA(+.Windows)`, `MMC/SOA`, `MMC/SOD(+.Windows)`, `CVE/VOA(.Windows)`,
  `OSD/TOD`.

Build a single-file executable:

```
MPAIApps\HCIApps\MacApp\MacAppBuild.bat     ->  MacApp.exe
```

Target: **.NET 10**, `win-x64`, WPF + WebView2. The app resolves its root from the
executable's location — the first ancestor containing both `AIMs` and `UAs`
(`MpaiPaths`), so a clone runs in place without configuration.

## 5. Models & prerequisites

Model binaries are distributed **separately**. Place them under `Models/` (the
fallback the code uses) or set explicit paths in `AIMs/aim-settings.json` under
the relevant AIM keys.

| Model | File | Approx. size | Source | Settings key |
| --- | --- | --- | --- | --- |
| SCRFD (face detect) | `scrfd_10g_bnkps.onnx` | 16 MB | InsightFace | `ScrfdModel` |
| ArcFace (face embed) | `glintr100.onnx` | 249 MB | InsightFace | `ArcFaceModel` |
| ECAPA-TDNN (speaker) | `ecapa-tdnn.onnx` | 79 MB | SpeechBrain | `EcapaModel` |
| Piper voice (verdict) | `en_US-amy-medium.onnx` (+ `.json`) | 60 MB | rhasspy/piper-voices (Hugging Face) | `MMC-TTS-V2.5 : Voice:en` |

**Verify every download** with its checksum:

```
Get-FileHash <file> -Algorithm SHA256
```

Record the SHA-256 you obtain and pin it in your deployment notes. MAC needs no
Whisper or LLM (those belong to other applications).

## 6. Settings

`AIMs/aim-settings.json` maps AIM names to their model/tool paths, so the same
binaries run on any machine:

```json
{
  "MMC-TTS-V2.5": { "Voice:en": "D:/…/en_US-amy-medium.onnx" }
}
```

Keys not present fall back to `Models/<file>` under the resolved root. Missing
settings do not fail startup; a missing **model** fails only when the AIM that
needs it first runs.

## 7. The enrolment gallery (Shared Storage)

`PAF-FIR` and `MMC-SIR` match against a gallery held in governed **Shared
Storage** under `SharedStorage/` (key space `MMC-MAC-V2.5`). MAC **reads** the
gallery; it does not enrol. Enrolment (the ACR application) is not part of this
release. If the Shared-Storage gallery is empty and a legacy
`TestData/gallery.json` is present, it is imported once on first run.

## 8. Conformance notes

- The UA↔Controller boundary and the Module topology are addressed by **data type
  (+ port number)**; port names are labels, not the routing key.
- Verdict and identity are decided **inside** the Module (`OSD-IDR`); the UA
  renders the Response and the avatar and does not re-decide identity.
- Boundary ports that repeat a data type carry a **Port Number**; single
  occurrences carry none.

## 9. Licence

**BSD 3-Clause** — see `LICENSE` in this folder and at the repository root.
