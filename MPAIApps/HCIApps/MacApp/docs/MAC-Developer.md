# MAC - Developer Guidelines (Software Architecture)

**MAC** (Multimodal Access Control) is an MPAI-AIF application that recognises a
person from **face and voice** and grants or denies access. This document
describes how it is built, for software developers.

## 1. MPAI-AIF in brief

An **AIF** application is organised around a small number of standard notions:

- **AIM (AI Module):** a unit of processing with typed input and output ports.
  What matters at a port is the **data type**, not the port name.
- **Module (AIW):** a *composite* AIM - a graph of sub-AIMs wired together by
  data type. A Module is defined by an **L3** descriptor (a JSON file, here in
  `AIMs/AMDs/`) that lists its sub-AIMs, its boundary input/output ports and the
  internal topology.
- **Controller:** the runtime. Given a Module's L3, it instantiates each sub-AIM
  (through a *provider*), wires them, and executes the graph. It exposes
  `MPAI_AIFU_MODULE_Start / RunAsync / ResumeAsync / Stop` to the User Agent.
- **User Agent (UA):** the application "brain with I/O limbs". It acquires and
  delivers real-world data (camera, microphone, the avatar), and it
  **orchestrates** - it tells the Controller what to do and in which order. The
  UA is *not* part of the Module.
- **Shared Storage:** a governed key/value area (here `SharedStorage/`) used, for
  MAC, as the enrolment **gallery** of subjects.
- **Data types & qualifiers:** every port carries a typed object with a
  *qualifier* (format, attributes...). Only data types matter across a port.

## 2. The pattern used here

```
User Agent  --drives-->  Controller  --builds & runs-->  Module (sub-AIMs)
   |  (acquire face/voice, present avatar)                     |
   \------------ boundary inputs / outputs --------------------/
```

- The UA drives the Module **only** through the Controller (`MODULE_Start`,
  `RunAsync`, `Stop`, boundary read/write).
- A **provider** (a small switch class) constructs each sub-AIM the L3 names.
- The UA's behaviour is described by a **WDL** guidebook, the `.orch` file
  (`UAs/Orchestration/HCI-MAC.orch`), which the UA code realises.

## 3. The MAC Module - `MMC-MAC-V2.5`

L3: `AIMs/AMDs/1MMC-MAC-V2.5-I01.json`. Sub-AIMs (leaves the provider builds):

| Sub-AIM | Role | Engine |
|---|---|---|
| `PAF-FIR-V1.6` | Face Recognition | SCRFD (detect) + ArcFace (embed) |
| `MMC-SIR-V2.5` | Speaker Recognition | ECAPA-TDNN |
| `OSD-IDR-V1.5` | ID Reconciliation | reconciles Face ID + Speaker ID |
| `PAF-RSR-V1.6` | Response & Scene Rendering (composite) | drives the avatar |
| - `PAF-PSD`, `MMC-TTS`, `PAF-GFD` | RSR leaves | personal-status->face, text->speech, gesture/face descriptors |

**Boundary in:** `FaceObject` (OSD-BVO) + `FaceTime` (OSD-STM), then
`SpeechObject` (OSD-BSO) + `SpeechTime` (OSD-STM).
**Boundary out:** `UserID` (OSD-IID), `VocalResponse` (OSD-BSO),
`FaceDescriptors` (PAF-FDO).

**Reconciliation rule (OSD-IDR):** access is granted only when the **face and
voice legs agree** on the same subject; one modality alone, or disagreement,
yields the coarse "person" identity -> *not identified*.

The **gallery** is read from Shared Storage scope `"MMC-MAC-V2.5"`. FIR/SIR
compare a live probe embedding against enrolled subjects by cosine similarity.

## 4. The User Agent

`MPAIApps/HCIApps/MacApp/src/` - namespace `HciMac`.

- `MainWindow.xaml.cs` - realises `HCI-MAC.orch`: `MODULE_Start("MMC-MAC-V2.5")`
  -> speak *"...Look at the camera."* (a one-shot **RSR** render) -> capture a webcam
  frame -> write `FaceObject/FaceTime` -> `RunAsync` -> the Module **suspends** for
  speech -> speak *"Speak your passphrase."* -> capture microphone ->
  `ResumeAsync` with `SpeechObject/SpeechTime` -> read `UserID` -> present verdict
  -> `Stop`.
- `MacProvider.cs` - the switch: builds FIR/SIR/IDR/PSD/TTS/GFD.
- Real-world limbs come from `UAs/Lib/UaKit` (`AvatarUaHost`): the WebView 3-D
  avatar, microphone capture, and `PresentAsync` (speak + animate).

**Face capture is tagged `VisualObjectType = "Face"`** at acquisition (the app
knows it is acquiring a face); FIR acts on face-typed visual objects.

Visual acquisition uses **native Windows Media Capture** (no OpenCV).

## 5. Files this app needs (build closure)

- **App:** `MPAIApps/HCIApps/MacApp/*`
- **Framework (AIF):** `AIF/V3.0/src/{AIF.Controller, AIF.Store, AIF.SharedStorage, AIF.GlobalStorage}`
- **UA library:** `UAs/Lib/UaKit`; middleware `MW/HciApi` (SpeakingAvatar)
- **AIMs:** `AIMs/Core`, and the leaves `PAF/V1.6/FIR`, `MMC/V2.5/SIR`,
  `OSD/V1.5/IDR`, `PAF/V1.6/PSD`, `MMC/V2.5/TTS`, `PAF/V1.6/GFD`; audio devices
  `CAE3/V1.0/AOA(.Windows)`, `MMC/V2.5/SOD(.Windows)`, visual `CVE/V1.0/VOA.Windows`
- **L3s:** `AIMs/AMDs/1MMC-MAC-V2.5-I01.json` + the sub-AIM AMDs
- **Orchestration:** `UAs/Orchestration/HCI-MAC.orch`
- **Schemas:** the JSON schemas under `schemas/` reachable from MAC's data types
- **Settings:** `AIMs/aim-settings.json` - `MMC-TTS-V2.5` (Piper voice),
  `MMC-SOA-V2.5` (capture duration), and the FIR/SIR model settings.
- **Models (fetched separately):** SCRFD `scrfd_10g_bnkps.onnx`, ArcFace
  `glintr100.onnx`, ECAPA `ecapa-tdnn.onnx`, Piper voice `en_US-amy-medium`.

## 6. Build & run

```
D:\BI\MPAIApps\HCIApps\MacApp\MacAppBuild.bat   # produces MacApp.exe
D:\BI\MPAIApps\HCIApps\MacApp\MacApp.exe
```

The application root is resolved at runtime from the executable location
(`MpaiPaths.FindRoot`), so a single-file build finds its `AIMs/`, `Models/`,
`UAs/` and `SharedStorage/` alongside the deployment.

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
