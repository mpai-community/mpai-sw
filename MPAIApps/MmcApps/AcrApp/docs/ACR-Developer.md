# ACR - Developer Guidelines (Software Architecture)

**ACR** (Access Control Registration) enrols a new user by capturing their
**face and voice** and storing the resulting descriptors in the Shared-Storage
gallery that **MAC** later reads. This document is for software developers.

## 1. MPAI-AIF in brief
(Same platform as all HCI apps.)
- **AIM** - a typed processing unit. **A port is addressed by its data type**
  (and a *Port Number* only where a type occurs more than once on that AIM);
  the port's *name* is a human label, never an address.
- **Module (AIW)** - a composite AIM defined by an **L3** JSON (`AIMs/AMDs/`):
  sub-AIMs, boundary ports (`Direction` is **`Input`** or **`Output`**), and the
  internal topology.
- **Controller** - builds the Module from its L3 (via a *provider*), wires and
  runs it. It routes **by data type**: a Topology edge's endpoints are resolved
  once, from each AIM's `ExternalPorts` (and the composite's `InternalTypes`),
  to `(DataType, PortNumber)`; nothing downstream reads a port name. Suspends
  when a required boundary input has not been supplied and resumes when it is.
- **North API** (`MW/HciApi`, `NorthApi`) - the UA-facing interface. The UA
  supplies/reads data as `Datum(DataType, PortNumber, json)`; the boundary key on
  the wire is `DataType#PortNumber`. No port names, no application verbs.
- **User Agent (UA)** - acquires/delivers real-world data and **orchestrates**;
  described by a **WDL** `.orch` guidebook.
- **Shared Storage** - the enrolment **gallery**.

> **Names are labels.** Renaming a port's `Name` in an L3 (e.g. `UserName` ->
> `Goofy`, consistently in `ExternalPorts` and the topology edges that reference
> it) changes nothing at runtime, because the loader resolves it to
> `OSD-BTO-V1.5 #2` and the software routes by that. Verified by test.

## 2. The ACR Module - `MMC-ACR-V2.5`
L3: `AIMs/AMDs/1MMC-ACR-V2.5-I01.json`. Sub-AIMs (provider leaves):

| Sub-AIM | Role | Engine |
|---|---|---|
| `PAF-EFD-V1.6` | Entity Face **Description** (enrol) | SCRFD + ArcFace -> Face Descriptors |
| `MMC-ESD-V2.5` | Entity Speech **Description** (enrol) | ECAPA-TDNN -> Speech Descriptors |
| `PAF-RSR-V1.6` (+ `PAF-PSD`, `MMC-TTS`, `PAF-GFD`) | Response & Scene Rendering | drives the avatar prompts |

