# MPAI Applications - MAC, ACR, MAD, MAT, MPD, AMQ

Reference applications built on the **MPAI-AIF** AI Framework. Each application is
a genuine AIF **Module** - a graph of AI Modules - executed by the AIF
**Controller** and driven by a **User Agent**. They share a common runtime, a
common set of AI Modules (AIMs), and a common Speaking-Avatar library.

Developed by MPAI - Moving Picture, Audio and Data Coding by Artificial
Intelligence (<https://mpai.community>). Contact: secretariat@mpai.community.

Relevant MPAI Technical Specifications:

- **MPAI-AIF** - AI Framework: <https://mpai.community/standards/mpai-aif/>
- **MPAI-MMC** - Multimodal Conversation: <https://mpai.community/standards/mpai-mmc/>
- **MPAI-PAF** - Portable Avatar Format: <https://mpai.community/standards/mpai-paf/>
- **MPAI-OSD** - Object and Scene Description: <https://mpai.community/standards/mpai-osd/>

---

## The applications

| App | Name | Purpose | Guides |
|---|---|---|---|
| **MAC** | Multimodal Access Control | Recognise a person from **face and voice** and grant or deny access; granted only when both modalities agree. | [User](MPAIApps/MmcApps/MacApp/docs/MAC-User.md) / [Developer](MPAIApps/MmcApps/MacApp/docs/MAC-Developer.md) |
| **ACR** | Access Control Registration | **Enrol** a person's face and voice into the gallery MAC reads. | [User](MPAIApps/MmcApps/AcrApp/docs/ACR-User.md) / [Developer](MPAIApps/MmcApps/AcrApp/docs/ACR-Developer.md) |
| **MAD** | Multimodal Anonymous Dialogue | Hold a spoken **conversation** with a Speaking Avatar; no identity, a local LLM composes the replies. | [User](MPAIApps/MmcApps/MadApp/docs/MAD-User.md) / [Developer](MPAIApps/MmcApps/MadApp/docs/MAD-Developer.md) |
| **MAT** | Multimodal Anonymous Translation | Speak in one language; the avatar **translates** and speaks it in another. | [User](MPAIApps/MmcApps/MatApp/docs/MAT-User.md) / [Developer](MPAIApps/MmcApps/MatApp/docs/MAT-Developer.md) |
| **MPD** | Multimodal Personal Status-based Dialogue | Converse, with the machine reading **how you sound** as well as what you say. | [User](MPAIApps/MmcApps/MpdApp/docs/MPD-User.md) / [Developer](MPAIApps/MmcApps/MpdApp/docs/MPD-Developer.md) |
| **AMQ** | Audio-Visual Multimodal Question Answering | Show an image, ask a question, and the avatar **answers**. | [User](MPAIApps/MmcApps/AmqApp/docs/AMQ-User.md) / [Developer](MPAIApps/MmcApps/AmqApp/docs/AMQ-Developer.md) |

Each presents a 3-D **Speaking Avatar** that guides the user by voice.

---

## Architecture

### AIF in brief

- **AIM (AI Module)** - a unit of processing with typed Ports. **A Port is
  addressed by its Data Type and its Port Number**, never by its name: a Module
  may declare two Ports of one Data Type, and a name cannot distinguish them.
- **Module** - a *composite* AIM, defined by an **L3** descriptor (JSON, under
  `AIMs/AMDs/`) listing its sub-AIMs, its boundary Ports and its Topology.
- **Controller** - the runtime. Given an L3 it instantiates each sub-AIM through
  a **provider**, wires them, and executes the graph.
- **User Agent** - the application with its real-world edges. It acquires and
  delivers real-world data - camera, microphone, avatar - and drives the
  Controller. It is *not* part of the Module.
- **Data Types and Qualifiers** - every Object carries a **Qualifier** stating
  what its Data is: the sampling frequency, the precision, the container. An
  Object cannot be constructed without one. Schemas are under `schemas/`.
- **Shared Storage** - a governed store; MAC and ACR use it as the enrolment
  gallery. The framework stamps each write with the Module and the AIM that made
  it, and a writer cannot choose what that record says.

### The pattern

```
User Agent  --drives-->  Controller  --builds & runs-->  Module (sub-AIMs)
   |  (acquire face/voice/speech, present the avatar)          |
   \------------------ boundary inputs / outputs --------------/
```

The User Agent's behaviour is described by a **Workflow Description** - the
`.orch` file under `UAs/Orchestration/`.

### How the applications are composed

| App | Module (L3) | Leaf AIMs |
|---|---|---|
| MAC | `MMC-MAC-V2.5` | `PAF-FIR` (SCRFD+ArcFace), `MMC-SIR` (ECAPA), `OSD-IDR`, `PAF-RSR` |
| ACR | `MMC-ACR-V2.5` | `PAF-EFD`, `MMC-ESD`, `PAF-RSR` |
| MAD | `MMC-MAD-V2.5` | `MMC-ASR` (Whisper), `MMC-EDP` (LLM via Ollama), `PAF-RSR` |
| MAT | `MMC-MAT-V2.5` | `MMC-ASR`, `MMC-TTT` (M2M100), `PAF-RSR` |
| MPD | `MMC-MPD-V2.5` | `MMC-ASR`, `MMC-NLU`, `MMC-ESI`/`MMC-EFI`, `MMC-PSM`, `MMC-EDP`, `PAF-RSR` |
| AMQ | `MMC-AMQ-V2.5` | `MMC-ASR`, `MMC-TIQ` (BLIP), `MMC-TTS` |

`PAF-RSR` (Response and Scene Rendering) is a composite of `PAF-PSD` +
`MMC-TTS` + `PAF-GFD`, shared by every application. Live capture uses native
**Windows Media Capture**; audio capture and delivery use `MMC-SOA` and
`MMC-SOD`.

---

## Repository layout

```
AIF/            AI Framework runtime (Controller, Store, Shared Storage)
AIMs/           AI Modules
  AMDs/         L3 Module descriptors (JSON)
  Core/         shared types (Data Objects, Qualifiers, JSON, paths)
  <family>/     the AIMs (MMC, PAF, OSD, CAE3, CVE ...)
MW/             middleware
  HciApi/       INorthApi - the seam a User Agent is written against
  PortData/     Port-data serialisers and schema validation
  MasClient/    driving a Module over MPAI-MAS
  MasServer/    serving one
  Wdl/  Rca/    reading and executing a Workflow Description
UAs/
  Lib/UaKit/    Speaking-Avatar host, capture and present toolkit
  Orchestration/  the Workflow Descriptions (.orch)
  Assets/       avatar assets
MPAIApps/MmcApps/{MacApp,AcrApp,MadApp,MatApp,MpdApp,AmqApp}/
schemas/        JSON schemas of the AIF Data Types
```

---

## Prerequisites

- **.NET 10 SDK**, Windows (the user interfaces are WPF + WebView2).
- A **webcam** and **microphone** for MAC and ACR; a microphone for the rest.
- For **MAD** and **MPD**: a running local **LLM via Ollama**.
- **Model files are NOT included** (size and licensing). Each application's
  Developer guide lists exactly what it needs.

### Where models go

Place models under `Models/` beside the application root. The settings in
`AIMs/aim-settings.json` are **relative paths**, resolved against the root the
application computes from its own location - so the folder may be placed anywhere
and run. An absolute path still works but binds the installation to one machine.

---

## Build and run

Each application has a build script beside it:

```
MPAIApps\MmcApps\MacApp\MacAppBuild.bat
MPAIApps\MmcApps\AcrApp\AcrAppBuild.bat
MPAIApps\MmcApps\MadApp\MadAppBuild.bat
MPAIApps\MmcApps\MatApp\MatAppBuild.bat
MPAIApps\MmcApps\MpdApp\MpdAppBuild.bat
MPAIApps\MmcApps\AmqApp\AmqAppBuild.bat
```

Each resolves its own location, builds in place, and produces its executable
under `src\bin\Release\net10.0-windows10.0.19041.0\`.

An application resolves its root - to find `AIMs/`, `Models/`, `UAs/` and
`SharedStorage/` - from the executable's location.

### One application on its own

Each application is also offered as a **package**: the application, the projects
it needs, its Module descriptors, the schemas, the avatar assets and its guides,
and nothing belonging to another application. Unzip it anywhere, place the models
under `Models\`, run the build script.

---

## Models

Distributed separately. The Developer guides give, for each model, the settings
key, the file name, the size, the **SHA-256** and the source. Verify a download
with `Get-FileHash <file> -Algorithm SHA256`.

An ONNX model is often two files: a small `.onnx` holding the structure and a
large `.onnx.data` holding the weights. Both are needed.

Families used: InsightFace (SCRFD, ArcFace), SpeechBrain ECAPA-TDNN, Piper,
whisper.cpp, BLIP, M2M100, and an Ollama-served local LLM.

---

## Status

Reference implementations demonstrating MPAI-AIF end to end. Face and speaker
recognition quality depends on enrolment and on lighting and acoustic conditions.
MAD and MPD require a running local LLM. Biometric galleries are user data and are
not part of this repository.

---

## Licence

**BSD 3-Clause License** - `Copyright (c) 2026 MPAI - Moving Picture, Audio and
Data Coding by Artificial Intelligence`. See [`LICENSE`](LICENSE).
