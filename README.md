# MPAI HCI Applications - MAC, ACR, MAD, MAT

Reference **Human-CAV Interaction (HCI)** applications built on the
**MPAI-AIF** AI Framework. Each application is a genuine AIF **Module** (a graph
of AI Modules) executed by the AIF **Controller** and driven by a **User Agent**.
The applications share a common runtime, a common set of AI Modules (AIMs), and a
common Speaking-Avatar user-interface library.

Developed by MPAI - Moving Picture, Audio and Data Coding by Artificial
Intelligence (<https://mpai.community>). Contact: secretariat@mpai.community.

Relevant MPAI Technical Specifications:

- **MPAI-AIF** - AI Framework (Controller, AIMs, AIWs/Modules, User Agent, Store): <https://mpai.community/standards/mpai-aif/>
- **MPAI-MMC** - Multimodal Conversation (ASR, EDP dialogue, TTS, SIR/ESD speaker, SOA/SOD): <https://mpai.community/standards/mpai-mmc/>
- **MPAI-PAF** - Portable Avatar Format (RSR rendering, PSD, GFD, FIR/EFD face): <https://mpai.community/standards/mpai-paf/>
- **MPAI-OSD** - Object and Scene Description (IDR reconciliation, visual scene, time): <https://mpai.community/standards/mpai-osd/>

---

## The applications

| App | Name | Purpose | Guidelines |
|---|---|---|---|
| **MAC** | Multimodal Access Control | Recognise a person from **face and voice** and grant or deny access; access is granted only when both modalities agree. | [User](MPAIApps/HCIApps/MacApp/docs/MAC-User.md) / [Developer](MPAIApps/HCIApps/MacApp/docs/MAC-Developer.md) |
| **ACR** | Access Control Registration | **Enrol** a new person's face and voice into the gallery that MAC reads. | [User](MPAIApps/HCIApps/AcrApp/docs/ACR-User.md) / [Developer](MPAIApps/HCIApps/AcrApp/docs/ACR-Developer.md) |
| **MAD** | Multimodal Anonymous Dialogue | Hold a spoken **conversation** with a Speaking Avatar; no identity, a local LLM composes the replies. | [User](MPAIApps/HCIApps/MadApp/docs/MAD-User.md) / [Developer](MPAIApps/HCIApps/MadApp/docs/MAD-Developer.md) |
| **MAT** | Multimodal Anonymous Translation | Speak or type in one language; the Speaking Avatar **translates** and speaks it in another. | [User](MPAIApps/HCIApps/MatApp/docs/MAT-User.md) / [Developer](MPAIApps/HCIApps/MatApp/docs/MAT-Developer.md) |

Each application presents a 3-D **Speaking Avatar** that guides the user by voice.

---

## Architecture

### AIF in brief

MPAI-AIF organises an application around a few standard notions:

- **AIM (AI Module)** - a unit of processing with typed input and output ports.
  At a port, **only the data type matters**, not the port name.
- **Module (AIW)** - a *composite* AIM: a graph of sub-AIMs wired by data type.
  A Module is defined by an **L3** descriptor (JSON, under `AIMs/AMDs/`) that
  lists its sub-AIMs, its boundary ports, and the internal topology.
- **Controller** - the runtime. Given a Module's L3 it instantiates each sub-AIM
  through a **provider**, wires them, and executes the graph. It exposes
  `MPAI_AIFU_MODULE_Start / RunAsync / ResumeAsync / Stop` to the User Agent.
- **User Agent (UA)** - the application "brain with I/O limbs". It acquires and
  delivers real-world data (camera, microphone, the avatar) and **orchestrates**
  the Controller. The UA is *not* part of the Module.
- **Data types & qualifiers** - every port carries a typed object with a
  qualifier (format, attributes). The JSON schemas of the data types are under
  `schemas/`.
- **Shared Storage** - a governed store; MAC/ACR use it as the enrolment gallery.

### The pattern used by every app

```
User Agent  --drives-->  Controller  --builds & runs-->  Module (sub-AIMs)
   |  (acquire face/voice/speech, present the avatar)          |
   \------------------ boundary inputs / outputs --------------/
```

- The UA drives the Module only through the Controller.
- A **provider** (a small switch class per app) constructs the leaf AIMs named by
  the Module's L3.
- The UA's behaviour is described by a **WDL** guidebook, the `.orch` file under
  `UAs/Orchestration/`, which the UA code realises.

### How the three apps are composed

| App | Module (L3) | Leaf AIMs (via the app's provider) |
|---|---|---|
| MAC | `MMC-MAC-V2.5` | `PAF-FIR` (SCRFD+ArcFace face), `MMC-SIR` (ECAPA voice), `OSD-IDR` (reconcile), `PAF-RSR` -> `PAF-PSD` + `MMC-TTS` + `PAF-GFD` (avatar) |
| ACR | `MMC-ACR-V2.5` | `PAF-EFD` (face descriptors), `MMC-ESD` (speech descriptors), `PAF-RSR` (avatar prompts) |
| MAD | `MMC-MAD-V2.5` | `MMC-ASR` (Whisper speech->text), `MMC-EDP` (local LLM via Ollama), `PAF-RSR` (avatar reply) |
| MAT | `MMC-MAT-V2.5` | `MMC-ASR` (Whisper speech->text), `MMC-TTT` (M2M100 translation), `PAF-RSR` (avatar speaks the translation) |

`PAF-RSR` (Response and Scene Rendering) is a composite realised by its leaves
`PAF-PSD` + `MMC-TTS` + `PAF-GFD`; it is shared by all three apps. Live capture
uses native **Windows Media Capture**; audio capture/delivery use `MMC-SOA` /
`MMC-SOD`.

---

## Repository layout

```
AIF/            AI Framework runtime (Controller, Store, Shared/Global Storage)
AIMs/           AI Modules
  AMDs/         L3 Module descriptors (JSON)
  Core/         shared types (data objects, qualifiers, JSON, paths)
  <family>/     the AIMs (MMC, PAF, OSD, CAE3, CVE ...)
MW/HciApi/      HCI middleware (Speaking Avatar API, plug-in provider host)
UAs/
  Lib/UaKit/    Speaking-Avatar host, capture/present toolkit
  Orchestration/  the WDL .orch guidebooks (HCI-MAC/ACR/MAD)
  Assets/       avatar assets (glb, viewer HTML)
MPAIApps/HCIApps/{MacApp,AcrApp,MadApp,MatApp}/   the applications (src + docs + build)
schemas/        JSON schemas of the AIF data types
```

---

## Prerequisites

- **.NET 10 SDK**, Windows (the UIs are WPF + WebView2).
- A **webcam** and **microphone** for MAC and ACR.
- For **MAD**: a running local **LLM via Ollama** and the **Whisper** CLI + model
  (see the MAD Developer guide).
- **Model files are NOT included** in this repository (size and licensing). Each
  application's *Models & Prerequisites* section lists exactly which models it
  needs, with file names, sizes, **SHA-256** and sources; obtain them separately
  and place them under `Models/` (or set the paths in `AIMs/aim-settings.json`).

---

## Build & run

Each application has a build script that produces a single-file executable:

```
MPAIApps\HCIApps\MacApp\MacAppBuild.bat     ->  MacApp.exe
MPAIApps\HCIApps\AcrApp\AcrAppBuild.bat     ->  AcrApp.exe
MPAIApps\HCIApps\MadApp\MadAppBuild.bat     ->  MadApp.exe
```

An application resolves its root (to find `AIMs/`, `Models/`, `UAs/`,
`SharedStorage/`) from the executable's location. For the full per-application
build closure (which projects, L3s, schemas and models are required), see that
application's **Developer** guide. MAD additionally requires Ollama running with
the configured model.

---

## Models

Model binaries are distributed separately. The per-application Developer guides
give a **Models & Prerequisites** table with, for each model: the settings key
(and fallback path), file name, size, **SHA-256** (the authoritative identity -
verify a downloaded file with `Get-FileHash <file> -Algorithm SHA256`), and the
source. Families used: InsightFace (SCRFD detector, ArcFace recogniser),
SpeechBrain ECAPA-TDNN (speaker), Piper (text-to-speech voices),
whisper.cpp (speech-to-text), and an Ollama-served local LLM for dialogue.

---

## Status

These are reference implementations demonstrating MPAI-AIF end to end. Face and
speaker recognition quality depends on enrolment and on lighting/acoustic
conditions. MAD requires a running local LLM; response latency reflects local
inference. Biometric galleries are user data and are not part of this repository.

---

## Licence

**BSD 3-Clause License** - `Copyright (c) 2026 MPAI - Moving Picture, Audio and
Data Coding by Artificial Intelligence`. See [`LICENSE`](LICENSE). Each
application folder also carries a copy of the licence.