**Boundary inputs** (by data type; Port Number where a type repeats):
`FaceObject` (OSD-BVO), `FaceTime` (OSD-STM **#1**), `SpeechObject` (OSD-BSO),
`SpeechTime` (OSD-STM **#2**), `PersonalStatus` (MMC-EPS),
`Response` (OSD-BTO **#1**), `UserName` (OSD-BTO **#2**).
**Boundary outputs:** `VocalResponse` (OSD-BSO), `MachineFaceDescriptors` (PAF-FDO).

The **OSD-STM** and **OSD-BTO** types each occur twice at the boundary, so they
carry Port Numbers; this is the case that makes the number mandatory. The UA
supplies each by `(DataType, PortNumber)` - e.g. the name is `(OSD-BTO, 2)` - so
the routing never depends on the label.

### How the name reaches the gallery
`UserName` (OSD-BTO #2) is routed to **both** `PAF-EFD` and `MMC-ESD` (each
declares a `UserName` OSD-BTO input in its own AMD). EFD/ESD read the name **by
data type** and use it as the subject key. EFD writes the **face** half and ESD
the **voice** half of `subject:<name>` into Shared Storage; the write is
**per-subject** (`SubjectGallery.SaveSubject`), so an enrolment touches exactly
one key and merges with any half already stored - it does not rewrite the whole
gallery. The gallery scope is `"MMC-MAC-V2.5"` - the same scope MAC reads.

## 3. The User Agent
`MPAIApps/HCIApps/AcrApp/src/` - provider `AcrProvider.cs`, UA
`MainWindow.xaml.cs` (namespace `AcrApp`), realising `UAs/Orchestration/HCI-ACR.orch`.
It drives the Module through the **North API** (`NorthApi`), addressing data only
by type:

1. `StartFlow("MMC-ACR-V2.5")`.
2. Prompt (a one-shot RSR render) -> user **types a name**.
3. "Look at the camera." -> capture face -> `Advance` with
   `(OSD-BVO)` + `(OSD-STM,1)` + `(OSD-BTO,2)`=name. The flow **suspends** for speech.
4. "Please speak a short sentence." -> capture speech -> `Advance` with
   `(OSD-BSO)` + `(OSD-STM,2)` + `(OSD-BTO,1)`=confirmation + `(MMC-EPS)` + `(OSD-BTO,2)`=name.
   EFD/ESD enrol; RSR renders the confirmation.
5. Read outputs by type: `(OSD-BSO)` spoken confirmation, `(PAF-FDO)` avatar. `StopFlow`.

The name is **typed by the user** (ASR is unreliable for bare names). Face
capture is tagged `VisualObjectType = "Face"`; visual acquisition uses **native
Windows Media Capture** (no OpenCV). Real-world limbs come from `UAs/Lib/UaKit`.

## 4. Files this app needs (build closure)
- **App:** `MPAIApps/HCIApps/AcrApp/*`
- **AIF:** `AIF/V3.0/src/{AIF.Controller, AIF.Store, AIF.SharedStorage, AIF.GlobalStorage}`
- **UA library / North API:** `UAs/Lib/UaKit`, `MW/HciApi` (`NorthApi`)
- **AIMs:** `AIMs/Core`; leaves `PAF/V1.6/EFD`, `MMC/V2.5/ESD`, `PAF/V1.6/PSD`,
  `MMC/V2.5/TTS`, `PAF/V1.6/GFD`; devices `CAE3/V1.0/AOA(.Windows)`,
  `MMC/V2.5/SOD(.Windows)`, `CVE/V1.0/VOA.Windows`
- **L3s:** `1MMC-ACR-V2.5-I01.json` + the sub-AIM AMDs (`1PAF-EFD`, `1MMC-ESD`,
  `1PAF-RSR` and its leaves)
- **Orchestration:** `UAs/Orchestration/HCI-ACR.orch`
- **Schemas:** the JSON schemas reachable from ACR's data types
- **Settings:** `AIMs/aim-settings.json` (TTS voice, SOA duration, EFD/ESD models)
- **Models (fetched separately):** SCRFD, ArcFace `glintr100.onnx`, ECAPA
  `ecapa-tdnn.onnx`, Piper voice `en_US-amy-medium`.

## 5. Build & run
```
D:\BI\MPAIApps\HCIApps\AcrApp\AcrAppBuild.bat
D:\BI\MPAIApps\HCIApps\AcrApp\AcrApp.exe
```
The application root is resolved at runtime from the executable location
(`MpaiPaths`, first ancestor holding both `AIMs` and `UAs`), so a clone runs in
place. ACR **writes** the gallery; **enrolled subjects are not part of the
distribution** (they are personal data and live only under `SharedStorage/`).

## Models & Prerequisites

The application code is in this package; the model files are **not** (they are large
and separately licensed). Obtain each model below, place it at the indicated relative
path under `Models\`, or set the corresponding key in `AIMs\aim-settings.json`.

> **Verification:** the SHA-256 values below identify the exact model files used.
> After downloading, verify each file with `Get-FileHash <file> -Algorithm SHA256`.
> For the InsightFace and SpeechBrain models the exact download URL/version was not
> recorded; the SHA-256 is the authoritative identity - confirm your copy matches.

| Model | Settings key (fallback) | File | Size | SHA-256 | Source |
|---|---|---|---|---|---|
| SCRFD face detector (InsightFace SCRFD-10G-BNKPS) | `ScrfdModel` -> `Models\scrfd_10g_bnkps.onnx` | `scrfd_10g_bnkps.onnx` | 16.14 MB | `5838F7FE053675B1C7A08B633DF49E7AF5495CEE0493C7DCF6697200B85B5B91` | InsightFace model zoo (verify by SHA-256) |
| ArcFace recogniser (InsightFace glintr100 / buffalo_l R100) | `ArcFaceModel` -> `Models\glintr100.onnx` | `glintr100.onnx` | 248.62 MB | `A7933EA5330113B01C9B60351D8F4C33003F145D8470AC5F0E52EE2EFFE25C60` | InsightFace model zoo (verify by SHA-256) |
| ECAPA-TDNN speaker (SpeechBrain spkrec-ecapa-voxceleb, ONNX export) | `EcapaModel` -> `Models\ecapa-tdnn.onnx` | `ecapa-tdnn.onnx` | 79.44 MB | `38FDFC7D2BC9E2925349BAAB9639FCAF4B4C755BE83EE7D616B6E3FBC9D5EAB3` | SpeechBrain (ONNX export; verify by SHA-256) |
| Piper TTS voice | `VoiceModel` / `Voice:en` | `en_US-amy-medium.onnx` | 60.27 MB | `B3A6E47B57B8C7FBE6A0CE2518161A50F59A9CDD8A50835C02CB02BDD6206C18` | Hugging Face `rhasspy/piper-voices` (en_US-amy-medium) |
| Piper voice config | `VoiceConfig` / `VoiceConfig:en` | `en_US-amy-medium.onnx.json` | 0.005 MB | `95A23EB4D42909D38DF73BB9AC7F45F597DBFCDE2D1BF9526FDEAF5466977D77` | Hugging Face `rhasspy/piper-voices` |

Install the Piper voice under `Models\Piper\voices\en_US-amy-medium\`. The Piper executable (`PiperExecutable`) is the Piper Windows release (`piper.exe`).
