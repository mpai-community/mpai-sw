# ACR - Developer Guidelines (Software Architecture)

**ACR** (Access Control Registration) enrols a new user by capturing their
**face and voice** and storing the resulting descriptors in the Shared-Storage
gallery that **MAC** later reads. This document is for software developers.

## 1. MPAI-AIF in brief
(Same platform as all HCI apps.)
- **AIM** - typed processing unit; **only data types matter at a port**.
- **Module (AIW)** - a composite AIM defined by an **L3** JSON (`AIMs/AMDs/`):
  sub-AIMs, boundary ports, topology.
- **Controller** - builds the Module from its L3 (via a *provider*), wires and
  runs it; exposes `MODULE_Start/RunAsync/ResumeAsync/Stop`.
- **User Agent (UA)** - acquires/delivers real-world data and **orchestrates**
  the Controller; described by a **WDL** `.orch` guidebook.
- **Shared Storage** - the enrolment **gallery**.

## 2. The ACR Module - `MMC-ACR-V2.5`
L3: `AIMs/AMDs/1MMC-ACR-V2.5-I01.json`. Sub-AIMs (provider leaves):

| Sub-AIM | Role | Engine |
|---|---|---|
| `PAF-EFD-V1.6` | Entity Face **Description** (enrol) | SCRFD + ArcFace -> Face Descriptors |
| `MMC-ESD-V2.5` | Entity Speech **Description** (enrol) | ECAPA-TDNN -> Speech Descriptors |
| `PAF-RSR-V1.6` (+ `PAF-PSD`, `MMC-TTS`, `PAF-GFD`) | Response & Scene Rendering | drives the avatar prompts |

**Boundary in:** `FaceObject`/`FaceTime`, `SpeechObject`/`SpeechTime`,
`PersonalStatus`, `Response`. **Boundary out:** `FaceDescriptors` (PAF-FDO),
`SpeechDescriptors` (MMC-SDO), `VocalResponse`, `MachineFaceDescriptors`.

The user's **name is a UA-level datum** (typed by the user), used as the gallery
key - it is not a Module port. ACR **writes** the enrolment into Shared-Storage
scope `"MMC-MAC-V2.5"` (the same scope MAC reads).

## 3. The User Agent
`MPAIApps/HCIApps/AcrApp/src/` - provider `AcrProvider.cs`, UA
`MainWindow.xaml.cs`, realising `UAs/Orchestration/HCI-ACR.orch`.

Flow: `MODULE_Start("MMC-ACR-V2.5")` -> speak *"Welcome to the CAV Access Control
Registration Service. Please type your name."* -> user **types a name** -> speak
*"Look at the camera."* -> capture face -> `RunAsync` -> suspend -> speak *"Please
speak a short sentence so I can learn your voice."* -> capture speech ->
`ResumeAsync` -> read the descriptors -> **write the gallery entry**
`subject:<name>` (face + voice embeddings, with capture times) -> present a
closing confirmation -> `Stop`.

Face capture is tagged `VisualObjectType = "Face"`; visual acquisition uses
**native Windows Media Capture** (no OpenCV).

## 4. Files this app needs (build closure)
- **App:** `MPAIApps/HCIApps/AcrApp/*`
- **AIF:** `AIF/V3.0/src/{Controller, Store, SharedStorage, GlobalStorage}`
- **UA library / MW:** `UAs/Lib/UaKit`, `MW/HciApi`
- **AIMs:** `AIMs/Core`; leaves `PAF/V1.6/EFD`, `MMC/V2.5/ESD`, `PAF/V1.6/PSD`,
  `MMC/V2.5/TTS`, `PAF/V1.6/GFD`; devices `CAE3/V1.0/AOA(.Windows)`,
  `MMC/V2.5/SOD(.Windows)`, `CVE/V1.0/VOA.Windows`
- **L3s:** `1MMC-ACR-V2.5-I01.json` + sub-AIM AMDs
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
