# MPAI Reference Software

Reference implementation of the **MPAI-AIF V3.0** AI Framework and the
**Human-CAV Interaction (HCI)** applications. This release publishes one
application — **MAC (Multimodal Access Control)** — on the shared framework,
runtime and AI Modules. Additional applications will be added as they are
completed.

Developed by MPAI — Moving Picture, Audio and Data Coding by Artificial
Intelligence (https://mpai.community). Contact: secretariat@mpai.community.

Relevant MPAI Technical Specifications:

- **MPAI-AIF** — AI Framework (Controller, AIMs, AIWs/Modules, User Agent, Store): https://mpai.community/standards/mpai-aif/
- **MPAI-MMC** — Multimodal Conversation (SIR speaker, SOA/SOD speech I/O, TTS): https://mpai.community/standards/mpai-mmc/
- **MPAI-PAF** — Portable Avatar Format (RSR rendering, PSD, GFD, FIR/EFD face): https://mpai.community/standards/mpai-paf/
- **MPAI-OSD** — Object and Scene Description (IDR reconciliation, visual scene, time): https://mpai.community/standards/mpai-osd/

---

## The governing principle

At any interface, **only the data type matters**. What crosses a port — between
the User Agent and the Controller, or between AI Modules — is identified by its
data type (and a port number where a type repeats), never by a port name or an
application verb. Application meaning lives in the design of a workflow, not in
the machinery that executes it. Every design choice in this codebase follows from
that principle.

## The framework in brief

- **AI Module (AIM)** — a unit of processing with typed input and output ports.
- **Module (AIW)** — a *composite* AIM: a graph of sub-AIMs wired by data type,
  defined by an **L3** descriptor (JSON, under `AIMs/AMDs/`).
- **Controller** — the runtime. Given a Module's L3 it instantiates each sub-AIM
  through a **provider**, wires them, and executes the graph. Both the User Agent
  and the AIMs call the Controller API.
- **User Agent (UA)** — acquires and delivers real-world data (camera,
  microphone, the avatar) and drives the Module through the Controller's
  **North API**, addressing data by type. The UA is *not* part of the Module.
- **Shared Storage** — a governed store the AIMs reach through the Controller API
  (for MAC, the enrolment gallery).

```
User Agent  --North API-->  Controller  --builds & runs-->  Module (sub-AIMs)
   |  (acquire face/voice, present the avatar)                    |
   \----------------- typed boundary data -----------------------/
```

## The applications in this release

| App | Name | Purpose | Guides |
| --- | --- | --- | --- |
| **MAC** | Multimodal Access Control | Recognise a person from **face and voice** and issue a spoken verdict; access is granted only when both modalities agree. | [User](MPAIApps/HCIApps/MacApp/docs/MAC-User.md) / [Developer](MPAIApps/HCIApps/MacApp/docs/MAC-Developer.md) |
| **MAD** | Multimodal Anonymous Dialogue | Hold a spoken **conversation** with a Speaking Avatar; no identity, a local LLM composes the replies. | [User](MPAIApps/HCIApps/MadApp/docs/MAD-User.md) / [Developer](MPAIApps/HCIApps/MadApp/docs/MAD-Developer.md) |

Each application presents a 3-D **Speaking Avatar** that guides the user by voice.

---

## Repository layout

```
AIF/V3.0/src/     the runtime: Controller, Store, Shared / Global Storage
AIMs/             the AI Modules
  Core/           shared data types, qualifiers, JSON, paths
  AMDs/           L3 Module descriptors (JSON)
  <family>/       the AIMs (MMC, PAF, OSD, CAE, CVE)
MW/HciApi/        the North API (type-addressed UA entry) + provider host
UAs/
  Lib/UaKit/      shared User-Agent toolkit: capture + Speaking-Avatar renderer
  Orchestration/  the WDL .orch guidebook (HCI-MAC)
  Assets/         avatar assets (glb, viewer HTML)
MPAIApps/HCIApps/{MacApp,MadApp}/   the applications (src + docs + build)
schemas/          JSON schemas of the AIF data types
```

The layout mirrors the architecture: `AIF/` is the runtime; `AIMs/` the Modules
(with `Core/` for shared types and `AMDs/` for the L3 descriptors); `MW/HciApi`
the North API; `UAs/` the User-Agent side; `MPAIApps/` the application.

## Prerequisites

- **.NET 10 SDK**, Windows (the UI is WPF + WebView2).
- A **webcam** and **microphone**.
- **Model files are NOT included** in this repository (size and licensing). See
  the MAC **Developer** guide for the exact list, with file names, sizes,
  **SHA-256** and sources; place them under `Models/` (or set the paths in
  `AIMs/aim-settings.json`).
- MAC recognises people already present in the **enrolment gallery** (a governed
  Shared-Storage area). The gallery is user data and is not part of this
  repository.

## Build & run

```
MPAIApps\HCIApps\MacApp\MacAppBuild.bat     ->  MacApp.exe
MPAIApps\HCIApps\MadApp\MadAppBuild.bat     ->  MadApp.exe
```

**MAD** additionally requires a running local **LLM via Ollama** and the
**Whisper** speech-to-text CLI + model (see the MAD Developer guide).

An application resolves its root (to find `AIMs/`, `Models/`, `UAs/`,
`SharedStorage/`) from the executable's location — the first ancestor folder that
contains both `AIMs` and `UAs`. A clone unzipped anywhere therefore runs in place.

## Licence

**BSD 3-Clause License** — Copyright (c) 2026 MPAI. See `LICENSE`. The application
folder also carries a copy of the licence.
